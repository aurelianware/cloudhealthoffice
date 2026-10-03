using CapitationService.Models;
using CapitationService.Repositories;
using CloudHealthOffice.Infrastructure.Security;

namespace CapitationService.Services;

/// <summary>
/// The approving or releasing user is one of the payment's makers, and the
/// tenant enforces separation of duties. Controllers answer 403.
/// </summary>
public sealed class SeparationOfDutiesException : Exception
{
    public SeparationOfDutiesException(string message) : base(message) { }
}

/// <summary>What the acting user is doing to the payment.</summary>
public enum PaymentAction
{
    /// <summary>Approving a capitation statement for payment.</summary>
    Approve,

    /// <summary>Releasing money for a statement: a disbursement, a batch, or a NACHA file.</summary>
    Release
}

/// <summary>
/// Maker-checker for capitation payments: a user who prepared a payment (created
/// or executed the capitation run that generated the statement) cannot approve it
/// or release it. On by default; a tenant can turn it off in tenant-service
/// (<c>configuration.paymentControls.enforceSeparationOfDuties = false</c>), and
/// every same-user approval or release that the override allows is logged as an
/// audit warning.
/// </summary>
public interface IPaymentSeparationOfDuties
{
    /// <summary>
    /// Throws <see cref="SeparationOfDutiesException"/> when <paramref name="actorUserId"/>
    /// is one of the statement's makers and the tenant enforces separation of duties.
    /// </summary>
    Task EnsureActorIsNotMakerAsync(CapitationStatement statement, string? actorUserId, PaymentAction action);
}

public sealed class PaymentSeparationOfDuties : IPaymentSeparationOfDuties
{
    /// <summary>Event id carried by every audit entry this rule writes.</summary>
    public static readonly EventId OverrideAuditEvent = new(4601, "PaymentSeparationOfDutiesOverride");
    public static readonly EventId NoMakerRecordedEvent = new(4602, "PaymentSeparationOfDutiesNoMaker");

    private readonly ICapitationRunRepository _runRepository;
    private readonly ITenantPaymentControls _controls;
    private readonly ICurrentActor _actor;
    private readonly ILogger<PaymentSeparationOfDuties> _logger;

    public PaymentSeparationOfDuties(
        ICapitationRunRepository runRepository,
        ITenantPaymentControls controls,
        ICurrentActor actor,
        ILogger<PaymentSeparationOfDuties> logger)
    {
        _runRepository = runRepository;
        _controls = controls;
        _actor = actor;
        _logger = logger;
    }

    public async Task EnsureActorIsNotMakerAsync(CapitationStatement statement, string? actorUserId, PaymentAction action)
    {
        var makers = await ResolveMakersAsync(statement);

        if (makers.Count == 0)
        {
            // Records written before makers were recorded: nothing to compare,
            // so the action is allowed and the gap is made visible.
            _logger.LogWarning(NoMakerRecordedEvent,
                "Separation of duties not checked: statement {StatementId} ({StatementNumber}) has no recorded creator; " +
                "user {UserId} allowed to {Action} it",
                statement.Id, statement.StatementNumber, Sanitize(actorUserId), action);
            return;
        }

        var isMaker = string.IsNullOrWhiteSpace(actorUserId)
            || makers.Contains(actorUserId, StringComparer.OrdinalIgnoreCase);
        if (!isMaker)
            return;

        var tenantId = TryTenantId();
        var enforced = tenantId == null || await _controls.IsSeparationOfDutiesEnforcedAsync(tenantId);

        if (enforced)
        {
            throw new SeparationOfDutiesException(action == PaymentAction.Approve
                ? $"Separation of duties: you prepared capitation statement {statement.StatementNumber} " +
                  "(created or executed its capitation run), so you cannot approve it. Another user with payments:approve must approve it."
                : $"Separation of duties: you prepared capitation statement {statement.StatementNumber} " +
                  "(created or executed its capitation run), so you cannot release its payment. Another user with payments:approve must release it.");
        }

        _logger.LogWarning(OverrideAuditEvent,
            "AUDIT separation-of-duties override: user {UserId} allowed to {Action} capitation statement {StatementId} " +
            "({StatementNumber}, net payable {NetPayable}) that they prepared, because tenant {TenantId} has " +
            "paymentControls.enforceSeparationOfDuties = false",
            Sanitize(actorUserId), action, statement.Id, statement.StatementNumber, statement.NetPayable, Sanitize(tenantId));
    }

    /// <summary>
    /// The users who prepared the statement. Statements carry their makers; older
    /// statements that only name the system placeholder fall back to their run.
    /// </summary>
    private async Task<List<string>> ResolveMakersAsync(CapitationStatement statement)
    {
        var makers = new List<string>();
        Add(makers, statement.CreatedBy);
        Add(makers, statement.RunCreatedBy);

        if (string.IsNullOrWhiteSpace(statement.RunCreatedBy) && !string.IsNullOrWhiteSpace(statement.CapitationRunId))
        {
            var run = await _runRepository.GetByIdAsync(statement.CapitationRunId);
            if (run != null)
            {
                Add(makers, run.CreatedBy);
                Add(makers, run.ExecutedBy);
            }
        }

        return makers;
    }

    private static void Add(List<string> makers, string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId) ||
            string.Equals(userId, CapitationStatement.SystemCreator, StringComparison.OrdinalIgnoreCase))
            return;
        makers.Add(userId);
    }

    private string? TryTenantId()
    {
        try
        {
            return _actor.TenantId;
        }
        catch (UnauthorizedAccessException)
        {
            return null; // no tenant: the override cannot apply
        }
    }

    private static string Sanitize(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
