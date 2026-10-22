using EnrollmentImportService.Models;
using EnrollmentImportService.Repositories;

namespace EnrollmentImportService.Services;

public enum ExceptionActionOutcome { Done, NotFound, Invalid, Conflict }

public sealed record ExceptionActionResult(ExceptionActionOutcome Outcome, EnrollmentException? Exception, string? Error = null)
{
    public static ExceptionActionResult Ok(EnrollmentException e) => new(ExceptionActionOutcome.Done, e);
    public static ExceptionActionResult Missing() => new(ExceptionActionOutcome.NotFound, null);
    public static ExceptionActionResult Bad(string error, EnrollmentException? e = null) => new(ExceptionActionOutcome.Invalid, e, error);
    public static ExceptionActionResult Changed() => new(ExceptionActionOutcome.Conflict, null, "The exception changed since it was read; reload and try again.");
}

/// <summary>
/// The enrollment specialist's actions on an exception
/// (docs/architecture/enrollment-operations.md §3): reprocess (as received,
/// corrected, or matched to a member), hold for the sponsor, send back to the
/// sponsor, or close with no action. Every action is recorded on the exception.
/// </summary>
public sealed class EnrollmentExceptionService
{
    private readonly IEnrollmentExceptionRepository _exceptions;
    private readonly IEnrollmentImportService _import;
    private readonly TimeProvider _clock;

    public EnrollmentExceptionService(IEnrollmentExceptionRepository exceptions, IEnrollmentImportService import, TimeProvider clock)
    {
        _exceptions = exceptions;
        _import = import;
        _clock = clock;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Re-runs the change through the import, optionally corrected first (a
    /// whole replacement subscriber loop or dependent, of the same kind). Applied
    /// resolves the exception; another rejection keeps it open with the new reason.
    /// </summary>
    public async Task<ExceptionActionResult> ReprocessAsync(string tenantId, string id, string actor,
        MemberEnrollment? correctedSubscriber, Dependent? correctedDependent, string? note, CancellationToken ct = default)
    {
        var exception = await _exceptions.GetAsync(tenantId, id, ct);
        if (exception is null) return ExceptionActionResult.Missing();
        if (exception.Status == EnrollmentExceptionStatus.Resolved)
            return ExceptionActionResult.Bad("This exception is already resolved.", exception);
        if (correctedSubscriber is not null && exception.SourceKind != EnrollmentExceptionSourceKind.Subscriber
            || correctedDependent is not null && exception.SourceKind != EnrollmentExceptionSourceKind.Dependent)
            return ExceptionActionResult.Bad($"This exception is a {exception.SourceKind}; correct it with the same kind.", exception);

        var corrected = correctedSubscriber is not null || correctedDependent is not null;
        if (correctedSubscriber is not null) exception.Subscriber = correctedSubscriber;
        if (correctedDependent is not null) exception.Dependent = correctedDependent;
        return await RunAsync(exception, tenantId, actor, corrected ? EnrollmentExceptionActions.Corrected : EnrollmentExceptionActions.Reprocessed, note, ct);
    }

    /// <summary>
    /// Links the change to an existing member and reprocesses it: the
    /// subscriber id of a subscriber loop, or the subscriber a dependent
    /// belongs to. For "subscriber not found" and possible duplicates.
    /// </summary>
    public async Task<ExceptionActionResult> MatchAsync(string tenantId, string id, string actor, string memberId, string? note, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(memberId)) return ExceptionActionResult.Bad("memberId is required.");
        var exception = await _exceptions.GetAsync(tenantId, id, ct);
        if (exception is null) return ExceptionActionResult.Missing();
        if (exception.Status == EnrollmentExceptionStatus.Resolved)
            return ExceptionActionResult.Bad("This exception is already resolved.", exception);

        memberId = memberId.Trim();
        if (exception.SourceKind == EnrollmentExceptionSourceKind.Subscriber && exception.Subscriber is { } sub)
            sub.SubscriberId = memberId;
        else if (exception.Dependent is { } dep)
            dep.SubscriberId = memberId;
        else
            return ExceptionActionResult.Bad("The exception has no change to match.", exception);

        return await RunAsync(exception, tenantId, actor, EnrollmentExceptionActions.Matched,
            string.IsNullOrWhiteSpace(note) ? $"Matched to member {memberId}" : $"Matched to member {memberId}: {note}", ct);
    }

    public Task<ExceptionActionResult> HoldAsync(string tenantId, string id, string actor, DateTime until, string? note, CancellationToken ct = default)
    {
        var now = Now;
        if (until <= now) return Task.FromResult(ExceptionActionResult.Bad("Hold until a time in the future."));
        if (until > now.AddDays(90)) return Task.FromResult(ExceptionActionResult.Bad("A hold can last at most 90 days."));
        return UpdateAsync(tenantId, id, actor, EnrollmentExceptionActions.Held, note, e =>
        {
            e.Status = EnrollmentExceptionStatus.Held;
            e.HeldUntil = until;
        }, ct);
    }

    public Task<ExceptionActionResult> SendBackAsync(string tenantId, string id, string actor, string note, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(note)
            ? Task.FromResult(ExceptionActionResult.Bad("Say what the sponsor needs to correct."))
            : ResolveAsync(tenantId, id, actor, EnrollmentExceptionActions.SentBack, EnrollmentExceptionResolution.SentBackToSponsor, note, ct);

    public Task<ExceptionActionResult> NoActionAsync(string tenantId, string id, string actor, string note, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(note)
            ? Task.FromResult(ExceptionActionResult.Bad("A reason is required."))
            : ResolveAsync(tenantId, id, actor, EnrollmentExceptionActions.NoAction, EnrollmentExceptionResolution.NoAction, note, ct);

    private Task<ExceptionActionResult> ResolveAsync(string tenantId, string id, string actor, string action,
        EnrollmentExceptionResolution resolution, string note, CancellationToken ct) =>
        UpdateAsync(tenantId, id, actor, action, note, e =>
        {
            e.Status = EnrollmentExceptionStatus.Resolved;
            e.Resolution = resolution;
            e.ResolvedAt = Now;
            e.ResolvedBy = actor;
            e.HeldUntil = null;
        }, ct);

    private async Task<ExceptionActionResult> UpdateAsync(string tenantId, string id, string actor, string action, string? note,
        Action<EnrollmentException> change, CancellationToken ct)
    {
        var exception = await _exceptions.GetAsync(tenantId, id, ct);
        if (exception is null) return ExceptionActionResult.Missing();
        if (exception.Status == EnrollmentExceptionStatus.Resolved)
            return ExceptionActionResult.Bad("This exception is already resolved.", exception);
        var expected = exception.Version;
        change(exception);
        exception.UpdatedAt = Now;
        exception.Actions.Add(new EnrollmentExceptionAction { Action = action, Actor = actor, At = Now, Note = note?.Trim() });
        try
        {
            await _exceptions.SaveAsync(exception, expected, ct);
        }
        catch (EnrollmentExceptionConcurrencyException)
        {
            return ExceptionActionResult.Changed();
        }
        return ExceptionActionResult.Ok(exception);
    }

    private async Task<ExceptionActionResult> RunAsync(EnrollmentException exception, string tenantId, string actor, string action, string? note, CancellationToken ct)
    {
        // Record the action (and any correction) first, so the import's outcome lands on top of it.
        var expected = exception.Version;
        exception.Attempts++;
        exception.Status = EnrollmentExceptionStatus.Open;
        exception.HeldUntil = null;
        exception.UpdatedAt = Now;
        exception.Actions.Add(new EnrollmentExceptionAction { Action = action, Actor = actor, At = Now, Note = note?.Trim() });
        if (action != EnrollmentExceptionActions.Reprocessed)
            exception.Actions.Add(new EnrollmentExceptionAction { Action = EnrollmentExceptionActions.Reprocessed, Actor = actor, At = Now });
        try
        {
            await _exceptions.SaveAsync(exception, expected, ct);
        }
        catch (EnrollmentExceptionConcurrencyException)
        {
            return ExceptionActionResult.Changed();
        }

        var batch = new Enrollment834
        {
            BatchId = $"{exception.BatchId}-RP{exception.Attempts}",
            FileName = exception.FileName ?? string.Empty,
            EdiFileId = exception.EdiFileId,
            ActorId = actor,
            ReprocessExceptionId = exception.Id,
            ParsedAt = Now,
        };
        if (exception.SourceKind == EnrollmentExceptionSourceKind.Subscriber && exception.Subscriber is { } subscriber)
            batch.Enrollments.Add(subscriber);
        else if (exception.Dependent is { } dependent)
            batch.DependentEnrollments.Add(dependent);
        else
            return ExceptionActionResult.Bad("The exception has no change to reprocess.", exception);

        await _import.ImportEnrollmentAsync(batch, tenantId);
        return ExceptionActionResult.Ok((await _exceptions.GetAsync(tenantId, exception.Id, ct))!);
    }
}
