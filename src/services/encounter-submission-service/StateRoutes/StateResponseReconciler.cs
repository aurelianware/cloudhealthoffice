using CloudHealthOffice.Infrastructure.Logging;
using EncounterSubmissionService.Models;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace EncounterSubmissionService.StateRoutes;

/// <summary>A state response file as received, with what it was matched to.</summary>
public sealed class StateEncounterResponse
{
    [BsonId]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string TenantId { get; set; } = string.Empty;
    public string StateCode { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string? FileName { get; set; }
    public DateTime ReceivedAt { get; set; }
    public string Content { get; set; } = string.Empty;
    public int Matched { get; set; }
    public int Accepted { get; set; }
    public int Rejected { get; set; }

    /// <summary>References in the file that matched no submission (ST02 for a 999, CLM01 for a 277).</summary>
    public List<string> Unmatched { get; set; } = new();
}

public sealed class StateResponseResult
{
    public string ResponseId { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public int Matched { get; set; }
    public int Accepted { get; set; }
    public int Rejected { get; set; }
    public List<string> Unmatched { get; init; } = new();
}

public interface IStateResponseReconciler
{
    /// <summary>
    /// Applies a 999 or 277CA / 277 from the state to the tenant's encounters on that
    /// route, record by record, and stores the file. Throws <see cref="NotSupportedException"/>
    /// for a format not handled yet (the DHCS EVR XML file).
    /// </summary>
    Task<StateResponseResult> ApplyAsync(
        StateEncounterRoute route, string tenantId, string? fileName, string content, CancellationToken ct = default);
}

public sealed class StateResponseReconciler : IStateResponseReconciler
{
    // A 999 speaks only to records still waiting on their syntax acknowledgment.
    private static readonly EncounterSubmissionStatus[] Awaiting999 =
    {
        EncounterSubmissionStatus.Batched,
        EncounterSubmissionStatus.Submitted
    };

    // A 277 may arrive before or after its 999, and may repeat.
    private static readonly EncounterSubmissionStatus[] Awaiting277 =
    {
        EncounterSubmissionStatus.Batched,
        EncounterSubmissionStatus.Submitted,
        EncounterSubmissionStatus.SyntaxAccepted,
        EncounterSubmissionStatus.SyntaxAcceptedWithErrors,
        EncounterSubmissionStatus.StateFrontEndAccepted
    };

    private readonly IMongoCollection<EncounterSubmission> _submissions;
    private readonly IMongoCollection<StateEncounterResponse> _responses;
    private readonly ILogger<StateResponseReconciler> _logger;
    private readonly TimeProvider _clock;

    public StateResponseReconciler(IMongoDatabase database, ILogger<StateResponseReconciler> logger,
        TimeProvider? clock = null)
    {
        _submissions = database.GetCollection<EncounterSubmission>("encounter_submissions");
        _responses = database.GetCollection<StateEncounterResponse>("encounter_state_responses");
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<StateResponseResult> ApplyAsync(
        StateEncounterRoute route, string tenantId, string? fileName, string content, CancellationToken ct = default)
    {
        if (route.Format != EncounterRouteFormat.X12_837)
            throw new ArgumentException($"{route.StateCode} responses use the FMMIS acknowledgment endpoint.", nameof(route));
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("A tenant is required.", nameof(tenantId));
        if (content.TrimStart().StartsWith('<'))
            throw new NotSupportedException(
                "XML response files (the DHCS Encounter Validation Response) are not reconciled yet; " +
                "the EVR schema from the DHCS companion guide is needed first.");

        var x12 = X12Segments.Parse(content);
        var kind = x12.TransactionType switch
        {
            "999" => "999",
            "277" => route.ResponseFiles.Contains("277CA") ? "277CA" : "277",
            var other => throw new NotSupportedException($"Transaction set {other} is not a response this route reconciles.")
        };

        var now = _clock.GetUtcNow().UtcDateTime;
        var response = new StateEncounterResponse
        {
            TenantId = tenantId,
            StateCode = route.StateCode,
            Kind = kind,
            FileName = fileName,
            ReceivedAt = now,
            Content = content
        };
        var result = new StateResponseResult { ResponseId = response.Id, Kind = kind };

        if (kind == "999") await Apply999Async(route, tenantId, x12, response, result, now, ct);
        else await Apply277Async(route, tenantId, x12, response, result, now, ct);

        response.Matched = result.Matched;
        response.Accepted = result.Accepted;
        response.Rejected = result.Rejected;
        response.Unmatched = result.Unmatched;
        await _responses.InsertOneAsync(response, cancellationToken: ct);

        _logger.LogInformation(
            "{State} {Kind} for tenant {TenantId}: {Matched} matched ({Accepted} accepted, {Rejected} rejected), {Unmatched} unmatched",
            route.StateCode, kind, LogSanitizer.Sanitize(tenantId), result.Matched, result.Accepted,
            result.Rejected, result.Unmatched.Count);
        return result;
    }

    private async Task Apply999Async(StateEncounterRoute route, string tenantId, X12Segments x12,
        StateEncounterResponse response, StateResponseResult result, DateTime now, CancellationToken ct)
    {
        foreach (var group in Ack999Parser.Parse(x12))
        {
            var submissions = await _submissions.Find(Builders<EncounterSubmission>.Filter.And(
                    Builders<EncounterSubmission>.Filter.Eq(s => s.TenantId, tenantId),
                    Builders<EncounterSubmission>.Filter.Eq(s => s.StateCode, route.StateCode),
                    Builders<EncounterSubmission>.Filter.Eq(s => s.GroupControlNumber, group.GroupControlNumber),
                    Builders<EncounterSubmission>.Filter.In(s => s.Status, Awaiting999)))
                .ToListAsync(ct);

            var byStControl = group.Transactions.ToDictionary(t => t.TransactionSetControlNumber, StringComparer.Ordinal);
            foreach (var t in group.Transactions)
                if (!submissions.Any(s => s.TransactionSetControlNumber == t.TransactionSetControlNumber))
                    result.Unmatched.Add($"GS06 {group.GroupControlNumber} ST02 {t.TransactionSetControlNumber}");
            if (submissions.Count == 0 && group.Transactions.Count == 0)
                result.Unmatched.Add($"GS06 {group.GroupControlNumber}");

            foreach (var submission in submissions)
            {
                // A transaction set the 999 does not list takes the group's code: accepted
                // under A / E / P, rejected under R / M / W / X.
                var (code, errors) = submission.TransactionSetControlNumber is { } st && byStControl.TryGetValue(st, out var t)
                    ? (t.Code, t.Errors)
                    : (group.GroupCode is "A" or "E" or "P" ? "A" : group.GroupCode, (IReadOnlyList<string>)Array.Empty<string>());

                var status = code switch
                {
                    "A" => EncounterSubmissionStatus.SyntaxAccepted,
                    "E" => EncounterSubmissionStatus.SyntaxAcceptedWithErrors,
                    _ => EncounterSubmissionStatus.Rejected
                };
                var codes = new List<string> { $"IK5:{code}" };
                var lastError = status == EncounterSubmissionStatus.Rejected
                    ? $"{route.Receiver} 999 rejected the transaction ({code}){(errors.Count > 0 ? ": " + string.Join("; ", errors) : "")}"
                    : null;

                if (await UpdateAsync(submission, tenantId, Awaiting999, status, response, codes, errors.ToList(),
                        lastError, null, now, ct))
                    Count(result, status);
            }
        }
    }

    private async Task Apply277Async(StateEncounterRoute route, string tenantId, X12Segments x12,
        StateEncounterResponse response, StateResponseResult result, DateTime now, CancellationToken ct)
    {
        foreach (var ack in Ack277Parser.Parse(x12))
        {
            var submission = await _submissions.Find(Builders<EncounterSubmission>.Filter.And(
                    Builders<EncounterSubmission>.Filter.Eq(s => s.TenantId, tenantId),
                    Builders<EncounterSubmission>.Filter.Eq(s => s.StateCode, route.StateCode),
                    Builders<EncounterSubmission>.Filter.Eq(s => s.PatientControlNumber, ack.PatientControlNumber),
                    Builders<EncounterSubmission>.Filter.In(s => s.Status, Awaiting277)))
                .SortByDescending(s => s.UpdatedAt)
                .FirstOrDefaultAsync(ct);

            if (submission is null)
            {
                result.Unmatched.Add($"CLM01 {ack.PatientControlNumber}");
                continue;
            }

            EncounterSubmissionStatus status;
            string? lastError = null;
            if (ack.IsRejected)
            {
                status = EncounterSubmissionStatus.Rejected;
                lastError = $"{route.Receiver} claim acknowledgment rejected the encounter: {string.Join(", ", ack.StatusCodes)}";
            }
            else if (ack.IsAccepted)
            {
                status = EncounterSubmissionStatus.StateFrontEndAccepted;
            }
            else
            {
                // Pended or informational: keep the current status, record the codes.
                status = submission.Status;
            }

            if (await UpdateAsync(submission, tenantId, Awaiting277, status, response, ack.StatusCodes.ToList(),
                    new List<string>(), lastError, ack.StateClaimReference, now, ct))
                Count(result, status);
        }
    }

    private async Task<bool> UpdateAsync(EncounterSubmission submission, string tenantId,
        EncounterSubmissionStatus[] allowedFrom, EncounterSubmissionStatus status, StateEncounterResponse response,
        List<string> codes, List<string> errors, string? lastError, string? stateReference, DateTime now,
        CancellationToken ct)
    {
        var update = Builders<EncounterSubmission>.Update
            .Set(s => s.Status, status)
            .Set(s => s.StateStatusCodes, codes)
            .Set(s => s.AcknowledgedAt, now)
            .Set(s => s.UpdatedAt, now)
            .Push(s => s.ResponseHistory, new EncounterResponseEvent
            {
                ResponseId = response.Id,
                Kind = response.Kind,
                ReceivedAt = now,
                StatusCodes = codes,
                ResultingStatus = status,
                Errors = errors
            });
        if (response.Kind == "999") update = update.Set(s => s.AcknowledgmentCode, codes[0].Split(':')[^1]);
        if (lastError is not null) update = update.Set(s => s.LastError, lastError);
        if (status == EncounterSubmissionStatus.StateFrontEndAccepted) update = update.Set(s => s.LastError, null);
        if (!string.IsNullOrWhiteSpace(stateReference)) update = update.Set(s => s.StateClaimReference, stateReference);

        var written = await _submissions.UpdateOneAsync(
            Builders<EncounterSubmission>.Filter.And(
                Builders<EncounterSubmission>.Filter.Eq(s => s.Id, submission.Id),
                Builders<EncounterSubmission>.Filter.Eq(s => s.TenantId, tenantId),
                Builders<EncounterSubmission>.Filter.In(s => s.Status, allowedFrom)),
            update, cancellationToken: ct);
        return written.ModifiedCount == 1;
    }

    private static void Count(StateResponseResult result, EncounterSubmissionStatus status)
    {
        result.Matched++;
        if (status == EncounterSubmissionStatus.Rejected) result.Rejected++;
        else if (status is EncounterSubmissionStatus.SyntaxAccepted or EncounterSubmissionStatus.SyntaxAcceptedWithErrors
                 or EncounterSubmissionStatus.StateFrontEndAccepted) result.Accepted++;
    }
}
