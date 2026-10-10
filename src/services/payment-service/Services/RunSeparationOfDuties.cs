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
/// Thrown when a run cannot be started because it is not Pending: it was
/// executed already, or another executor started it a moment earlier (the
/// Pending -> Running write is conditional). Controllers answer 409.
/// </summary>
public sealed class RunConflictException : InvalidOperationException
{
    public RunConflictException(string message) : base(message) { }
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

    /// <summary>
    /// The acting user, after checking they may release a claim reservation a
    /// run holds: a user (never a service token) other than the user who
    /// executed the run. Releasing lets a later run pay (or reverse) the claim,
    /// so the executor does not get to judge their own run's leftovers.
    /// </summary>
    /// <exception cref="SeparationOfDutiesException">The actor is a service, unauthenticated, or the run's executor.</exception>
    string EnsureMayReleaseReservation(string runKind, string runNumber, string? executedBy);

    /// <summary>
    /// The acting user, after checking they may approve, send, retry or reconcile a
    /// payment run's NACHA file at the bank (the step that actually moves money): a
    /// user (never a service token) who neither created nor executed the run. A run
    /// with no recorded creator is refused here (not merely logged): the maker-checker
    /// comparison cannot be made.
    /// </summary>
    /// <exception cref="SeparationOfDutiesException">The actor is a service, unauthenticated, the run's creator or executor, or no creator is recorded.</exception>
    string EnsureMayTransmit(string runNumber, string? createdBy, string? executedBy);
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

    public string EnsureMayReleaseReservation(string runKind, string runNumber, string? executedBy)
    {
        var action = $"releasing a claim reservation of a {runKind} lets a later run pay it";
        var user = EnsureUser(action);

        if (string.IsNullOrWhiteSpace(executedBy))
        {
            _logger.LogWarning(NoMakerRecordedEvent,
                "No executor recorded for {RunKind} {RunNumber}; reservation release by {User} allowed without an executor comparison",
                runKind, Sanitize(runNumber), Sanitize(user));
            return user;
        }

        if (string.Equals(executedBy, user, StringComparison.OrdinalIgnoreCase))
            throw new SeparationOfDutiesException(
                $"Separation of duties: you executed {runKind} {runNumber}, so you cannot release its claim reservations. " +
                "A different user with payments:approve must release them.");

        return user;
    }

    public string EnsureMayTransmit(string runNumber, string? createdBy, string? executedBy)
    {
        var user = EnsureUser("sending a payment run's NACHA file to the bank moves money");

        if (string.IsNullOrWhiteSpace(createdBy))
            throw new SeparationOfDutiesException(
                $"Separation of duties: payment run {runNumber} has no recorded creator, so its file cannot be sent to the bank " +
                "(the maker-checker comparison cannot be made).");
        if (string.Equals(createdBy, user, StringComparison.OrdinalIgnoreCase))
            throw new SeparationOfDutiesException(
                $"Separation of duties: you created payment run {runNumber}, so you cannot send its file to the bank. " +
                "A different user with payments:approve must.");
        if (string.Equals(executedBy, user, StringComparison.OrdinalIgnoreCase))
            throw new SeparationOfDutiesException(
                $"Separation of duties: you executed payment run {runNumber}, so you cannot also send its file to the bank. " +
                "A different user with payments:approve must.");

        return user;
    }

    private string EnsureUser(string action)
    {
        if (!_actor.IsAuthenticated)
            throw new SeparationOfDutiesException(
                $"Separation of duties: {action} and needs a user with payments:approve");

        if (_actor.IsService)
            throw new SeparationOfDutiesException(
                $"Separation of duties: {action} and needs a user with payments:approve, not a service token");

        return _actor.UserId;
    }

    public string EnsureMayRelease(string runKind, string runNumber, string? createdBy)
    {
        var user = EnsureUser($"executing a {runKind} releases money");

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
