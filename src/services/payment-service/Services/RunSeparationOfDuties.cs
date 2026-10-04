using CloudHealthOffice.Infrastructure.Security;

namespace PaymentService.Services;

/// <summary>
/// Thrown when the acting caller may not release money for a run
/// (maker-checker). Controllers answer 403 "Separation of duties".
/// </summary>
public sealed class SeparationOfDutiesException : Exception
{
    public SeparationOfDutiesException(string message) : base(message) { }
}

/// <summary>
/// Maker-checker for releasing money, after the capitation-service
/// (<c>PaymentSeparationOfDuties</c>) and premium-billing-service
/// (<c>DebitSeparationOfDuties</c>) precedent. Executing a payment run issues
/// the payments (check numbers, Posted payments, 835 envelopes, claims finalized
/// as paid); executing a reversal run recoups them (negative payments, reversal
/// 835s, claims voided). The user who created the run (the maker) cannot execute
/// it, and a service token (which satisfies every tenant permission) cannot
/// execute one at all. There is no per-tenant override, as in premium-billing
/// and provider-service bank-account changes. A run with no recorded creator
/// (created before creators came from the token) is allowed and logged.
/// </summary>
public interface IRunSeparationOfDuties
{
    /// <summary>The acting user, after checking they may release money for a run created by <paramref name="createdBy"/>.</summary>
    /// <exception cref="SeparationOfDutiesException">The actor is a service, unauthenticated, or the run's creator.</exception>
    string EnsureMayRelease(string runKind, string runNumber, string? createdBy);
}

public sealed class RunSeparationOfDuties : IRunSeparationOfDuties
{
    public static readonly EventId NoMakerRecordedEvent = new(4621, "PaymentRunNoMakerRecorded");

    private readonly ICurrentActor _actor;
    private readonly ILogger<RunSeparationOfDuties> _logger;

    public RunSeparationOfDuties(ICurrentActor actor, ILogger<RunSeparationOfDuties> logger)
    {
        _actor = actor;
        _logger = logger;
    }

    public string EnsureMayRelease(string runKind, string runNumber, string? createdBy)
    {
        if (!_actor.IsAuthenticated)
            throw new SeparationOfDutiesException(
                $"Separation of duties: executing a {runKind} releases money and needs a user with payments:approve");

        if (_actor.IsService)
            throw new SeparationOfDutiesException(
                $"Separation of duties: executing a {runKind} releases money and needs a user with payments:approve, not a service token");

        var user = _actor.UserId;

        if (string.IsNullOrWhiteSpace(createdBy))
        {
            _logger.LogWarning(NoMakerRecordedEvent,
                "No creator recorded for {RunKind} {RunNumber}; execution by {User} allowed without a maker-checker comparison",
                runKind, Sanitize(runNumber), Sanitize(user));
            return user;
        }

        if (string.Equals(createdBy, user, StringComparison.OrdinalIgnoreCase))
            throw new SeparationOfDutiesException(
                $"Separation of duties: you created {runKind} {runNumber}, so you cannot execute it. " +
                "A different user with payments:approve must execute it.");

        return user;
    }

    private static string Sanitize(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
