using System.Text.Json;
using CloudHealthOffice.FieldProtection;
using SponsorService.Models;
using SponsorService.Repositories;

namespace SponsorService.Services;

/// <summary>The change id does not name a change of this sponsor (404).</summary>
public sealed class SponsorBankAccountNotFoundException : Exception
{
    public SponsorBankAccountNotFoundException(string message) : base(message) { }
}

/// <summary>
/// The change cannot be decided as asked: it is no longer pending, the active
/// account changed since it was proposed, or another write got there first (409).
/// </summary>
public sealed class SponsorBankAccountConflictException : Exception
{
    public SponsorBankAccountConflictException(string message) : base(message) { }
}

/// <summary>The proposal is not a usable account (400).</summary>
public sealed class SponsorBankAccountValidationException : Exception
{
    public SponsorBankAccountValidationException(IReadOnlyList<string> errors)
        : base(string.Join(" ", errors)) => Errors = errors;

    public IReadOnlyList<string> Errors { get; }
}

/// <summary>
/// The acting identity may not make this decision (403): the approver
/// proposed the change (separation of duties), or the caller is a service.
/// </summary>
public sealed class SponsorBankAccountForbiddenException : Exception
{
    public SponsorBankAccountForbiddenException(string message) : base(message) { }

    public string Title => "Separation of duties";
}

/// <summary>Who is acting, from the validated token.</summary>
public readonly record struct SponsorBankAccountActor(string UserId, bool IsService);

/// <summary>
/// Dual control over sponsor bank accounts (what premium billing debits),
/// after provider-service's ProviderBankAccountChangeService. A change is
/// proposed (billing:run or enrollment:process) and stays pending until a
/// different user holding payments:approve approves it; only then does
/// premium billing see it. The first account is pending too. No per-tenant
/// override. Routing and account numbers are encrypted at rest through
/// <see cref="IFieldProtector"/>; every audit entry leaves them out.
/// </summary>
public interface ISponsorBankAccountService
{
    /// <summary>The record with numbers decrypted, or null when the sponsor has none.</summary>
    Task<SponsorBankAccountRecord?> GetAsync(Sponsor sponsor, CancellationToken ct = default);

    /// <summary>
    /// The approved account premium billing debits, numbers decrypted. Null
    /// when none is approved (or the record belongs to an earlier sponsor
    /// document with the same group number). Never a pending one.
    /// </summary>
    Task<SponsorBankAccountDetails?> GetActiveAsync(Sponsor sponsor, CancellationToken ct = default);

    Task<SponsorBankAccountChange> ProposeAsync(
        Sponsor sponsor, ProposeSponsorBankAccountRequest request, string actorUserId, CancellationToken ct = default);

    Task<SponsorBankAccountChange> ApproveAsync(
        Sponsor sponsor, string changeId, SponsorBankAccountActor actor, string? reason, CancellationToken ct = default);

    Task<SponsorBankAccountChange> RejectAsync(
        Sponsor sponsor, string changeId, SponsorBankAccountActor actor, string? reason, CancellationToken ct = default);

    Task<SponsorBankAccountChange> CancelAsync(
        Sponsor sponsor, string changeId, string actorUserId, string? reason, CancellationToken ct = default);
}

public sealed class SponsorBankAccountService : ISponsorBankAccountService
{
    public static readonly EventId ProposedEvent = new(4811, "SponsorBankAccountChangeProposed");
    public static readonly EventId ApprovedEvent = new(4812, "SponsorBankAccountChangeApproved");
    public static readonly EventId RejectedEvent = new(4813, "SponsorBankAccountChangeRejected");
    public static readonly EventId CancelledEvent = new(4814, "SponsorBankAccountChangeCancelled");
    public static readonly EventId RefusedEvent = new(4815, "SponsorBankAccountChangeRefused");

    private static readonly JsonSerializerOptions CloneOptions = new(JsonSerializerDefaults.Web);

    private readonly ISponsorBankAccountRepository _repository;
    private readonly IFieldProtector _protector;
    private readonly ILogger<SponsorBankAccountService> _logger;

    public SponsorBankAccountService(
        ISponsorBankAccountRepository repository,
        IFieldProtector protector,
        ILogger<SponsorBankAccountService> logger)
    {
        _repository = repository;
        _protector = protector;
        _logger = logger;
    }

    public async Task<SponsorBankAccountRecord?> GetAsync(Sponsor sponsor, CancellationToken ct = default)
    {
        var stored = await _repository.GetAsync(sponsor.TenantId, sponsor.GroupNumber, ct);
        return stored == null ? null : Decrypt(stored);
    }

    public async Task<SponsorBankAccountDetails?> GetActiveAsync(Sponsor sponsor, CancellationToken ct = default)
    {
        var record = await GetAsync(sponsor, ct);
        if (record?.Active == null) return null;
        if (!string.IsNullOrEmpty(record.SponsorId) && !string.Equals(record.SponsorId, sponsor.Id, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Bank-account record for sponsor {GroupNumber} in tenant {TenantId} belongs to an earlier sponsor document; not used",
                Sanitize(sponsor.GroupNumber), Sanitize(sponsor.TenantId));
            return null;
        }
        return record.Active;
    }

    public async Task<SponsorBankAccountChange> ProposeAsync(
        Sponsor sponsor, ProposeSponsorBankAccountRequest request, string actorUserId, CancellationToken ct = default)
    {
        var tenantId = Require(sponsor.TenantId, "tenant");
        var group = Require(sponsor.GroupNumber, "group number");
        var now = DateTime.UtcNow;

        var record = await GetAsync(sponsor, ct) ?? new SponsorBankAccountRecord
        {
            TenantId = tenantId,
            GroupNumber = group,
            SponsorId = sponsor.Id,
            Revision = 0,
        };
        var expectedRevision = record.Revision;

        // A record left by an earlier sponsor document with the same group
        // number (deleted and re-created) gives the new sponsor nothing: its
        // account is not carried over and its pending change is superseded.
        if (!string.IsNullOrEmpty(record.SponsorId) && !string.Equals(record.SponsorId, sponsor.Id, StringComparison.Ordinal))
        {
            record.SponsorId = sponsor.Id;
            record.Active = null;
            record.ActiveChangeId = null;
            record.ActiveApprovedBy = null;
            record.ActiveApprovedAt = null;
        }

        var (details, carriedOver) = BuildDetails(request, record.Active);

        var change = new SponsorBankAccountChange
        {
            TenantId = tenantId,
            GroupNumber = group,
            Proposed = details,
            NumbersCarriedOver = carriedOver,
            BaseActiveChangeId = record.ActiveChangeId,
            RequestedBy = actorUserId,
            RequestedAt = now,
            Status = SponsorBankAccountChangeStatus.Pending,
        };

        // One pending change per sponsor: a new proposal supersedes the old one.
        var superseded = record.Changes.Where(c => c.Status == SponsorBankAccountChangeStatus.Pending).ToList();
        foreach (var old in superseded)
        {
            Close(old, SponsorBankAccountChangeStatus.Cancelled, actorUserId, now, $"Superseded by bank-account change {change.Id}");
        }

        if (string.IsNullOrEmpty(record.SponsorId)) record.SponsorId = sponsor.Id;
        record.Changes.Add(change);

        if (!await SaveAsync(record, expectedRevision, ct))
        {
            throw new SponsorBankAccountConflictException(
                $"The bank account of sponsor {group} was changed by another request at the same time. Retry.");
        }

        foreach (var old in superseded)
        {
            Audit(CancelledEvent, "cancelled (superseded)", old, actorUserId, tenantId);
        }
        Audit(ProposedEvent, "proposed", change, actorUserId, tenantId);
        return change;
    }

    public async Task<SponsorBankAccountChange> ApproveAsync(
        Sponsor sponsor, string changeId, SponsorBankAccountActor actor, string? reason, CancellationToken ct = default)
    {
        var (record, change) = await LoadAsync(sponsor, changeId, ct);
        RequireUser(actor, "approve", change, sponsor.TenantId);

        // Separation of duties: the user who proposed a change cannot approve it.
        if (string.IsNullOrWhiteSpace(actor.UserId)
            || string.Equals(actor.UserId, change.RequestedBy, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(RefusedEvent,
                "AUDIT sponsor bank-account change refused: user {UserId} tried to approve change {ChangeId} for sponsor {GroupNumber} " +
                "in tenant {TenantId} that they proposed (separation of duties)",
                Sanitize(actor.UserId), change.Id, Sanitize(sponsor.GroupNumber), Sanitize(sponsor.TenantId));
            throw new SponsorBankAccountForbiddenException(
                "Separation of duties: you proposed this bank-account change, so you cannot approve it. " +
                "Another user with payments:approve must approve it.");
        }

        RequirePending(change);

        if (!string.Equals(record.ActiveChangeId, change.BaseActiveChangeId, StringComparison.Ordinal)
            || (!string.IsNullOrEmpty(record.SponsorId) && !string.Equals(record.SponsorId, sponsor.Id, StringComparison.Ordinal)))
        {
            Refused(actor.UserId, "approve", change, sponsor.TenantId, "the active account changed since it was proposed");
            throw new SponsorBankAccountConflictException(
                $"The sponsor's bank account has changed since change {change.Id} was proposed. It cannot be approved; propose it again.");
        }

        var expectedRevision = record.Revision;
        var now = DateTime.UtcNow;

        change.PreviousAccount = SponsorBankAccountMasking.Mask(record.Active);
        record.Active = Clone(change.Proposed!);
        record.ActiveChangeId = change.Id;
        record.ActiveApprovedBy = actor.UserId;
        record.ActiveApprovedAt = now;
        if (string.IsNullOrEmpty(record.SponsorId)) record.SponsorId = sponsor.Id;
        Close(change, SponsorBankAccountChangeStatus.Approved, actor.UserId, now, Trim(reason));

        if (!await SaveAsync(record, expectedRevision, ct))
        {
            Refused(actor.UserId, "approve", change, sponsor.TenantId, "another request changed the record first");
            throw new SponsorBankAccountConflictException(
                $"Bank-account change {change.Id} was changed by another request at the same time. Reload and retry.");
        }

        Audit(ApprovedEvent, "approved", change, actor.UserId, sponsor.TenantId);
        return change;
    }

    public async Task<SponsorBankAccountChange> RejectAsync(
        Sponsor sponsor, string changeId, SponsorBankAccountActor actor, string? reason, CancellationToken ct = default)
    {
        var (record, change) = await LoadAsync(sponsor, changeId, ct);
        RequireUser(actor, "reject", change, sponsor.TenantId);
        RequirePending(change);

        var expectedRevision = record.Revision;
        Close(change, SponsorBankAccountChangeStatus.Rejected, actor.UserId, DateTime.UtcNow, Trim(reason));
        if (!await SaveAsync(record, expectedRevision, ct))
        {
            throw new SponsorBankAccountConflictException(
                $"Bank-account change {change.Id} was changed by another request at the same time. Reload and retry.");
        }

        Audit(RejectedEvent, "rejected", change, actor.UserId, sponsor.TenantId);
        return change;
    }

    public async Task<SponsorBankAccountChange> CancelAsync(
        Sponsor sponsor, string changeId, string actorUserId, string? reason, CancellationToken ct = default)
    {
        var (record, change) = await LoadAsync(sponsor, changeId, ct);
        RequirePending(change);

        var expectedRevision = record.Revision;
        Close(change, SponsorBankAccountChangeStatus.Cancelled, actorUserId, DateTime.UtcNow, Trim(reason) ?? "Cancelled");
        if (!await SaveAsync(record, expectedRevision, ct))
        {
            throw new SponsorBankAccountConflictException(
                $"Bank-account change {change.Id} was changed by another request at the same time. Reload and retry.");
        }

        Audit(CancelledEvent, "cancelled", change, actorUserId, sponsor.TenantId);
        return change;
    }

    // ── proposal ────────────────────────────────────────────────────────

    /// <summary>
    /// The account as it will be after approval. Without numbers, the active
    /// account's numbers are reused (an enrollment-only change); with them,
    /// both must be valid. An enrolled NACHA account needs numbers; Stripe ACH
    /// needs the Stripe ids.
    /// </summary>
    private static (SponsorBankAccountDetails Details, bool CarriedOver) BuildDetails(
        ProposeSponsorBankAccountRequest request, SponsorBankAccountDetails? active)
    {
        var errors = new List<string>();
        var routing = request.RoutingNumber?.Trim();
        var account = request.AccountNumber?.Trim();
        var hasRouting = !string.IsNullOrEmpty(routing);
        var hasAccount = !string.IsNullOrEmpty(account);
        var carriedOver = false;

        if (hasRouting != hasAccount)
        {
            errors.Add("Give both routing and account number, or neither to keep the current ones.");
        }
        else if (hasRouting)
        {
            if (!SponsorBankAccountMasking.IsValidRoutingNumber(routing))
                errors.Add("The routing number must be a valid 9-digit ABA routing number.");
            if (!SponsorBankAccountMasking.IsValidAccountNumber(account))
                errors.Add("The account number must be 4 to 17 digits.");
        }
        else if (!string.IsNullOrEmpty(active?.RoutingNumber) && !string.IsNullOrEmpty(active.AccountNumber))
        {
            routing = active.RoutingNumber;
            account = active.AccountNumber;
            carriedOver = true;
        }

        var method = request.PreferredMethod ?? SponsorDebitMethod.Nacha;
        if (request.EftEnabled && method == SponsorDebitMethod.Nacha && string.IsNullOrEmpty(routing))
            errors.Add("Auto-debit by NACHA needs a routing and an account number.");
        if (request.EftEnabled && method == SponsorDebitMethod.StripeAch
            && (string.IsNullOrWhiteSpace(request.StripeCustomerId) || string.IsNullOrWhiteSpace(request.StripePaymentMethodId)))
            errors.Add("Auto-debit by Stripe ACH needs a Stripe customer id and payment method id.");

        if (errors.Count > 0) throw new SponsorBankAccountValidationException(errors);

        var details = new SponsorBankAccountDetails
        {
            EftEnabled = request.EftEnabled,
            PreferredMethod = request.PreferredMethod,
            RoutingNumber = string.IsNullOrEmpty(routing) ? null : routing,
            AccountNumber = string.IsNullOrEmpty(account) ? null : account,
            AccountType = request.AccountType,
            AccountHolderName = request.AccountHolderName?.Trim(),
            StripeCustomerId = request.StripeCustomerId?.Trim(),
            StripePaymentMethodId = request.StripePaymentMethodId?.Trim(),
        };
        details.RoutingNumberLast4 = SponsorBankAccountMasking.Last4(details.RoutingNumber);
        details.AccountNumberLast4 = SponsorBankAccountMasking.Last4(details.AccountNumber);
        return (details, carriedOver);
    }

    // ── storage: encryption at rest ─────────────────────────────────────

    /// <summary>
    /// Saves a copy of <paramref name="record"/> with every routing and
    /// account number encrypted; the in-memory record keeps plaintext.
    /// </summary>
    private async Task<bool> SaveAsync(SponsorBankAccountRecord record, long expectedRevision, CancellationToken ct)
    {
        var stored = Clone(record);
        Encrypt(stored.Active);
        foreach (var change in stored.Changes)
        {
            Encrypt(change.Proposed);
            Encrypt(change.PreviousAccount);
        }

        var saved = await _repository.SaveAsync(stored, expectedRevision, ct);
        if (saved)
        {
            record.Id = stored.Id;
            record.Revision = stored.Revision;
            record.UpdatedAt = stored.UpdatedAt;
        }
        return saved;
    }

    private SponsorBankAccountRecord Decrypt(SponsorBankAccountRecord stored)
    {
        var record = Clone(stored);
        Decrypt(record.Active);
        foreach (var change in record.Changes)
        {
            Decrypt(change.Proposed);
            Decrypt(change.PreviousAccount);
        }
        return record;
    }

    private void Encrypt(SponsorBankAccountDetails? details)
    {
        if (details == null) return;
        details.RoutingNumber = _protector.Protect(details.RoutingNumber);
        details.AccountNumber = _protector.Protect(details.AccountNumber);
    }

    private void Decrypt(SponsorBankAccountDetails? details)
    {
        if (details == null) return;
        details.RoutingNumber = _protector.Unprotect(details.RoutingNumber);
        details.AccountNumber = _protector.Unprotect(details.AccountNumber);
    }

    // ── rules ───────────────────────────────────────────────────────────

    private async Task<(SponsorBankAccountRecord Record, SponsorBankAccountChange Change)> LoadAsync(
        Sponsor sponsor, string changeId, CancellationToken ct)
    {
        var record = await GetAsync(sponsor, ct);
        var change = record?.Changes.FirstOrDefault(c => string.Equals(c.Id, changeId, StringComparison.Ordinal));
        if (record == null || change == null)
        {
            throw new SponsorBankAccountNotFoundException(
                $"Bank-account change {changeId} not found for sponsor {sponsor.GroupNumber}");
        }
        return (record, change);
    }

    private void RequireUser(SponsorBankAccountActor actor, string action, SponsorBankAccountChange change, string tenantId)
    {
        if (!actor.IsService) return;

        _logger.LogWarning(RefusedEvent,
            "AUDIT sponsor bank-account change refused: service {UserId} tried to {Action} change {ChangeId} for sponsor {GroupNumber} in tenant {TenantId}",
            Sanitize(actor.UserId), action, change.Id, Sanitize(change.GroupNumber), Sanitize(tenantId));
        throw new SponsorBankAccountForbiddenException(
            $"A sponsor bank-account change must be decided by a user holding payments:approve; a service token cannot {action} it.");
    }

    private static void RequirePending(SponsorBankAccountChange change)
    {
        if (change.Status != SponsorBankAccountChangeStatus.Pending)
        {
            throw new SponsorBankAccountConflictException(
                $"Bank-account change {change.Id} is {change.Status}, not Pending.");
        }
    }

    /// <summary>Decides a change and drops its full numbers: only the masked view of a decided change is kept.</summary>
    private static void Close(SponsorBankAccountChange change, SponsorBankAccountChangeStatus status, string actor, DateTime at, string? reason)
    {
        change.Status = status;
        change.DecidedBy = actor;
        change.DecidedAt = at;
        change.Reason = reason;
        change.Proposed = SponsorBankAccountMasking.Mask(change.Proposed);
    }

    /// <summary>Audit entry: actor, sponsor, tenant and change; never a routing or account number.</summary>
    private void Audit(EventId eventId, string action, SponsorBankAccountChange change, string actor, string tenantId)
    {
        _logger.LogInformation(eventId,
            "AUDIT sponsor bank-account change {Action}: change {ChangeId} for sponsor {GroupNumber} in tenant {TenantId} " +
            "by {UserId}; requested by {RequestedBy}; status {Status}",
            action, change.Id, Sanitize(change.GroupNumber), Sanitize(tenantId),
            Sanitize(actor), Sanitize(change.RequestedBy), change.Status);
    }

    private void Refused(string actor, string action, SponsorBankAccountChange change, string tenantId, string why)
    {
        _logger.LogWarning(RefusedEvent,
            "AUDIT sponsor bank-account change refused: {UserId} could not {Action} change {ChangeId} for sponsor {GroupNumber} " +
            "in tenant {TenantId}: {Why}",
            Sanitize(actor), action, change.Id, Sanitize(change.GroupNumber), Sanitize(tenantId), why);
    }

    private static T Clone<T>(T value)
        => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, CloneOptions), CloneOptions)!;

    private static string Require(string? value, string what)
        => string.IsNullOrEmpty(value) ? throw new InvalidOperationException($"Sponsor {what} is required") : value;

    private static string? Trim(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        var r = reason.Trim();
        return r.Length > 500 ? r[..500] : r;
    }

    private static string Sanitize(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
