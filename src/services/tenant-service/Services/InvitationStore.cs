using MongoDB.Bson;
using MongoDB.Driver;
using TenantService.Models;

namespace TenantService.Services;

public enum InvitationError
{
    None,
    NotFound,
    Expired,
    Revoked,
    AlreadyRedeemed,
    EmailMismatch,
    IdentityInUse,

    /// <summary>The address belongs to an Active or linked user (or to several users) in the tenant.</summary>
    UserExists,

    /// <summary>The address already has a live invitation; resend it instead.</summary>
    InvitationPending,

    /// <summary>The user or invitation changed underneath the operation.</summary>
    Conflict,
}

public sealed record InvitationResult(InvitationError Error, Invitation? Invitation = null)
{
    public bool Succeeded => Error == InvitationError.None;
}

public sealed record RedeemResult(InvitationError Error, string? TenantId = null, string? UserId = null, string? MaskedEmail = null)
{
    public bool Succeeded => Error == InvitationError.None;
}

/// <summary>
/// Invitations and the Invited TenantUsers they activate.
///
/// Consistency model: the TenantUser document is the authority on whether a
/// code can still be redeemed. While a user is Invited it carries the live
/// code's hash and expiry, and redemption is one conditional update on that
/// user (Invited, unlinked, this invitation, this hash, unexpired, this email)
/// that links oid+tid and sets Active in the same write. Revoke and resend
/// change the user first, so each of them and a redemption serialize on that
/// one document: whichever write lands first wins, and the other matches
/// nothing. The Invitation document is the record (status, audit fields) and
/// is updated after the user; if that second write is lost, the user is
/// already in its final state and a retry completes the record. No
/// multi-document transaction is needed, so this works on a standalone mongod
/// and on Cosmos DB for MongoDB, which tenant-service also supports.
/// </summary>
public interface IInvitationStore
{
    /// <summary>
    /// Creates the invitation and its Invited user (or re-invites an existing
    /// unlinked, non-Active user with this address). <paramref name="invitation"/>
    /// carries the code hash and expiry; its <c>UserId</c> is set here.
    /// </summary>
    Task<InvitationResult> CreateAsync(Invitation invitation, CancellationToken ct);

    Task<IReadOnlyList<Invitation>> ListAsync(string tenantId, CancellationToken ct);

    Task<Invitation?> GetAsync(string tenantId, string invitationId, CancellationToken ct);

    /// <summary>Replaces the code (the old one stops working at once) and resets the expiry.</summary>
    Task<InvitationResult> ResendAsync(string tenantId, string invitationId, string newCodeHash, DateTime expiresAt,
        string actor, CancellationToken ct);

    Task<InvitationResult> RevokeAsync(string tenantId, string invitationId, string actor, CancellationToken ct);

    /// <summary>
    /// Redeems the invitation whose code hashes to <paramref name="codeHash"/> for
    /// the Entra identity <paramref name="tid"/>/<paramref name="oid"/> signed in
    /// as <paramref name="email"/>.
    /// </summary>
    Task<RedeemResult> RedeemAsync(string codeHash, string tid, string oid, string email, CancellationToken ct);
}

public sealed class MongoInvitationStore : IInvitationStore
{
    public const string CollectionName = "Invitations";

    /// <summary>Written as updatedBy on users that redemption activates.</summary>
    public const string RedemptionActor = "token-service:invitation";

    private readonly IMongoCollection<Invitation> _invitations;
    private readonly IMongoCollection<TenantUser> _users;
    private readonly ILogger<MongoInvitationStore> _logger;
    private readonly TimeProvider _time;
    private static int _indexesEnsured;

    private static readonly FilterDefinitionBuilder<TenantUser> UserFilter = Builders<TenantUser>.Filter;
    private static readonly FilterDefinitionBuilder<Invitation> InvitationFilter = Builders<Invitation>.Filter;

    public MongoInvitationStore(IMongoDatabase database, ILogger<MongoInvitationStore> logger, TimeProvider? time = null)
    {
        _invitations = database.GetCollection<Invitation>(CollectionName);
        _users = database.GetCollection<TenantUser>("TenantUsers");
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private async Task EnsureIndexesAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _indexesEnsured, 1) == 1)
            return;
        try
        {
            await _invitations.Indexes.CreateManyAsync(new[]
            {
                new CreateIndexModel<Invitation>(
                    Builders<Invitation>.IndexKeys.Ascending(i => i.CodeHash),
                    new CreateIndexOptions { Unique = true }),
                new CreateIndexModel<Invitation>(
                    Builders<Invitation>.IndexKeys.Ascending(i => i.TenantId).Descending(i => i.CreatedAt)),
            }, ct);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            // Not needed for correctness (codes are 256-bit random); retried next time.
            Interlocked.Exchange(ref _indexesEnsured, 0);
            _logger.LogWarning(ex, "Invitation indexes could not be created");
        }
    }

    /// <summary>A user with no Entra link (empty or missing oid).</summary>
    private static FilterDefinition<TenantUser> Unlinked
        => UserFilter.In(u => u.AzureAdObjectId, new[] { string.Empty, null });

    private static FilterDefinition<TenantUser> StatusIsActive
        => UserFilter.Regex(u => u.Status, new BsonRegularExpression("^\\s*active\\s*$", "i"));

    // ── create ──────────────────────────────────────────────────────────

    public async Task<InvitationResult> CreateAsync(Invitation invitation, CancellationToken ct)
    {
        await EnsureIndexesAsync(ct);
        var now = Now;

        var existing = await _users
            .Find(u => u.TenantId == invitation.TenantId && u.EmailNormalized == invitation.EmailNormalized)
            .Limit(2).ToListAsync(ct);

        if (existing.Count > 1)
            return new InvitationResult(InvitationError.UserExists);

        string? supersededInvitationId = null;

        if (existing.Count == 1)
        {
            var user = existing[0];
            if (!string.IsNullOrEmpty(user.AzureAdObjectId) || TenantUserStatus.Is(user.Status, TenantUserStatus.Active))
                return new InvitationResult(InvitationError.UserExists);
            if (TenantUserStatus.Is(user.Status, TenantUserStatus.Invited)
                && !string.IsNullOrEmpty(user.InvitationCodeHash) && user.InvitationExpiresAt > now)
                return new InvitationResult(InvitationError.InvitationPending);

            // Re-invite an unlinked user that is not Active: Invited with an
            // expired or revoked invitation, or Disabled/Locked. Conditional on
            // what was just read, so a concurrent change wins.
            var filter = UserFilter.And(
                UserFilter.Eq(u => u.Id, user.Id),
                UserFilter.Eq(u => u.TenantId, invitation.TenantId),
                Unlinked,
                UserFilter.Not(StatusIsActive),
                UserFilter.Eq(u => u.InvitationId, user.InvitationId));
            var update = Builders<TenantUser>.Update
                .Set(u => u.Status, TenantUserStatus.Invited)
                .Set(u => u.InvitationId, invitation.Id)
                .Set(u => u.InvitationCodeHash, invitation.CodeHash)
                .Set(u => u.InvitationExpiresAt, invitation.ExpiresAt)
                .Set(u => u.DisplayName, invitation.DisplayName)
                .Set(u => u.FirstName, invitation.FirstName)
                .Set(u => u.LastName, invitation.LastName)
                .Set(u => u.Department, invitation.Department)
                .Set(u => u.Roles, invitation.Roles)
                .Set(u => u.UpdatedAt, now)
                .Set(u => u.UpdatedBy, invitation.CreatedBy);
            var result = await _users.UpdateOneAsync(filter, update, cancellationToken: ct);
            if (result.MatchedCount == 0)
                return new InvitationResult(InvitationError.Conflict);

            invitation.UserId = user.Id;
            supersededInvitationId = user.InvitationId;
        }
        else
        {
            var user = new TenantUser
            {
                TenantId = invitation.TenantId,
                Email = invitation.Email,
                EmailNormalized = invitation.EmailNormalized,
                DisplayName = invitation.DisplayName,
                FirstName = invitation.FirstName,
                LastName = invitation.LastName,
                Department = invitation.Department,
                Roles = invitation.Roles,
                Status = TenantUserStatus.Invited,
                InvitationId = invitation.Id,
                InvitationCodeHash = invitation.CodeHash,
                InvitationExpiresAt = invitation.ExpiresAt,
                CreatedAt = now,
                UpdatedAt = now,
                CreatedBy = invitation.CreatedBy,
                UpdatedBy = invitation.CreatedBy,
            };
            await _users.InsertOneAsync(user, cancellationToken: ct);
            invitation.UserId = user.Id;
        }

        invitation.Status = InvitationStatus.Pending;
        invitation.CreatedAt = now;
        await _invitations.InsertOneAsync(invitation, cancellationToken: ct);

        if (!string.IsNullOrEmpty(supersededInvitationId) && supersededInvitationId != invitation.Id)
        {
            // The earlier invitation's hash is no longer on the user, so it can
            // no longer be redeemed; record that it was superseded.
            await _invitations.UpdateOneAsync(
                InvitationFilter.And(
                    InvitationFilter.Eq(i => i.Id, supersededInvitationId),
                    InvitationFilter.Eq(i => i.Status, InvitationStatus.Pending)),
                Builders<Invitation>.Update
                    .Set(i => i.Status, InvitationStatus.Revoked)
                    .Set(i => i.RevokedAt, now)
                    .Set(i => i.RevokedBy, invitation.CreatedBy),
                cancellationToken: ct);
        }

        return new InvitationResult(InvitationError.None, invitation);
    }

    // ── read ────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<Invitation>> ListAsync(string tenantId, CancellationToken ct)
        => await _invitations.Find(i => i.TenantId == tenantId)
            .SortByDescending(i => i.CreatedAt).Limit(500).ToListAsync(ct);

    public async Task<Invitation?> GetAsync(string tenantId, string invitationId, CancellationToken ct)
        => await _invitations.Find(i => i.Id == invitationId && i.TenantId == tenantId).FirstOrDefaultAsync(ct);

    /// <summary>The Invited, unlinked user that still points at this invitation.</summary>
    private static FilterDefinition<TenantUser> InvitedUserOf(Invitation inv)
        => UserFilter.And(
            UserFilter.Eq(u => u.Id, inv.UserId),
            UserFilter.Eq(u => u.TenantId, inv.TenantId),
            UserFilter.Eq(u => u.Status, TenantUserStatus.Invited),
            UserFilter.Eq(u => u.InvitationId, inv.Id),
            Unlinked);

    // ── resend ──────────────────────────────────────────────────────────

    public async Task<InvitationResult> ResendAsync(string tenantId, string invitationId, string newCodeHash,
        DateTime expiresAt, string actor, CancellationToken ct)
    {
        await EnsureIndexesAsync(ct);
        var inv = await GetAsync(tenantId, invitationId, ct);
        if (inv == null)
            return new InvitationResult(InvitationError.NotFound);
        if (inv.Status == InvitationStatus.Redeemed)
            return new InvitationResult(InvitationError.AlreadyRedeemed, inv);
        if (inv.Status == InvitationStatus.Revoked)
            return new InvitationResult(InvitationError.Revoked, inv);

        var now = Now;

        // 1. The user: from here on only the new code can match it.
        var userUpdate = await _users.UpdateOneAsync(InvitedUserOf(inv),
            Builders<TenantUser>.Update
                .Set(u => u.InvitationCodeHash, newCodeHash)
                .Set(u => u.InvitationExpiresAt, expiresAt)
                .Set(u => u.UpdatedAt, now)
                .Set(u => u.UpdatedBy, actor),
            cancellationToken: ct);
        if (userUpdate.MatchedCount == 0)
            return await ExplainUserMismatchAsync(inv, ct);

        // 2. The record.
        var updated = await _invitations.FindOneAndUpdateAsync(
            InvitationFilter.And(
                InvitationFilter.Eq(i => i.Id, inv.Id),
                InvitationFilter.Eq(i => i.TenantId, tenantId),
                InvitationFilter.Eq(i => i.Status, InvitationStatus.Pending)),
            Builders<Invitation>.Update
                .Set(i => i.CodeHash, newCodeHash)
                .Set(i => i.ExpiresAt, expiresAt)
                .Set(i => i.ResentAt, now)
                .Set(i => i.ResentBy, actor)
                .Inc(i => i.SendCount, 1),
            new FindOneAndUpdateOptions<Invitation> { ReturnDocument = ReturnDocument.After }, ct);
        return updated == null
            ? new InvitationResult(InvitationError.Conflict)
            : new InvitationResult(InvitationError.None, updated);
    }

    // ── revoke ──────────────────────────────────────────────────────────

    public async Task<InvitationResult> RevokeAsync(string tenantId, string invitationId, string actor, CancellationToken ct)
    {
        var inv = await GetAsync(tenantId, invitationId, ct);
        if (inv == null)
            return new InvitationResult(InvitationError.NotFound);
        if (inv.Status == InvitationStatus.Redeemed)
            return new InvitationResult(InvitationError.AlreadyRedeemed, inv);
        if (inv.Status == InvitationStatus.Revoked)
            return new InvitationResult(InvitationError.None, inv);

        var now = Now;

        // 1. The user: remove the live code, so no redemption can match from now on.
        //    The user stays Invited (it cannot sign in) until it is re-invited or deleted.
        var userUpdate = await _users.UpdateOneAsync(InvitedUserOf(inv),
            Builders<TenantUser>.Update
                .Set(u => u.InvitationCodeHash, null)
                .Set(u => u.InvitationExpiresAt, null)
                .Set(u => u.UpdatedAt, now)
                .Set(u => u.UpdatedBy, actor),
            cancellationToken: ct);
        if (userUpdate.MatchedCount == 0)
        {
            var explained = await ExplainUserMismatchAsync(inv, ct);
            if (explained.Error == InvitationError.AlreadyRedeemed)
                return explained;
            // The user was deleted, or re-invited by a newer invitation: this one
            // is dead either way; record the revocation.
        }

        // 2. The record.
        var updated = await _invitations.FindOneAndUpdateAsync(
            InvitationFilter.And(
                InvitationFilter.Eq(i => i.Id, inv.Id),
                InvitationFilter.Eq(i => i.TenantId, tenantId),
                InvitationFilter.Eq(i => i.Status, InvitationStatus.Pending)),
            Builders<Invitation>.Update
                .Set(i => i.Status, InvitationStatus.Revoked)
                .Set(i => i.RevokedAt, now)
                .Set(i => i.RevokedBy, actor),
            new FindOneAndUpdateOptions<Invitation> { ReturnDocument = ReturnDocument.After }, ct);
        if (updated != null)
            return new InvitationResult(InvitationError.None, updated);

        var current = await GetAsync(tenantId, invitationId, ct);
        return current?.Status switch
        {
            InvitationStatus.Revoked => new InvitationResult(InvitationError.None, current),
            InvitationStatus.Redeemed => new InvitationResult(InvitationError.AlreadyRedeemed, current),
            _ => new InvitationResult(InvitationError.Conflict),
        };
    }

    /// <summary>Why the invited user no longer matches <paramref name="inv"/>.</summary>
    private async Task<InvitationResult> ExplainUserMismatchAsync(Invitation inv, CancellationToken ct)
    {
        var user = await _users.Find(u => u.Id == inv.UserId && u.TenantId == inv.TenantId).FirstOrDefaultAsync(ct);
        if (user != null && user.InvitationId == inv.Id && !string.IsNullOrEmpty(user.AzureAdObjectId))
        {
            // Redeemed, but the record was not updated yet (or that write was lost).
            await MarkRedeemedAsync(inv, user.AzureAdTenantId, user.AzureAdObjectId, ct);
            return new InvitationResult(InvitationError.AlreadyRedeemed, inv);
        }
        return new InvitationResult(InvitationError.Conflict);
    }

    // ── redeem ──────────────────────────────────────────────────────────

    public async Task<RedeemResult> RedeemAsync(string codeHash, string tid, string oid, string email, CancellationToken ct)
    {
        var now = Now;
        var signedInEmail = InvitationCodes.NormalizeEmail(email);

        var inv = await _invitations.Find(i => i.CodeHash == codeHash).FirstOrDefaultAsync(ct);
        if (inv == null)
            return new RedeemResult(InvitationError.NotFound);

        if (inv.Status == InvitationStatus.Redeemed)
        {
            // The same person again (a double click, a retry): they are already in.
            return inv.RedeemedBy != null && Same(inv.RedeemedBy.Tid, tid) && Same(inv.RedeemedBy.Oid, oid)
                ? new RedeemResult(InvitationError.None, inv.TenantId, inv.UserId)
                : new RedeemResult(InvitationError.AlreadyRedeemed);
        }
        if (inv.Status == InvitationStatus.Revoked)
            return new RedeemResult(InvitationError.Revoked);
        if (inv.Status != InvitationStatus.Pending)
            return new RedeemResult(InvitationError.NotFound);
        if (inv.ExpiresAt <= now)
            return new RedeemResult(InvitationError.Expired);
        if (string.IsNullOrEmpty(signedInEmail) || signedInEmail != inv.EmailNormalized)
            return new RedeemResult(InvitationError.EmailMismatch, MaskedEmail: InvitationCodes.MaskEmail(inv.Email));

        // This identity must not already be someone else in this tenant.
        var linkedElsewhere = await _users.Find(UserFilter.And(
                UserFilter.Eq(u => u.TenantId, inv.TenantId),
                UserFilter.Eq(u => u.AzureAdObjectId, oid),
                UserFilter.In(u => u.AzureAdTenantId, new[] { tid, string.Empty, null }),
                UserFilter.Ne(u => u.Id, inv.UserId)))
            .AnyAsync(ct);
        if (linkedElsewhere)
            return new RedeemResult(InvitationError.IdentityInUse);

        // The one write that decides: every condition is re-checked on the user
        // document itself, and the link and activation happen together.
        var link = await _users.UpdateOneAsync(
            UserFilter.And(
                InvitedUserOf(inv),
                UserFilter.Eq(u => u.InvitationCodeHash, codeHash),
                UserFilter.Gt(u => u.InvitationExpiresAt, now),
                UserFilter.Eq(u => u.EmailNormalized, inv.EmailNormalized)),
            Builders<TenantUser>.Update
                .Set(u => u.AzureAdObjectId, oid)
                .Set(u => u.AzureAdTenantId, tid)
                .Set(u => u.Status, TenantUserStatus.Active)
                .Set(u => u.InvitationCodeHash, null)
                .Set(u => u.InvitationExpiresAt, null)
                .Set(u => u.UpdatedAt, now)
                .Set(u => u.UpdatedBy, RedemptionActor),
            cancellationToken: ct);

        if (link.MatchedCount == 0)
            return await ExplainFailedRedemptionAsync(inv, codeHash, tid, oid, signedInEmail, now, ct);

        // Two redemptions by one identity of two invitations in one tenant can
        // pass the check above together; whichever sees the other undoes its own.
        var links = await _users.CountDocumentsAsync(UserFilter.And(
                UserFilter.Eq(u => u.TenantId, inv.TenantId),
                UserFilter.Eq(u => u.AzureAdObjectId, oid),
                UserFilter.In(u => u.AzureAdTenantId, new[] { tid, string.Empty, null })),
            cancellationToken: ct);
        if (links > 1)
        {
            await _users.UpdateOneAsync(
                UserFilter.And(
                    UserFilter.Eq(u => u.Id, inv.UserId),
                    UserFilter.Eq(u => u.AzureAdObjectId, oid),
                    UserFilter.Eq(u => u.AzureAdTenantId, tid),
                    UserFilter.Eq(u => u.UpdatedBy, RedemptionActor)),
                Builders<TenantUser>.Update
                    .Set(u => u.AzureAdObjectId, string.Empty)
                    .Set(u => u.AzureAdTenantId, string.Empty)
                    .Set(u => u.Status, TenantUserStatus.Invited)
                    .Set(u => u.InvitationCodeHash, codeHash)
                    .Set(u => u.InvitationExpiresAt, inv.ExpiresAt)
                    .Set(u => u.UpdatedAt, Now),
                cancellationToken: ct);
            return new RedeemResult(InvitationError.IdentityInUse);
        }

        await MarkRedeemedAsync(inv, tid, oid, ct);
        return new RedeemResult(InvitationError.None, inv.TenantId, inv.UserId);
    }

    private async Task<RedeemResult> ExplainFailedRedemptionAsync(
        Invitation inv, string codeHash, string tid, string oid, string signedInEmail, DateTime now, CancellationToken ct)
    {
        var user = await _users.Find(u => u.Id == inv.UserId && u.TenantId == inv.TenantId).FirstOrDefaultAsync(ct);
        if (user == null)
            return new RedeemResult(InvitationError.Revoked);

        if (!string.IsNullOrEmpty(user.AzureAdObjectId) || !TenantUserStatus.Is(user.Status, TenantUserStatus.Invited))
        {
            if (user.InvitationId == inv.Id && Same(user.AzureAdObjectId, oid) && Same(user.AzureAdTenantId, tid))
            {
                // This identity won (a concurrent attempt of its own, or an earlier
                // attempt whose record update was lost): complete the record.
                await MarkRedeemedAsync(inv, tid, oid, ct);
                return new RedeemResult(InvitationError.None, inv.TenantId, inv.UserId);
            }
            if (user.InvitationId == inv.Id && !string.IsNullOrEmpty(user.AzureAdObjectId))
                await MarkRedeemedAsync(inv, user.AzureAdTenantId, user.AzureAdObjectId, ct);
            return new RedeemResult(InvitationError.AlreadyRedeemed);
        }

        if (user.InvitationId != inv.Id || user.InvitationCodeHash != codeHash)
            return new RedeemResult(InvitationError.Revoked);
        if (user.InvitationExpiresAt == null || user.InvitationExpiresAt <= now)
            return new RedeemResult(InvitationError.Expired);
        if (user.EmailNormalized != signedInEmail)
            return new RedeemResult(InvitationError.EmailMismatch, MaskedEmail: InvitationCodes.MaskEmail(inv.Email));
        return new RedeemResult(InvitationError.Conflict);
    }

    private Task MarkRedeemedAsync(Invitation inv, string tid, string oid, CancellationToken ct)
        => _invitations.UpdateOneAsync(
            InvitationFilter.And(
                InvitationFilter.Eq(i => i.Id, inv.Id),
                InvitationFilter.Eq(i => i.Status, InvitationStatus.Pending)),
            Builders<Invitation>.Update
                .Set(i => i.Status, InvitationStatus.Redeemed)
                .Set(i => i.RedeemedAt, Now)
                .Set(i => i.RedeemedBy, new InvitationRedeemer { Tid = tid, Oid = oid }),
            cancellationToken: ct);

    private static bool Same(string? a, string? b)
        => !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
