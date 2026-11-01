using System.Net;
using System.Net.Http.Json;
using CloudHealthOffice.Infrastructure.Data;
using CloudHealthOffice.Infrastructure.Logging;
using EncounterSubmissionService.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace EncounterSubmissionService.StateRoutes;

/// <summary>A staged state encounter file, kept in Mongo so nothing depends on blob storage being configured.</summary>
public sealed class StateEncounterFile
{
    [BsonId]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string TenantId { get; set; } = string.Empty;
    public string StateCode { get; set; } = string.Empty;
    public string Receiver { get; set; } = string.Empty;
    public string BatchId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FormType { get; set; } = string.Empty;
    public string InterchangeControlNumber { get; set; } = string.Empty;
    public string UsageIndicator { get; set; } = "T";
    public int TransactionCount { get; set; }
    public List<string> SubmissionIds { get; set; } = new();
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Set when the file has been handed to the state's transport (a later step).</summary>
    public DateTime? TransmittedAt { get; set; }
}

public sealed class StateEncounterBatchResult
{
    public string BatchId { get; init; } = string.Empty;
    public string StateCode { get; init; } = string.Empty;
    public List<StateEncounterFile> Files { get; init; } = new();
    public List<string> BatchedSubmissionIds { get; init; } = new();

    /// <summary>Submissions held back with <see cref="EncounterSubmissionStatus.ValidationFailed"/>, with why.</summary>
    public Dictionary<string, IReadOnlyList<string>> HeldBack { get; init; } = new();

    /// <summary>Replacements and voids left Pending until the state's reference for their original arrives.</summary>
    public List<string> WaitingOnOriginal { get; init; } = new();
}

public interface IStateEncounterBatchService
{
    /// <summary>
    /// Creates (or returns the existing) tracking record for a claim on a state route.
    /// Idempotent per tenant, state and claim, so a redelivered Kafka message is harmless.
    /// </summary>
    Task<EncounterSubmission> CreateSubmissionRecordAsync(
        StateEncounterRoute route, string claimId, string tenantId, DateTime adjudicatedAt);

    /// <summary>
    /// Builds and stores the 837 file(s) for the given submissions, one per form type.
    /// Encounters that fail validation are held back; everything else is marked Batched.
    /// A read failure from claims, member or provider service refuses the whole batch.
    /// </summary>
    Task<StateEncounterBatchResult> BuildBatchAsync(
        StateEncounterRoute route, IReadOnlyList<EncounterSubmission> submissions, string tenantId,
        CancellationToken ct = default);

    /// <summary>
    /// Submissions on a state route that are waiting to be batched, across tenants,
    /// for the background worker. Each is batched under its own tenant.
    /// </summary>
    Task<IReadOnlyList<EncounterSubmission>> GetBatchableAsync(string stateCode, int limit);

    Task<IReadOnlyList<StateEncounterFile>> ListFilesAsync(string tenantId, string? stateCode, int limit = 50);

    Task<StateEncounterFile?> GetFileAsync(string tenantId, string fileId);

    /// <summary>
    /// Records that a staged file was handed to the state's transport: the file gets
    /// <see cref="StateEncounterFile.TransmittedAt"/> and its Batched encounters become Submitted.
    /// Returns null when the file does not exist for the tenant.
    /// </summary>
    Task<StateEncounterFile?> MarkTransmittedAsync(string tenantId, string fileId, CancellationToken ct = default);
}

public interface IEncounterControlNumberSource
{
    /// <summary>Next ISA13 for a tenant's interchanges to one state, 1–999999999, wrapping.</summary>
    Task<long> NextInterchangeControlNumberAsync(string tenantId, string stateCode, CancellationToken ct = default);
}

public sealed class MongoEncounterControlNumberSource : IEncounterControlNumberSource
{
    private readonly IMongoCollection<BsonDocument> _counters;

    public MongoEncounterControlNumberSource(IMongoDatabase database)
    {
        _counters = database.GetCollection<BsonDocument>("encounter_control_numbers");
    }

    public async Task<long> NextInterchangeControlNumberAsync(string tenantId, string stateCode, CancellationToken ct = default)
    {
        var id = $"{tenantId}:{stateCode.ToUpperInvariant()}:ISA";
        var doc = await _counters.FindOneAndUpdateAsync(
            Builders<BsonDocument>.Filter.Eq("_id", id),
            Builders<BsonDocument>.Update.Inc("seq", 1L).SetOnInsert("tenantId", tenantId),
            new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After },
            ct);
        var seq = doc["seq"].ToInt64();
        return (seq - 1) % 999_999_999 + 1;
    }
}

public sealed class StateEncounterBatchService : IStateEncounterBatchService
{
    private static readonly EncounterSubmissionStatus[] Batchable =
    {
        EncounterSubmissionStatus.Pending,
        EncounterSubmissionStatus.DeadlineWarning,
        EncounterSubmissionStatus.Rejected
    };

    private readonly IMongoCollection<EncounterSubmission> _submissions;
    private readonly IMongoCollection<StateEncounterFile> _files;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IStateEncounterRouteRegistry _registry;
    private readonly IEncounterControlNumberSource _controlNumbers;
    private readonly ILogger<StateEncounterBatchService> _logger;
    private readonly TimeProvider _clock;

    public StateEncounterBatchService(
        IMongoDatabase database,
        IHttpClientFactory httpClientFactory,
        IStateEncounterRouteRegistry registry,
        IEncounterControlNumberSource controlNumbers,
        ILogger<StateEncounterBatchService> logger,
        TimeProvider? clock = null)
    {
        _submissions = database.GetCollection<EncounterSubmission>("encounter_submissions");
        _files = database.GetCollection<StateEncounterFile>("encounter_state_files");
        _httpClientFactory = httpClientFactory;
        _registry = registry;
        _controlNumbers = controlNumbers;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<EncounterSubmission> CreateSubmissionRecordAsync(
        StateEncounterRoute route, string claimId, string tenantId, DateTime adjudicatedAt)
    {
        if (route.Format != EncounterRouteFormat.X12_837)
            throw new ArgumentException($"{route.StateCode} is not an X12 route.", nameof(route));
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(claimId))
            throw new ArgumentException("A tenant and a claim are required.");

        var now = _clock.GetUtcNow().UtcDateTime;
        var submission = new EncounterSubmission
        {
            // Deterministic id: a redelivered adjudication message finds the same record.
            Id = $"{tenantId}:{route.StateCode}:{claimId}",
            TenantId = tenantId,
            ClaimId = claimId,
            ClaimAdjudicatedAt = adjudicatedAt,
            StateCode = route.StateCode,
            Receiver = route.Receiver,
            SubmissionDeadline = route.ComputeDeadline(adjudicatedAt, _registry.GetOptions(route.StateCode)),
            Status = EncounterSubmissionStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now
        };

        try
        {
            await _submissions.InsertOneAsync(submission);
            return submission;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            var existing = await _submissions.Find(Builders<EncounterSubmission>.Filter.And(
                Builders<EncounterSubmission>.Filter.Eq(s => s.Id, submission.Id),
                Builders<EncounterSubmission>.Filter.Eq(s => s.TenantId, tenantId))).FirstOrDefaultAsync();
            return existing ?? throw new InvalidOperationException(
                "Encounter submission id collides with a record in another tenant.");
        }
    }

    public async Task<StateEncounterBatchResult> BuildBatchAsync(
        StateEncounterRoute route, IReadOnlyList<EncounterSubmission> submissions, string tenantId,
        CancellationToken ct = default)
    {
        // ── Guards: refuse before any read or write ─────────────────────
        if (route.Format != EncounterRouteFormat.X12_837)
            throw new ArgumentException($"{route.StateCode} is not an X12 route.", nameof(route));
        if (string.IsNullOrWhiteSpace(tenantId) || submissions.Count == 0)
            throw new ArgumentException("A tenant and at least one submission are required.");
        if (submissions.Any(s => s.TenantId != tenantId))
            throw new InvalidOperationException("All submissions must belong to the requested tenant.");
        if (submissions.Any(s => !string.Equals(s.StateCode, route.StateCode, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"All submissions must be on the {route.StateCode} route.");
        if (submissions.Any(s => !Batchable.Contains(s.Status)))
            throw new InvalidOperationException("Only pending, warning or rejected submissions can be batched.");
        if (submissions.Any(s => string.IsNullOrWhiteSpace(s.Id) || string.IsNullOrWhiteSpace(s.ClaimId))
            || submissions.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != submissions.Count
            || submissions.Select(s => s.ClaimId).Distinct(StringComparer.Ordinal).Count() != submissions.Count)
            throw new ArgumentException("Submission and claim identifiers must be present and unique within a batch.");

        if (_registry.Resolve(route.StateCode, route.LineOfBusiness, tenantId, out var reason) is null)
            throw new InvalidOperationException($"The {route.StateCode} route cannot be used: {reason}.");
        var options = _registry.GetOptions(route.StateCode)!;
        var tenantOptions = _registry.GetTenantOptions(route.StateCode, tenantId)!;

        // ── Read every source record first; any read failure refuses the batch ──
        var claimsClient = _httpClientFactory.CreateClient("ClaimsService");
        var memberClient = _httpClientFactory.CreateClient("MemberService");
        var providerClient = _httpClientFactory.CreateClient("ProviderService");

        var mapped = new List<(EncounterSubmission Submission, EncounterClaim Claim)>();
        var heldBack = new Dictionary<string, IReadOnlyList<string>>();

        // ── Replacements and voids need the state's reference for the original ──
        var linked = await ResolveLinkedAsync(submissions, tenantId, ct);
        var waiting = new List<string>();

        foreach (var submission in submissions)
        {
            var link = linked.GetValueOrDefault(submission.Id);
            if (link is { Waiting: true })
            {
                waiting.Add(submission.Id);
                continue;
            }
            if (link is { CloseReason: { } closeReason })
            {
                await CloseLinkedAsync(submission, tenantId, closeReason, ct);
                continue;
            }
            if (link is { HoldReason: { } holdReason })
            {
                heldBack[submission.Id] = new[] { holdReason };
                continue;
            }

            var claim = await ReadRequiredAsync<ClaimRecord>(claimsClient,
                $"/api/claims/{Uri.EscapeDataString(submission.ClaimId)}", tenantId, "Claim", ct);
            if (claim.Id != submission.ClaimId)
                throw new InvalidOperationException("Claim response did not identify the requested claim; nothing was batched.");

            var member = string.IsNullOrWhiteSpace(claim.MemberId) ? null
                : await ReadOptionalAsync<MemberRecord>(memberClient,
                    $"/api/v1/members/{Uri.EscapeDataString(claim.MemberId)}", tenantId, "Member", ct);
            var billing = string.IsNullOrWhiteSpace(claim.BillingProviderNPI) ? null
                : await ReadOptionalAsync<ProviderRecord>(providerClient,
                    $"/api/v1/providers/npi/{Uri.EscapeDataString(claim.BillingProviderNPI)}", tenantId, "Provider", ct);

            var encounter = EncounterClaimMapper.Map(submission.Id, claim, member, billing) with
            {
                FrequencyCode = link?.FrequencyCode ?? "1",
                OriginalStateReference = link?.StateReference
            };
            var errors = EncounterValidator.Validate(encounter).ToList();
            if (member is null) errors.Insert(0, "member record not found in member-service");
            if (billing is null) errors.Insert(0, "billing provider not found in provider-service by NPI");

            if (errors.Count > 0) heldBack[submission.Id] = errors;
            else mapped.Add((submission, encounter));
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var batchId = Guid.NewGuid().ToString();

        // ── Hold back what failed validation ────────────────────────────
        foreach (var (submissionId, errors) in heldBack)
        {
            await _submissions.UpdateOneAsync(
                Builders<EncounterSubmission>.Filter.And(
                    Builders<EncounterSubmission>.Filter.Eq(s => s.Id, submissionId),
                    Builders<EncounterSubmission>.Filter.Eq(s => s.TenantId, tenantId),
                    Builders<EncounterSubmission>.Filter.In(s => s.Status, Batchable)),
                Builders<EncounterSubmission>.Update
                    .Set(s => s.Status, EncounterSubmissionStatus.ValidationFailed)
                    .Set(s => s.ValidationErrors, errors.ToList())
                    .Set(s => s.LastError, $"Held back by {route.StateCode} encounter validation: {errors[0]}")
                    .Set(s => s.UpdatedAt, now),
                cancellationToken: ct);
        }

        var result = new StateEncounterBatchResult
        {
            BatchId = batchId,
            StateCode = route.StateCode,
            HeldBack = heldBack,
            WaitingOnOriginal = waiting
        };

        // ── Write one interchange per form type ─────────────────────────
        foreach (var group in mapped.GroupBy(m => m.Claim.Form).OrderBy(g => g.Key))
        {
            var icn = await _controlNumbers.NextInterchangeControlNumberAsync(tenantId, route.StateCode, ct);
            var envelope = new Encounter837Envelope
            {
                SenderIdQualifier = tenantOptions.SubmitterIdQualifier,
                SenderId = tenantOptions.SubmitterId!,
                ReceiverIdQualifier = options.ReceiverIdQualifier,
                ReceiverId = options.ReceiverId!,
                SubmitterName = tenantOptions.SubmitterName!,
                ContactName = tenantOptions.ContactName,
                ContactPhone = tenantOptions.ContactPhone,
                ReceiverName = options.ReceiverName!,
                DestinationPayerName = options.DestinationPayerName!,
                DestinationPayerId = options.DestinationPayerId!,
                DestinationPayerResponsibility = options.DestinationPayerResponsibility,
                DestinationClaimFilingIndicator = options.DestinationClaimFilingIndicator,
                PlanName = tenantOptions.PlanName!,
                PlanPayerId = tenantOptions.PlanPayerId!,
                PlanPayerResponsibility = options.PlanPayerResponsibility,
                PlanClaimFilingIndicator = options.PlanClaimFilingIndicator,
                UsageIndicator = options.UsageIndicator,
                InterchangeControlNumber = icn,
                CreatedAtUtc = now
            };

            var written = Encounter837Writer.Write(group.Key, group.Select(g => g.Claim).ToList(), envelope);
            var formLabel = group.Key == EncounterFormType.Institutional ? "837I" : "837P";
            var file = new StateEncounterFile
            {
                TenantId = tenantId,
                StateCode = route.StateCode,
                Receiver = route.Receiver,
                BatchId = batchId,
                FormType = formLabel,
                InterchangeControlNumber = icn.ToString("D9"),
                UsageIndicator = envelope.UsageIndicator == "P" ? "P" : "T",
                TransactionCount = written.Placements.Count,
                SubmissionIds = written.Placements.Select(p => p.SubmissionId).ToList(),
                Content = written.Content,
                CreatedAt = now,
                FileName = FileName(route, tenantOptions, formLabel, envelope.UsageIndicator, now, icn)
            };

            await _files.InsertOneAsync(file, cancellationToken: ct);

            foreach (var placement in written.Placements)
            {
                var update = await _submissions.UpdateOneAsync(
                    Builders<EncounterSubmission>.Filter.And(
                        Builders<EncounterSubmission>.Filter.Eq(s => s.Id, placement.SubmissionId),
                        Builders<EncounterSubmission>.Filter.Eq(s => s.TenantId, tenantId),
                        Builders<EncounterSubmission>.Filter.In(s => s.Status, Batchable)),
                    Builders<EncounterSubmission>.Update
                        .Set(s => s.Status, EncounterSubmissionStatus.Batched)
                        .Set(s => s.BatchId, batchId)
                        .Set(s => s.Receiver, route.Receiver)
                        .Set(s => s.FormType, formLabel)
                        .Set(s => s.FileName, file.FileName)
                        .Set(s => s.InterchangeControlNumber, placement.InterchangeControlNumber)
                        .Set(s => s.GroupControlNumber, placement.GroupControlNumber)
                        .Set(s => s.TransactionSetControlNumber, placement.TransactionSetControlNumber)
                        .Set(s => s.PatientControlNumber, placement.PatientControlNumber)
                        .Set(s => s.FrequencyCode, group.First(g => g.Submission.Id == placement.SubmissionId).Claim.FrequencyCode)
                        .Set(s => s.ValidationErrors, new List<string>())
                        .Set(s => s.LastError, null)
                        .Set(s => s.UpdatedAt, now),
                    cancellationToken: ct);
                if (update.ModifiedCount == 1) result.BatchedSubmissionIds.Add(placement.SubmissionId);
                else
                    _logger.LogWarning(
                        "Submission {SubmissionId} changed state while batch {BatchId} was built; it stays out of the batch record",
                        LogSanitizer.Sanitize(placement.SubmissionId), batchId);
            }

            result.Files.Add(file);
        }

        _logger.LogInformation(
            "{State} encounter batch {BatchId} for tenant {TenantId}: {Files} file(s), {Batched} batched, {Held} held back",
            route.StateCode, batchId, LogSanitizer.Sanitize(tenantId), result.Files.Count,
            result.BatchedSubmissionIds.Count, heldBack.Count);

        return result;
    }

    [CrossTenantQuery("EncounterSubmissionWorker (background): state route batching across tenants; each tenant's records are batched under its own TenantId.")]
    public async Task<IReadOnlyList<EncounterSubmission>> GetBatchableAsync(string stateCode, int limit) =>
        await _submissions.Find(Builders<EncounterSubmission>.Filter.And(
                Builders<EncounterSubmission>.Filter.Eq(s => s.StateCode, stateCode.ToUpperInvariant()),
                Builders<EncounterSubmission>.Filter.In(s => s.Status, new[]
                {
                    EncounterSubmissionStatus.Pending,
                    EncounterSubmissionStatus.DeadlineWarning
                })))
            .SortBy(s => s.SubmissionDeadline)
            .Limit(Math.Clamp(limit, 1, 50_000))
            .ToListAsync();

    public async Task<IReadOnlyList<StateEncounterFile>> ListFilesAsync(string tenantId, string? stateCode, int limit = 50)
    {
        var filter = Builders<StateEncounterFile>.Filter.Eq(f => f.TenantId, tenantId);
        if (!string.IsNullOrWhiteSpace(stateCode))
            filter &= Builders<StateEncounterFile>.Filter.Eq(f => f.StateCode, stateCode.ToUpperInvariant());
        return await _files.Find(filter)
            .SortByDescending(f => f.CreatedAt)
            .Limit(Math.Clamp(limit, 1, 200))
            .Project<StateEncounterFile>(Builders<StateEncounterFile>.Projection.Exclude(f => f.Content))
            .ToListAsync();
    }

    public async Task<StateEncounterFile?> GetFileAsync(string tenantId, string fileId) =>
        await _files.Find(Builders<StateEncounterFile>.Filter.And(
            Builders<StateEncounterFile>.Filter.Eq(f => f.Id, fileId),
            Builders<StateEncounterFile>.Filter.Eq(f => f.TenantId, tenantId))).FirstOrDefaultAsync();

    public async Task<StateEncounterFile?> MarkTransmittedAsync(string tenantId, string fileId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var file = await _files.FindOneAndUpdateAsync(
            Builders<StateEncounterFile>.Filter.And(
                Builders<StateEncounterFile>.Filter.Eq(f => f.Id, fileId),
                Builders<StateEncounterFile>.Filter.Eq(f => f.TenantId, tenantId)),
            Builders<StateEncounterFile>.Update.Set(f => f.TransmittedAt, now),
            new FindOneAndUpdateOptions<StateEncounterFile>
            {
                ReturnDocument = ReturnDocument.After,
                Projection = Builders<StateEncounterFile>.Projection.Exclude(f => f.Content)
            },
            ct);
        if (file is null) return null;

        await _submissions.UpdateManyAsync(
            Builders<EncounterSubmission>.Filter.And(
                Builders<EncounterSubmission>.Filter.In(s => s.Id, file.SubmissionIds),
                Builders<EncounterSubmission>.Filter.Eq(s => s.TenantId, tenantId),
                Builders<EncounterSubmission>.Filter.Eq(s => s.FileName, file.FileName),
                Builders<EncounterSubmission>.Filter.Eq(s => s.Status, EncounterSubmissionStatus.Batched)),
            Builders<EncounterSubmission>.Update
                .Set(s => s.Status, EncounterSubmissionStatus.Submitted)
                .Set(s => s.SubmittedAt, now)
                .Set(s => s.UpdatedAt, now),
            cancellationToken: ct);
        return file;
    }

    private sealed record LinkResolution(
        string? FrequencyCode, string? StateReference, bool Waiting = false,
        string? CloseReason = null, string? HoldReason = null);

    /// <summary>
    /// For each replacement (7) or void (8): the state reference to put in REF*F8, or
    /// why it cannot go yet. An original the state rejected or that was closed was never
    /// accepted, so its replacement goes as a new original and its void is closed.
    /// </summary>
    private async Task<Dictionary<string, LinkResolution>> ResolveLinkedAsync(
        IReadOnlyList<EncounterSubmission> submissions, string tenantId, CancellationToken ct)
    {
        var result = new Dictionary<string, LinkResolution>(StringComparer.Ordinal);
        var linked = submissions.Where(s => s.FrequencyCode is "7" or "8").ToList();
        if (linked.Count == 0) return result;

        var originalIds = linked.Select(s => s.OriginalSubmissionId).Where(id => id is not null).Distinct().ToList();
        var originals = (await _submissions.Find(Builders<EncounterSubmission>.Filter.And(
                    Builders<EncounterSubmission>.Filter.In(s => s.Id, originalIds),
                    Builders<EncounterSubmission>.Filter.Eq(s => s.TenantId, tenantId)))
                .ToListAsync(ct))
            .ToDictionary(s => s.Id, StringComparer.Ordinal);

        foreach (var s in linked)
        {
            if (s.OriginalSubmissionId is null || !originals.TryGetValue(s.OriginalSubmissionId, out var original))
            {
                result[s.Id] = new LinkResolution(s.FrequencyCode, null,
                    HoldReason: "the original encounter record for this replacement or void was not found");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(original.StateClaimReference))
            {
                result[s.Id] = new LinkResolution(s.FrequencyCode, original.StateClaimReference);
            }
            else if (original.Status is EncounterSubmissionStatus.Rejected or EncounterSubmissionStatus.ClosedNotReportable)
            {
                result[s.Id] = s.FrequencyCode == "7"
                    ? new LinkResolution("1", null)
                    : new LinkResolution("8", null, CloseReason: "the state never accepted the original encounter, so there is nothing to void");
            }
            else
            {
                result[s.Id] = new LinkResolution(s.FrequencyCode, null, Waiting: true);
            }
        }
        return result;
    }

    private async Task CloseLinkedAsync(EncounterSubmission submission, string tenantId, string reason, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await _submissions.UpdateOneAsync(
            Builders<EncounterSubmission>.Filter.And(
                Builders<EncounterSubmission>.Filter.Eq(s => s.Id, submission.Id),
                Builders<EncounterSubmission>.Filter.Eq(s => s.TenantId, tenantId),
                Builders<EncounterSubmission>.Filter.In(s => s.Status, Batchable)),
            Builders<EncounterSubmission>.Update
                .Set(s => s.Status, EncounterSubmissionStatus.ClosedNotReportable)
                .Set(s => s.LastError, $"Closed without reporting: {reason}")
                .Set(s => s.UpdatedAt, now)
                .Push(s => s.ReworkHistory, new EncounterReworkEvent
                {
                    Action = "Closed", ReasonCode = "ORIGINAL_NOT_ACCEPTED", ActorId = "batch", Note = reason, At = now
                }),
            cancellationToken: ct);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    internal static string FileName(StateEncounterRoute route, StateEncounterTenantOptions tenant,
        string formLabel, string usage, DateTime now, long icn)
    {
        var who = Safe(tenant.PlanCode) is { Length: > 0 } code ? code : Safe(tenant.SubmitterId);
        var mode = usage == "P" ? "PROD" : "TEST";
        return $"{route.FilePrefix}_{who}_{formLabel}_{mode}_{now:yyyyMMddHHmmss}_{icn:D9}.x12";
    }

    private static string Safe(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty
            : new string(value.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());

    private async Task<T> ReadRequiredAsync<T>(HttpClient client, string path, string tenantId, string what,
        CancellationToken ct) where T : class =>
        await ReadAsync<T>(client, path, tenantId, what, notFoundIsNull: false, ct)
        ?? throw new InvalidOperationException($"{what} response was empty; nothing was batched.");

    private Task<T?> ReadOptionalAsync<T>(HttpClient client, string path, string tenantId, string what,
        CancellationToken ct) where T : class =>
        ReadAsync<T>(client, path, tenantId, what, notFoundIsNull: true, ct);

    private async Task<T?> ReadAsync<T>(HttpClient client, string path, string tenantId, string what,
        bool notFoundIsNull, CancellationToken ct) where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Tenant-ID", tenantId);
        using var response = await client.SendAsync(request, ct);
        if (notFoundIsNull && response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("{What} read failed while building a state encounter batch: {StatusCode}",
                what, response.StatusCode);
            throw new HttpRequestException($"{what} data unavailable; no encounter records were batched.",
                null, response.StatusCode);
        }
        return await response.Content.ReadFromJsonAsync<T>(EncounterClaimMapper.JsonOptions, ct);
    }
}
