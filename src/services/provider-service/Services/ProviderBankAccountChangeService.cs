using System.Text.Json;
using ProviderService.Models;
using ProviderService.Repositories;

namespace ProviderService.Services;

/// <summary>The change id does not name a change of this provider (404).</summary>
public sealed class BankAccountChangeNotFoundException : Exception
{
    public BankAccountChangeNotFoundException(string message) : base(message) { }
}

/// <summary>
/// The change cannot be decided as asked: it is no longer pending, the active
/// account changed since it was proposed, or another write got there first (409).
/// </summary>
public sealed class BankAccountChangeConflictException : Exception
{
    public BankAccountChangeConflictException(string message) : base(message) { }
}

/// <summary>
/// The acting identity may not make this decision (403): the approver
/// proposed the change (separation of duties), or the caller is a service
/// rather than a user.
/// </summary>
public sealed class BankAccountChangeForbiddenException : Exception
{
    public BankAccountChangeForbiddenException(string title, string message) : base(message) => Title = title;

    public string Title { get; }
}

/// <summary>Who is acting, from the validated token.</summary>
public readonly record struct BankAccountActor(string UserId, bool IsService);

/// <summary>
/// Dual control for provider bank accounts. A change is proposed (needs
/// <c>providers:write</c>) and stays pending until a different user holding
/// <c>payments:approve</c> approves it; only then do payments use it. There is
/// no per-tenant override: the rule is always on.
/// </summary>
public interface IProviderBankAccountChangeService
{
    /// <summary>
    /// The account payments use: the approved account on the provider's
    /// bank-account record, or, for a provider with no record yet, the
    /// account carried on the provider row from before dual control. Never a
    /// pending one.
    /// </summary>
    Task<ProviderBankAccount?> GetActiveAccountAsync(Provider provider, CancellationToken ct = default);

    Task<PendingBankAccountChange?> GetPendingAsync(Provider provider, CancellationToken ct = default);

    Task<IReadOnlyList<PendingBankAccountChange>> ListChangesAsync(Provider provider, CancellationToken ct = default);

    /// <summary>
    /// Records <paramref name="proposed"/> as the provider's pending change
    /// (cancelling any earlier pending one). The active account is unchanged.
    /// </summary>
    Task<PendingBankAccountChange> ProposeAsync(
        Provider provider, ProviderBankAccount proposed, string actorUserId, string source, CancellationToken ct = default);

    /// <summary>
    /// Whether <paramref name="account"/> would change anything: false when it
    /// equals the active account or the pending proposal.
    /// </summary>
    Task<bool> DiffersFromCurrentAsync(Provider provider, ProviderBankAccount account, CancellationToken ct = default);

    Task<PendingBankAccountChange> ApproveAsync(
        Provider provider, string changeId, BankAccountActor actor, string? reason, CancellationToken ct = default);

    Task<PendingBankAccountChange> RejectAsync(
        Provider provider, string changeId, BankAccountActor actor, string? reason, CancellationToken ct = default);

    Task<PendingBankAccountChange> CancelAsync(
        Provider provider, string changeId, string actorUserId, string? reason, CancellationToken ct = default);
}

public sealed class ProviderBankAccountChangeService : IProviderBankAccountChangeService
{
    public static readonly EventId ProposedEvent = new(4701, "ProviderBankAccountChangeProposed");
    public static readonly EventId ApprovedEvent = new(4702, "ProviderBankAccountChangeApproved");
    public static readonly EventId RejectedEvent = new(4703, "ProviderBankAccountChangeRejected");
    public static readonly EventId CancelledEvent = new(4704, "ProviderBankAccountChangeCancelled");
    public static readonly EventId RefusedEvent = new(4705, "ProviderBankAccountChangeRefused");

    private static readonly JsonSerializerOptions CloneOptions = new(JsonSerializerDefaults.Web);

    private readonly IProviderBankAccountRepository _repository;
    private readonly ILogger<ProviderBankAccountChangeService> _logger;

    public ProviderBankAccountChangeService(
        IProviderBankAccountRepository repository,
        ILogger<ProviderBankAccountChangeService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<ProviderBankAccount?> GetActiveAccountAsync(Provider provider, CancellationToken ct = default)
    {
        var record = await _repository.GetAsync(provider.TenantId, provider.ProviderId, ct);
        return record != null ? record.Active : provider.BankAccount;
    }

    public async Task<PendingBankAccountChange?> GetPendingAsync(Provider provider, CancellationToken ct = default)
    {
        var record = await _repository.GetAsync(provider.TenantId, provider.ProviderId, ct);
        return record?.Pending;
    }

    public async Task<IReadOnlyList<PendingBankAccountChange>> ListChangesAsync(Provider provider, CancellationToken ct = default)
    {
        var record = await _repository.GetAsync(provider.TenantId, provider.ProviderId, ct);
        return record == null
            ? Array.Empty<PendingBankAccountChange>()
            : record.Changes.OrderByDescending(c => c.RequestedAt).ToList();
    }

    public async Task<bool> DiffersFromCurrentAsync(Provider provider, ProviderBankAccount account, CancellationToken ct = default)
    {
        var record = await _repository.GetAsync(provider.TenantId, provider.ProviderId, ct);
        var active = record != null ? record.Active : provider.BankAccount;
        var candidate = Clone(account);
        BankAccountMasking.DeriveLast4(candidate);
        if (BankAccountMasking.SameAccount(active, candidate)) return false;
        if (record?.Pending?.Proposed is { } pending && BankAccountMasking.SameAccount(pending, candidate)) return false;
        return true;
    }

    public async Task<PendingBankAccountChange> ProposeAsync(
        Provider provider, ProviderBankAccount proposed, string actorUserId, string source, CancellationToken ct = default)
    {
        var tenantId = Require(provider.TenantId, "tenant");
        var providerId = Require(provider.ProviderId, "provider id");
        var now = DateTime.UtcNow;

        var record = await _repository.GetAsync(tenantId, providerId, ct) ?? NewRecord(provider);
        var expectedRevision = record.Revision;

        var details = Clone(proposed);
        BankAccountMasking.DeriveLast4(details);

        var change = new PendingBankAccountChange
        {
            TenantId = tenantId,
            ProviderId = providerId,
            ProviderNpi = provider.NPI,
            Proposed = details,
            BaseActiveChangeId = record.ActiveChangeId,
            RequestedBy = actorUserId,
            RequestedAt = now,
            Source = source,
            Status = BankAccountChangeStatus.Pending,
        };

        // One pending change per provider: a new proposal supersedes the old one.
        var superseded = record.Changes.Where(c => c.Status == BankAccountChangeStatus.Pending).ToList();
        foreach (var old in superseded)
        {
            Close(old, BankAccountChangeStatus.Cancelled, actorUserId, now, $"Superseded by bank-account change {change.Id}");
        }

        record.ProviderNpi ??= provider.NPI;
        record.Changes.Add(change);

        if (!await _repository.SaveAsync(record, expectedRevision, ct))
        {
            throw new BankAccountChangeConflictException(
                $"The bank account of provider {providerId} was changed by another request at the same time. Retry.");
        }

        foreach (var old in superseded)
        {
            Audit(CancelledEvent, "cancelled (superseded)", old, actorUserId, tenantId);
        }
        Audit(ProposedEvent, "proposed", change, actorUserId, tenantId);
        return change;
    }

    public async Task<PendingBankAccountChange> ApproveAsync(
        Provider provider, string changeId, BankAccountActor actor, string? reason, CancellationToken ct = default)
    {
        var (record, change) = await LoadAsync(provider, changeId, ct);
        RequireUser(actor, "approve", change, provider.TenantId);

        // Separation of duties: the user who proposed a change cannot approve it.
        if (string.IsNullOrWhiteSpace(actor.UserId)
            || string.Equals(actor.UserId, change.RequestedBy, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(RefusedEvent,
                "AUDIT bank-account change refused: user {UserId} tried to approve change {ChangeId} for provider {ProviderId} " +
                "in tenant {TenantId} that they proposed (separation of duties)",
                Sanitize(actor.UserId), change.Id, Sanitize(provider.ProviderId), Sanitize(provider.TenantId));
            throw new BankAccountChangeForbiddenException("Separation of duties",
                "Separation of duties: you proposed this bank-account change, so you cannot approve it. " +
                "Another user with payments:approve must approve it.");
        }

        RequirePending(change);

        if (!string.Equals(record.ActiveChangeId, change.BaseActiveChangeId, StringComparison.Ordinal))
        {
            throw new BankAccountChangeConflictException(
                $"The provider's bank account has changed since change {change.Id} was proposed. It cannot be approved; propose it again.");
        }

        var expectedRevision = record.Revision;
        var now = DateTime.UtcNow;
        var previous = BankAccountMasking.Mask(record.Active);
        var newAccount = change.Proposed;

        record.Active = newAccount;
        record.ActiveChangeId = change.Id;
        record.ActiveApprovedBy = actor.UserId;
        record.ActiveApprovedAt = now;
        change.PreviousAccount = previous;
        Close(change, BankAccountChangeStatus.Approved, actor.UserId, now, Trim(reason));

        if (!await _repository.SaveAsync(record, expectedRevision, ct))
        {
            throw new BankAccountChangeConflictException(
                $"Bank-account change {change.Id} was changed by another request at the same time. Reload and retry.");
        }

        Audit(ApprovedEvent, "approved", change, actor.UserId, provider.TenantId);
        return change;
    }

    public async Task<PendingBankAccountChange> RejectAsync(
        Provider provider, string changeId, BankAccountActor actor, string? reason, CancellationToken ct = default)
    {
        var (record, change) = await LoadAsync(provider, changeId, ct);
        RequireUser(actor, "reject", change, provider.TenantId);
        RequirePending(change);

        var expectedRevision = record.Revision;
        Close(change, BankAccountChangeStatus.Rejected, actor.UserId, DateTime.UtcNow, Trim(reason));
        if (!await _repository.SaveAsync(record, expectedRevision, ct))
        {
            throw new BankAccountChangeConflictException(
                $"Bank-account change {change.Id} was changed by another request at the same time. Reload and retry.");
        }

        Audit(RejectedEvent, "rejected", change, actor.UserId, provider.TenantId);
        return change;
    }

    public async Task<PendingBankAccountChange> CancelAsync(
        Provider provider, string changeId, string actorUserId, string? reason, CancellationToken ct = default)
    {
        var (record, change) = await LoadAsync(provider, changeId, ct);
        RequirePending(change);

        var expectedRevision = record.Revision;
        Close(change, BankAccountChangeStatus.Cancelled, actorUserId, DateTime.UtcNow, Trim(reason) ?? "Cancelled");
        if (!await _repository.SaveAsync(record, expectedRevision, ct))
        {
            throw new BankAccountChangeConflictException(
                $"Bank-account change {change.Id} was changed by another request at the same time. Reload and retry.");
        }

        Audit(CancelledEvent, "cancelled", change, actorUserId, provider.TenantId);
        return change;
    }

    private async Task<(ProviderBankAccountRecord Record, PendingBankAccountChange Change)> LoadAsync(
        Provider provider, string changeId, CancellationToken ct)
    {
        var record = await _repository.GetAsync(provider.TenantId, provider.ProviderId, ct);
        var change = record?.Changes.FirstOrDefault(c => string.Equals(c.Id, changeId, StringComparison.Ordinal));
        if (record == null || change == null)
        {
            throw new BankAccountChangeNotFoundException(
                $"Bank-account change {changeId} not found for provider {provider.ProviderId}");
        }
        return (record, change);
    }

    private void RequireUser(BankAccountActor actor, string action, PendingBankAccountChange change, string tenantId)
    {
        if (!actor.IsService) return;

        _logger.LogWarning(RefusedEvent,
            "AUDIT bank-account change refused: service {UserId} tried to {Action} change {ChangeId} for provider {ProviderId} in tenant {TenantId}",
            Sanitize(actor.UserId), action, change.Id, Sanitize(change.ProviderId), Sanitize(tenantId));
        throw new BankAccountChangeForbiddenException("Separation of duties",
            $"A bank-account change must be decided by a user holding payments:approve; a service token cannot {action} it.");
    }

    private static void RequirePending(PendingBankAccountChange change)
    {
        if (change.Status != BankAccountChangeStatus.Pending)
        {
            throw new BankAccountChangeConflictException(
                $"Bank-account change {change.Id} is {change.Status}, not Pending.");
        }
    }

    /// <summary>
    /// Decides a change and drops its full numbers: only the masked view of a
    /// decided change is kept (an approved account's full numbers live on the
    /// record's active account).
    /// </summary>
    private static void Close(PendingBankAccountChange change, BankAccountChangeStatus status, string actor, DateTime at, string? reason)
    {
        change.Status = status;
        change.DecidedBy = actor;
        change.DecidedAt = at;
        change.Reason = reason;
        change.Proposed = BankAccountMasking.Mask(change.Proposed);
    }

    private static ProviderBankAccountRecord NewRecord(Provider provider) => new()
    {
        TenantId = provider.TenantId,
        ProviderId = provider.ProviderId,
        ProviderNpi = provider.NPI,
        // An account set before dual control stays active until a change is approved.
        Active = provider.BankAccount == null ? null : Clone(provider.BankAccount),
        ActiveChangeId = provider.BankAccount == null ? null : ProviderBankAccountRecord.LegacyChangeId,
        Revision = 0,
    };

    /// <summary>Audit entry: actor, provider, tenant and change; never an account number.</summary>
    private void Audit(EventId eventId, string action, PendingBankAccountChange change, string actor, string tenantId)
    {
        _logger.LogInformation(eventId,
            "AUDIT bank-account change {Action}: change {ChangeId} for provider {ProviderId} (NPI {Npi}) in tenant {TenantId} " +
            "by {UserId}; requested by {RequestedBy}; status {Status}",
            action, change.Id, Sanitize(change.ProviderId), Sanitize(change.ProviderNpi), Sanitize(tenantId),
            Sanitize(actor), Sanitize(change.RequestedBy), change.Status);
    }

    private static ProviderBankAccount Clone(ProviderBankAccount account)
        => JsonSerializer.Deserialize<ProviderBankAccount>(JsonSerializer.Serialize(account, CloneOptions), CloneOptions)!;

    private static string Require(string? value, string what)
        => string.IsNullOrEmpty(value) ? throw new InvalidOperationException($"Provider {what} is required") : value;

    private static string? Trim(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        var r = reason.Trim();
        return r.Length > 500 ? r[..500] : r;
    }

    private static string Sanitize(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
