using CloudHealthOffice.Infrastructure.Logging;
using EncounterSubmissionService.Models;
using MongoDB.Driver;

namespace EncounterSubmissionService.StateRoutes;

/// <summary>Why an encounter is in the rework queue.</summary>
public static class EncounterReworkReasons
{
    /// <summary>Held back before sending: failed implementation-guide validation.</summary>
    public const string Validation = "VALIDATION";

    /// <summary>The state's 999 rejected the transaction set (syntax).</summary>
    public const string SyntaxRejected = "SYNTAX_REJECTED";

    /// <summary>The state's claim acknowledgment (277CA / 277) rejected the encounter.</summary>
    public const string StateRejected = "STATE_REJECTED";

    public static string For(EncounterSubmission s) => s.Status switch
    {
        EncounterSubmissionStatus.ValidationFailed => Validation,
        EncounterSubmissionStatus.Rejected when s.ResponseHistory.LastOrDefault()?.Kind == "999" => SyntaxRejected,
        _ => StateRejected
    };
}

/// <summary>One encounter waiting for rework, with everything the analyst needs to act.</summary>
public sealed class EncounterReworkItem
{
    public string SubmissionId { get; set; } = string.Empty;
    public string ClaimId { get; set; } = string.Empty;
    public string StateCode { get; set; } = string.Empty;
    public string? Receiver { get; set; }
    public string ReasonCode { get; set; } = string.Empty;
    public EncounterSubmissionStatus Status { get; set; }
    public List<string> Reasons { get; set; } = new();
    public List<string> StateStatusCodes { get; set; } = new();
    public string? PatientControlNumber { get; set; }
    public string? FileName { get; set; }
    public DateTime SubmissionDeadline { get; set; }
    public double DaysToDeadline { get; set; }
    public int RetryCount { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class EncounterReworkPage
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public long TotalCount { get; set; }
    public List<EncounterReworkItem> Items { get; set; } = new();
}

public enum ReworkOutcome
{
    Done,
    NotFound,
    NotInRework,
    ReasonRequired
}

public interface IEncounterReworkService
{
    /// <summary>The tenant's encounters needing rework, nearest deadline first.</summary>
    Task<EncounterReworkPage> ListAsync(string tenantId, string? stateCode, string? reasonCode,
        int page, int pageSize, CancellationToken ct = default);

    /// <summary>
    /// Sends a fixed encounter back to Pending, so the next batch re-reads the claim,
    /// member and provider and validates it again. A state-rejected encounter goes back
    /// as a new original, which is how a rejected (never accepted) encounter is corrected.
    /// </summary>
    Task<(ReworkOutcome Outcome, EncounterSubmission? Submission)> RequeueAsync(
        string tenantId, string submissionId, string actorId, string? note, CancellationToken ct = default);

    /// <summary>Closes an encounter that should not be reported (duplicate, not state-reportable). A reason is required.</summary>
    Task<(ReworkOutcome Outcome, EncounterSubmission? Submission)> CloseAsync(
        string tenantId, string submissionId, string actorId, string? reason, CancellationToken ct = default);
}

public sealed class EncounterReworkService : IEncounterReworkService
{
    internal static readonly EncounterSubmissionStatus[] InRework =
    {
        EncounterSubmissionStatus.ValidationFailed,
        EncounterSubmissionStatus.Rejected
    };

    private readonly IMongoCollection<EncounterSubmission> _submissions;
    private readonly ILogger<EncounterReworkService> _logger;
    private readonly TimeProvider _clock;

    public EncounterReworkService(IMongoDatabase database, ILogger<EncounterReworkService> logger, TimeProvider? clock = null)
    {
        _submissions = database.GetCollection<EncounterSubmission>("encounter_submissions");
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    private static FilterDefinition<EncounterSubmission> ReworkFilter(string tenantId, string? stateCode) =>
        Builders<EncounterSubmission>.Filter.And(
            Builders<EncounterSubmission>.Filter.Eq(s => s.TenantId, tenantId),
            Builders<EncounterSubmission>.Filter.In(s => s.Status, InRework),
            // Florida rejections keep their existing retry endpoint; this queue is the state X12 routes.
            Builders<EncounterSubmission>.Filter.Ne(s => s.StateCode, "FL"),
            string.IsNullOrWhiteSpace(stateCode)
                ? Builders<EncounterSubmission>.Filter.Empty
                : Builders<EncounterSubmission>.Filter.Eq(s => s.StateCode, stateCode.ToUpperInvariant()));

    public async Task<EncounterReworkPage> ListAsync(string tenantId, string? stateCode, string? reasonCode,
        int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);
        var now = _clock.GetUtcNow().UtcDateTime;

        var filter = ReworkFilter(tenantId, stateCode);
        // The reason is derived from status and last response; narrow by status first.
        if (reasonCode == EncounterReworkReasons.Validation)
            filter &= Builders<EncounterSubmission>.Filter.Eq(s => s.Status, EncounterSubmissionStatus.ValidationFailed);
        else if (reasonCode is EncounterReworkReasons.SyntaxRejected or EncounterReworkReasons.StateRejected)
            filter &= Builders<EncounterSubmission>.Filter.Eq(s => s.Status, EncounterSubmissionStatus.Rejected);

        var all = await _submissions.Find(filter).SortBy(s => s.SubmissionDeadline).ToListAsync(ct);
        var items = all
            .Where(s => string.IsNullOrWhiteSpace(reasonCode) || EncounterReworkReasons.For(s) == reasonCode)
            .ToList();

        return new EncounterReworkPage
        {
            Page = page,
            PageSize = pageSize,
            TotalCount = items.Count,
            Items = items.Skip((page - 1) * pageSize).Take(pageSize).Select(s => new EncounterReworkItem
            {
                SubmissionId = s.Id,
                ClaimId = s.ClaimId,
                StateCode = s.StateCode,
                Receiver = s.Receiver,
                ReasonCode = EncounterReworkReasons.For(s),
                Status = s.Status,
                Reasons = s.Status == EncounterSubmissionStatus.ValidationFailed && s.ValidationErrors.Count > 0
                    ? s.ValidationErrors.ToList()
                    : (s.ResponseHistory.LastOrDefault()?.Errors is { Count: > 0 } errors
                        ? errors.ToList()
                        : new List<string> { s.LastError ?? "rejected" }),
                StateStatusCodes = s.StateStatusCodes.ToList(),
                PatientControlNumber = s.PatientControlNumber,
                FileName = s.FileName,
                SubmissionDeadline = s.SubmissionDeadline,
                DaysToDeadline = Math.Round((s.SubmissionDeadline - now).TotalDays, 1),
                RetryCount = s.RetryCount,
                UpdatedAt = s.UpdatedAt
            }).ToList()
        };
    }

    public async Task<(ReworkOutcome Outcome, EncounterSubmission? Submission)> RequeueAsync(
        string tenantId, string submissionId, string actorId, string? note, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var current = await FindAsync(tenantId, submissionId, ct);
        if (current is null) return (ReworkOutcome.NotFound, null);
        if (!InRework.Contains(current.Status) || current.StateCode == "FL") return (ReworkOutcome.NotInRework, current);

        var updated = await _submissions.FindOneAndUpdateAsync(
            Builders<EncounterSubmission>.Filter.And(
                Builders<EncounterSubmission>.Filter.Eq(s => s.Id, submissionId),
                Builders<EncounterSubmission>.Filter.Eq(s => s.TenantId, tenantId),
                Builders<EncounterSubmission>.Filter.In(s => s.Status, InRework)),
            Builders<EncounterSubmission>.Update
                .Set(s => s.Status, EncounterSubmissionStatus.Pending)
                .Inc(s => s.RetryCount, 1)
                .Set(s => s.BatchId, null)
                .Set(s => s.FileName, null)
                .Set(s => s.InterchangeControlNumber, null)
                .Set(s => s.GroupControlNumber, null)
                .Set(s => s.TransactionSetControlNumber, null)
                .Set(s => s.ValidationErrors, new List<string>())
                .Set(s => s.LastError, null)
                .Set(s => s.UpdatedAt, now)
                .Push(s => s.ReworkHistory, new EncounterReworkEvent
                {
                    Action = "Requeued",
                    ReasonCode = EncounterReworkReasons.For(current),
                    ActorId = actorId,
                    Note = Trim(note),
                    At = now
                }),
            new FindOneAndUpdateOptions<EncounterSubmission> { ReturnDocument = ReturnDocument.After },
            ct);

        if (updated is null) return (ReworkOutcome.NotInRework, current);
        _logger.LogInformation("Encounter {SubmissionId} requeued for {State} after rework",
            LogSanitizer.Sanitize(submissionId), updated.StateCode);
        return (ReworkOutcome.Done, updated);
    }

    public async Task<(ReworkOutcome Outcome, EncounterSubmission? Submission)> CloseAsync(
        string tenantId, string submissionId, string actorId, string? reason, CancellationToken ct = default)
    {
        var why = Trim(reason);
        if (string.IsNullOrWhiteSpace(why)) return (ReworkOutcome.ReasonRequired, null);

        var now = _clock.GetUtcNow().UtcDateTime;
        var current = await FindAsync(tenantId, submissionId, ct);
        if (current is null) return (ReworkOutcome.NotFound, null);
        if (!InRework.Contains(current.Status) || current.StateCode == "FL") return (ReworkOutcome.NotInRework, current);

        var updated = await _submissions.FindOneAndUpdateAsync(
            Builders<EncounterSubmission>.Filter.And(
                Builders<EncounterSubmission>.Filter.Eq(s => s.Id, submissionId),
                Builders<EncounterSubmission>.Filter.Eq(s => s.TenantId, tenantId),
                Builders<EncounterSubmission>.Filter.In(s => s.Status, InRework)),
            Builders<EncounterSubmission>.Update
                .Set(s => s.Status, EncounterSubmissionStatus.ClosedNotReportable)
                .Set(s => s.LastError, $"Closed without reporting: {why}")
                .Set(s => s.UpdatedAt, now)
                .Push(s => s.ReworkHistory, new EncounterReworkEvent
                {
                    Action = "Closed",
                    ReasonCode = EncounterReworkReasons.For(current),
                    ActorId = actorId,
                    Note = why,
                    At = now
                }),
            new FindOneAndUpdateOptions<EncounterSubmission> { ReturnDocument = ReturnDocument.After },
            ct);

        if (updated is null) return (ReworkOutcome.NotInRework, current);
        _logger.LogInformation("Encounter {SubmissionId} closed without reporting", LogSanitizer.Sanitize(submissionId));
        return (ReworkOutcome.Done, updated);
    }

    private Task<EncounterSubmission?> FindAsync(string tenantId, string submissionId, CancellationToken ct) =>
        _submissions.Find(Builders<EncounterSubmission>.Filter.And(
                Builders<EncounterSubmission>.Filter.Eq(s => s.Id, submissionId),
                Builders<EncounterSubmission>.Filter.Eq(s => s.TenantId, tenantId)))
            .FirstOrDefaultAsync(ct)!;

    private static string? Trim(string? value)
    {
        var t = value?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length > 1000 ? t[..1000] : t;
    }
}
