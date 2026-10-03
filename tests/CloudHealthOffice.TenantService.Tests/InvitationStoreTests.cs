using CloudHealthOffice.Testing.Mongo;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Driver;
using TenantService.Models;
using TenantService.Services;

namespace CloudHealthOffice.TenantService.Tests;

/// <summary>A clock tests can move.</summary>
public sealed class MutableTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// Invitations against a real mongod: what is stored, and the conditional
/// writes that make redemption, revoke and resend safe against each other.
/// One mongod shared across the collection (MongoRunnerFixture); each test gets its own database.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class InvitationStoreTests : IAsyncLifetime
{
    private const string Tenant = "acme";
    private const string GuestTid = "33333333-3333-3333-3333-333333333333";
    private const string OtherTid = "44444444-4444-4444-4444-444444444444";
    private const string Email = "Pat.Guest@Partner.example";

    private readonly MongoRunnerFixture _mongo;
    private readonly MutableTimeProvider _clock = new();
    private IMongoDatabase _database = null!;
    private MongoInvitationStore _store = null!;

    static InvitationStoreTests()
    {
        ConventionRegistry.Register("CamelCase",
            new ConventionPack { new CamelCaseElementNameConvention() }, _ => true);
    }

    public InvitationStoreTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("invitations");
        _store = new MongoInvitationStore(_database, NullLogger<MongoInvitationStore>.Instance, _clock);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);

    private IMongoCollection<TenantUser> Users => _database.GetCollection<TenantUser>("TenantUsers");
    private IMongoCollection<Invitation> Invitations => _database.GetCollection<Invitation>(MongoInvitationStore.CollectionName);

    private async Task<(Invitation Invitation, string Code)> InviteAsync(string email = Email, string tenant = Tenant)
    {
        var code = InvitationCodes.NewCode();
        var invitation = new Invitation
        {
            TenantId = tenant,
            Email = email,
            EmailNormalized = InvitationCodes.NormalizeEmail(email),
            DisplayName = "Pat Guest",
            Roles = new List<string> { "ClaimsExaminer" },
            CodeHash = InvitationCodes.Hash(code),
            CreatedBy = "admin-1",
            ExpiresAt = _clock.Now.UtcDateTime.AddDays(7),
        };
        var result = await _store.CreateAsync(invitation, default);
        result.Error.Should().Be(InvitationError.None);
        return (result.Invitation!, code);
    }

    private Task<RedeemResult> RedeemAsync(string code, string oid = "oid-guest", string tid = GuestTid, string email = Email)
        => _store.RedeemAsync(InvitationCodes.Hash(code), tid, oid, email, default);

    private Task<TenantUser> UserAsync(string id) => Users.Find(u => u.Id == id).FirstAsync();
    private Task<Invitation> StoredAsync(string id) => Invitations.Find(i => i.Id == id).FirstAsync();

    private async Task<TenantUser> AddUserAsync(string email, string status = "Active", string oid = "", string tid = "")
    {
        var user = new TenantUser
        {
            TenantId = Tenant,
            Email = email,
            EmailNormalized = email.ToLowerInvariant(),
            DisplayName = "Existing",
            Status = status,
            AzureAdObjectId = oid,
            AzureAdTenantId = tid,
        };
        await Users.InsertOneAsync(user);
        return user;
    }

    // ── what is stored ──────────────────────────────────────────────────

    [Fact]
    public async Task Create_StoresOnlyTheCodeHash_AndAnInvitedUnlinkedUser()
    {
        var (invitation, code) = await InviteAsync();

        var raw = await _database.GetCollection<BsonDocument>(MongoInvitationStore.CollectionName)
            .Find(Builders<BsonDocument>.Filter.Empty).SingleAsync();
        raw.ToJson().Should().NotContain(code, "the code itself is never stored");
        raw["codeHash"].AsString.Should().Be(InvitationCodes.Hash(code));
        raw["status"].AsString.Should().Be(InvitationStatus.Pending);
        raw["emailNormalized"].AsString.Should().Be("pat.guest@partner.example");

        var rawUser = await _database.GetCollection<BsonDocument>("TenantUsers")
            .Find(Builders<BsonDocument>.Filter.Empty).SingleAsync();
        rawUser.ToJson().Should().NotContain(code);

        var user = await UserAsync(invitation.UserId);
        user.Status.Should().Be(TenantUserStatus.Invited);
        user.AzureAdObjectId.Should().BeEmpty();
        user.InvitationId.Should().Be(invitation.Id);
        user.InvitationCodeHash.Should().Be(InvitationCodes.Hash(code));
        user.Roles.Should().Equal("ClaimsExaminer");
    }

    [Fact]
    public void Codes_Are256BitBase64Url()
    {
        var code = InvitationCodes.NewCode();
        code.Should().HaveLength(43).And.MatchRegex("^[A-Za-z0-9_-]{43}$");
        InvitationCodes.IsWellFormed(code).Should().BeTrue();
        InvitationCodes.NewCode().Should().NotBe(code);
        InvitationCodes.MaskEmail("pat@acme.com").Should().Be("p***@acme.com");
    }

    // ── redeem ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Redeem_LinksTheIdentity_ActivatesTheUser_AndRecordsTheRedemption()
    {
        var (invitation, code) = await InviteAsync();

        var result = await RedeemAsync(code, email: "pat.guest@partner.EXAMPLE");

        result.Error.Should().Be(InvitationError.None);
        result.TenantId.Should().Be(Tenant);
        result.UserId.Should().Be(invitation.UserId);
        var user = await UserAsync(invitation.UserId);
        user.Status.Should().Be(TenantUserStatus.Active);
        user.AzureAdObjectId.Should().Be("oid-guest");
        user.AzureAdTenantId.Should().Be(GuestTid);
        user.InvitationCodeHash.Should().BeNull();
        var stored = await StoredAsync(invitation.Id);
        stored.Status.Should().Be(InvitationStatus.Redeemed);
        stored.RedeemedBy!.Tid.Should().Be(GuestTid);
        stored.RedeemedBy.Oid.Should().Be("oid-guest");
        stored.RedeemedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Redeem_UnknownCode_IsNotFound()
    {
        await InviteAsync();
        (await RedeemAsync(InvitationCodes.NewCode())).Error.Should().Be(InvitationError.NotFound);
    }

    [Fact]
    public async Task Redeem_Expired_IsRefusedAndChangesNothing()
    {
        var (invitation, code) = await InviteAsync();
        _clock.Now = _clock.Now.AddDays(8);

        (await RedeemAsync(code)).Error.Should().Be(InvitationError.Expired);
        (await UserAsync(invitation.UserId)).Status.Should().Be(TenantUserStatus.Invited);
    }

    [Fact]
    public async Task Redeem_Revoked_IsRefused()
    {
        var (invitation, code) = await InviteAsync();
        (await _store.RevokeAsync(Tenant, invitation.Id, "admin-2", default)).Error.Should().Be(InvitationError.None);

        (await RedeemAsync(code)).Error.Should().Be(InvitationError.Revoked);
        var user = await UserAsync(invitation.UserId);
        user.AzureAdObjectId.Should().BeEmpty();
        user.Status.Should().Be(TenantUserStatus.Invited);
        var stored = await StoredAsync(invitation.Id);
        stored.RevokedBy.Should().Be("admin-2");
        stored.RevokedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Redeem_AlreadyRedeemedByAnotherIdentity_IsRefused_SameIdentityAgainSucceeds()
    {
        var (invitation, code) = await InviteAsync();
        (await RedeemAsync(code)).Error.Should().Be(InvitationError.None);

        (await RedeemAsync(code, oid: "oid-attacker", tid: OtherTid)).Error.Should().Be(InvitationError.AlreadyRedeemed);
        (await RedeemAsync(code)).Error.Should().Be(InvitationError.None, "a retry by the person who redeemed it changes nothing");
        (await UserAsync(invitation.UserId)).AzureAdObjectId.Should().Be("oid-guest");
    }

    [Fact]
    public async Task Redeem_SignedInAsAnotherAddress_IsEmailMismatch_WithTheInvitedAddressMasked()
    {
        var (invitation, code) = await InviteAsync();

        var result = await RedeemAsync(code, email: "someone.else@partner.example");

        result.Error.Should().Be(InvitationError.EmailMismatch);
        result.MaskedEmail.Should().Be("P***@Partner.example");
        (await UserAsync(invitation.UserId)).AzureAdObjectId.Should().BeEmpty();
    }

    [Fact]
    public async Task Redeem_WithNoUsername_IsEmailMismatch()
    {
        var (_, code) = await InviteAsync();
        (await RedeemAsync(code, email: "")).Error.Should().Be(InvitationError.EmailMismatch);
    }

    [Fact]
    public async Task Redeem_IdentityAlreadyLinkedToAnotherUserInTheTenant_IsIdentityInUse()
    {
        await AddUserAsync("other@partner.example", oid: "oid-guest", tid: GuestTid);
        var (invitation, code) = await InviteAsync();

        (await RedeemAsync(code)).Error.Should().Be(InvitationError.IdentityInUse);
        var user = await UserAsync(invitation.UserId);
        user.AzureAdObjectId.Should().BeEmpty();
        user.Status.Should().Be(TenantUserStatus.Invited);
        (await StoredAsync(invitation.Id)).Status.Should().Be(InvitationStatus.Pending);
    }

    [Fact]
    public async Task Redeem_IdentityLinkedInAnotherTenant_IsFine()
    {
        await Users.InsertOneAsync(new TenantUser
        {
            TenantId = "beta", Email = "x@partner.example", EmailNormalized = "x@partner.example",
            AzureAdObjectId = "oid-guest", AzureAdTenantId = GuestTid,
        });
        var (_, code) = await InviteAsync();

        (await RedeemAsync(code)).Error.Should().Be(InvitationError.None);
    }

    [Fact]
    public async Task ConcurrentRedemptionsByDifferentIdentities_OnlyOneWins()
    {
        var (invitation, code) = await InviteAsync();

        // Every attempt uses the right code and address (an attacker directory
        // can mint a token for any address); exactly one may link.
        var attempts = Enumerable.Range(0, 12)
            .Select(i => Task.Run(() => RedeemAsync(code, oid: $"oid-{i}", tid: i % 2 == 0 ? GuestTid : OtherTid)))
            .ToArray();
        var results = await Task.WhenAll(attempts);

        results.Count(r => r.Error == InvitationError.None).Should().Be(1);
        results.Where(r => r.Error != InvitationError.None)
            .Should().OnlyContain(r => r.Error == InvitationError.AlreadyRedeemed);
        var winner = Array.FindIndex(results, r => r.Error == InvitationError.None);
        var user = await UserAsync(invitation.UserId);
        user.AzureAdObjectId.Should().Be($"oid-{winner}");
        (await StoredAsync(invitation.Id)).RedeemedBy!.Oid.Should().Be($"oid-{winner}");
    }

    [Fact]
    public async Task ConcurrentRevokeAndRedeem_NeverBothSucceed()
    {
        for (var i = 0; i < 10; i++)
        {
            var (invitation, code) = await InviteAsync($"pat{i}@partner.example");
            var redeem = Task.Run(() => RedeemAsync(code, oid: $"oid-r{i}", email: $"pat{i}@partner.example"));
            var revoke = Task.Run(() => _store.RevokeAsync(Tenant, invitation.Id, "admin", default));
            await Task.WhenAll(redeem, revoke);

            var redeemed = redeem.Result.Error == InvitationError.None;
            var revoked = revoke.Result.Error == InvitationError.None;
            (redeemed ^ revoked).Should().BeTrue($"round {i}: exactly one of redeem ({redeem.Result.Error}) and revoke ({revoke.Result.Error}) wins");
            var user = await UserAsync(invitation.UserId);
            (user.Status == TenantUserStatus.Active).Should().Be(redeemed);
            (await StoredAsync(invitation.Id)).Status.Should().Be(redeemed ? InvitationStatus.Redeemed : InvitationStatus.Revoked);
        }
    }

    // ── resend ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Resend_InvalidatesTheOldCode_AndResetsTheExpiry()
    {
        var (invitation, oldCode) = await InviteAsync();
        _clock.Now = _clock.Now.AddDays(8); // expired
        var newCode = InvitationCodes.NewCode();

        var resent = await _store.ResendAsync(Tenant, invitation.Id, InvitationCodes.Hash(newCode),
            _clock.Now.UtcDateTime.AddDays(7), "admin-2", default);

        resent.Error.Should().Be(InvitationError.None);
        resent.Invitation!.SendCount.Should().Be(2);
        resent.Invitation.ResentBy.Should().Be("admin-2");
        (await RedeemAsync(oldCode)).Error.Should().Be(InvitationError.NotFound);
        (await RedeemAsync(newCode)).Error.Should().Be(InvitationError.None);
    }

    [Fact]
    public async Task ResendAndRevoke_AfterRedemption_AreAlreadyRedeemed()
    {
        var (invitation, code) = await InviteAsync();
        (await RedeemAsync(code)).Error.Should().Be(InvitationError.None);

        (await _store.ResendAsync(Tenant, invitation.Id, InvitationCodes.Hash(InvitationCodes.NewCode()),
            _clock.Now.UtcDateTime.AddDays(7), "admin", default)).Error.Should().Be(InvitationError.AlreadyRedeemed);
        (await _store.RevokeAsync(Tenant, invitation.Id, "admin", default)).Error.Should().Be(InvitationError.AlreadyRedeemed);
        (await UserAsync(invitation.UserId)).Status.Should().Be(TenantUserStatus.Active);
    }

    [Fact]
    public async Task OtherTenantsInvitation_IsNotFoundForRevokeAndResend()
    {
        var (invitation, _) = await InviteAsync(tenant: "beta");

        (await _store.RevokeAsync(Tenant, invitation.Id, "admin", default)).Error.Should().Be(InvitationError.NotFound);
        (await _store.ResendAsync(Tenant, invitation.Id, "x", _clock.Now.UtcDateTime.AddDays(1), "admin", default))
            .Error.Should().Be(InvitationError.NotFound);
        (await _store.ListAsync(Tenant, default)).Should().BeEmpty();
    }

    // ── create: who may be invited ──────────────────────────────────────

    [Theory]
    [InlineData("Active", "")]
    [InlineData("active", "")]
    [InlineData("Disabled", "oid-linked")]
    public async Task Create_ForAnActiveOrLinkedUser_IsUserExists(string status, string oid)
    {
        await AddUserAsync("pat.guest@partner.example", status, oid, oid.Length > 0 ? GuestTid : "");

        var code = InvitationCodes.NewCode();
        var result = await _store.CreateAsync(new Invitation
        {
            TenantId = Tenant, Email = Email, EmailNormalized = InvitationCodes.NormalizeEmail(Email),
            CodeHash = InvitationCodes.Hash(code), ExpiresAt = _clock.Now.UtcDateTime.AddDays(7),
        }, default);

        result.Error.Should().Be(InvitationError.UserExists);
        (await Invitations.CountDocumentsAsync(Builders<Invitation>.Filter.Empty)).Should().Be(0);
    }

    [Fact]
    public async Task Create_WhileAnInvitationIsPending_IsInvitationPending()
    {
        await InviteAsync();
        var code = InvitationCodes.NewCode();
        var result = await _store.CreateAsync(new Invitation
        {
            TenantId = Tenant, Email = Email, EmailNormalized = InvitationCodes.NormalizeEmail(Email),
            CodeHash = InvitationCodes.Hash(code), ExpiresAt = _clock.Now.UtcDateTime.AddDays(7),
        }, default);

        result.Error.Should().Be(InvitationError.InvitationPending);
    }

    [Fact]
    public async Task Create_AfterRevocation_ReusesTheInvitedUser()
    {
        var (first, firstCode) = await InviteAsync();
        await _store.RevokeAsync(Tenant, first.Id, "admin", default);

        var (second, secondCode) = await InviteAsync();

        second.UserId.Should().Be(first.UserId);
        (await Users.CountDocumentsAsync(Builders<TenantUser>.Filter.Empty)).Should().Be(1);
        (await RedeemAsync(firstCode)).Error.Should().Be(InvitationError.Revoked);
        (await RedeemAsync(secondCode)).Error.Should().Be(InvitationError.None);
    }

    [Fact]
    public async Task Create_ForAnExpiredInvitation_SupersedesTheOldOne()
    {
        var (first, firstCode) = await InviteAsync();
        _clock.Now = _clock.Now.AddDays(8);

        var (_, secondCode) = await InviteAsync();

        (await StoredAsync(first.Id)).Status.Should().Be(InvitationStatus.Revoked);
        (await RedeemAsync(firstCode)).Error.Should().NotBe(InvitationError.None);
        (await RedeemAsync(secondCode)).Error.Should().Be(InvitationError.None);
    }

    // ── admin writes cannot undo a redemption ───────────────────────────

    [Fact]
    public async Task StaleAdminEditOfAnInvitedUser_AfterRedemption_IsRefusedAndDoesNotRevertTheLink()
    {
        var (invitation, code) = await InviteAsync();
        var repository = new TenantUserRepository(_database, NullLogger<TenantUserRepository>.Instance);
        var staleCopy = await repository.GetByIdAsync(invitation.UserId);

        (await RedeemAsync(code)).Error.Should().Be(InvitationError.None);

        staleCopy!.DisplayName = "Edited";
        var act = () => repository.UpdateAsync(staleCopy);
        await act.Should().ThrowAsync<UserChangedConcurrentlyException>();

        var user = await UserAsync(invitation.UserId);
        user.Status.Should().Be(TenantUserStatus.Active);
        user.AzureAdObjectId.Should().Be("oid-guest");
    }

    [Fact]
    public async Task AdminUpdate_NeverWritesTheEntraLink()
    {
        var user = await AddUserAsync("pat@acme.example", oid: "oid-1", tid: GuestTid);
        var repository = new TenantUserRepository(_database, NullLogger<TenantUserRepository>.Instance);
        var copy = await repository.GetByIdAsync(user.Id);

        copy!.AzureAdObjectId = "oid-attacker";
        copy.AzureAdTenantId = OtherTid;
        copy.DisplayName = "Renamed";
        await repository.UpdateAsync(copy);

        var stored = await UserAsync(user.Id);
        stored.DisplayName.Should().Be("Renamed");
        stored.AzureAdObjectId.Should().Be("oid-1");
        stored.AzureAdTenantId.Should().Be(GuestTid);
    }

    [Fact]
    public async Task Unlink_ClearsTheLink_InTheTenantOnly()
    {
        var user = await AddUserAsync("pat@acme.example", oid: "oid-1", tid: GuestTid);
        var repository = new TenantUserRepository(_database, NullLogger<TenantUserRepository>.Instance);

        (await repository.UnlinkAsync("beta", user.Id, "admin")).Should().BeNull();
        var unlinked = await repository.UnlinkAsync(Tenant, user.Id, "admin");

        unlinked!.AzureAdObjectId.Should().BeEmpty();
        unlinked.AzureAdTenantId.Should().BeEmpty();
        unlinked.UpdatedBy.Should().Be("admin");
        unlinked.Status.Should().Be("Active");
    }
}
