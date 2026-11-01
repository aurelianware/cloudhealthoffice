using CloudHealthOffice.Infrastructure.Data;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EncounterSubmissionService.Models;
using MongoDB.Driver;
using CloudHealthOffice.Infrastructure.Logging;

namespace EncounterSubmissionService.Services;

/// <summary>
/// Manages the lifecycle of FMMIS encounter submissions: tracking records,
/// batching, acknowledgment processing, and deadline monitoring.
/// Persists to MongoDB and calls claims-service / reference-data-service via HTTP.
/// </summary>
public class EncounterSubmissionServiceImpl : IEncounterSubmissionService
{
    private readonly IMongoCollection<EncounterSubmission> _collection;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<EncounterSubmissionServiceImpl> _logger;

    private const int DefaultSubmissionWindowDays = 60;
    private const int MaxRetryCount = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public EncounterSubmissionServiceImpl(
        IMongoDatabase database,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<EncounterSubmissionServiceImpl> logger)
    {
        _collection = database.GetCollection<EncounterSubmission>("encounter_submissions");
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    // ── Method 1: CreateSubmissionRecord ─────────────────────────────

    public async Task<EncounterSubmission> CreateSubmissionRecordAsync(
        string claimId, string tenantId, DateTime adjudicatedAt)
    {
        // Look up TenantComplianceConfig to get EncounterSubmissionDays
        var submissionDays = await GetEncounterSubmissionDaysAsync(tenantId);

        var submission = new EncounterSubmission
        {
            TenantId = tenantId,
            ClaimId = claimId,
            ClaimAdjudicatedAt = adjudicatedAt,
            StateCode = "FL",
            SubmissionDeadline = adjudicatedAt.AddDays(submissionDays),
            Status = EncounterSubmissionStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _collection.InsertOneAsync(submission);

        _logger.LogInformation(
            "Created encounter submission {Id} for claim {ClaimId}, tenant {TenantId}, " +
            "deadline {Deadline:yyyy-MM-dd} ({Days} days from adjudication)",
            submission.Id, claimId, tenantId, submission.SubmissionDeadline, submissionDays);

        return submission;
    }

    // ── Method 2: GetPendingSubmissions ──────────────────────────────

    public async Task<IEnumerable<EncounterSubmission>> GetPendingSubmissionsAsync(
        string tenantId, int page = 1, int pageSize = 50)
    {
        var filter = Builders<EncounterSubmission>.Filter.And(
            Builders<EncounterSubmission>.Filter.Eq(s => s.TenantId, tenantId),
            Builders<EncounterSubmission>.Filter.In(s => s.Status, new[]
            {
                EncounterSubmissionStatus.Pending,
                EncounterSubmissionStatus.DeadlineWarning,
                EncounterSubmissionStatus.Rejected
            }),
            Builders<EncounterSubmission>.Filter.Lt(s => s.RetryCount, MaxRetryCount)
        );

        var sort = Builders<EncounterSubmission>.Sort.Ascending(s => s.SubmissionDeadline);
        var skip = (page - 1) * pageSize;

        var submissions = await _collection
            .Find(filter)
            .Sort(sort)
            .Skip(skip)
            .Limit(pageSize)
            .ToListAsync();

        _logger.LogInformation(
            "Found {Count} pending submissions for tenant {TenantId} (page {Page})",
            submissions.Count, LogSanitizer.Sanitize(tenantId), page);

        return submissions;
    }

    public async Task<EncounterSubmission?> GetByIdAsync(string id, string tenantId)
    {
        var filter = Builders<EncounterSubmission>.Filter.And(
            Builders<EncounterSubmission>.Filter.Eq(s => s.Id, id),
            Builders<EncounterSubmission>.Filter.Eq(s => s.TenantId, tenantId)
        );

        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    [CrossTenantQuery("EncounterSubmissionWorker (background): deadline sweep across tenants; each flagged submission is updated under its own TenantId.")]
    public async Task<IEnumerable<EncounterSubmission>> GetApproachingDeadlineAsync(int warningDays = 7)
    {
        var warningCutoff = DateTime.UtcNow.AddDays(warningDays);

        var filter = Builders<EncounterSubmission>.Filter.And(
            Builders<EncounterSubmission>.Filter.In(s => s.Status, new[]
            {
                EncounterSubmissionStatus.Pending,
                EncounterSubmissionStatus.DeadlineWarning
            }),
            Builders<EncounterSubmission>.Filter.Lte(s => s.SubmissionDeadline, warningCutoff),
            Builders<EncounterSubmission>.Filter.Gt(s => s.SubmissionDeadline, DateTime.UtcNow)
        );

        var sort = Builders<EncounterSubmission>.Sort.Ascending(s => s.SubmissionDeadline);

        return await _collection.Find(filter).Sort(sort).ToListAsync();
    }

    // ── Method 3: BuildFmmisSubmissionBatch ──────────────────────────

    public async Task<FmmisSubmissionFileDto> BuildFmmisSubmissionBatchAsync(
        IEnumerable<EncounterSubmission> submissions, string tenantId)
    {
        var submissionList = submissions.ToList();
        if (string.IsNullOrWhiteSpace(tenantId) || submissionList.Count == 0)
            throw new ArgumentException("A tenant and at least one submission are required.");
        if (submissionList.Any(s => s.TenantId != tenantId))
            throw new InvalidOperationException("All submissions must belong to the requested tenant.");
        if (submissionList.Any(s => !string.Equals(s.StateCode, "FL", StringComparison.OrdinalIgnoreCase)))
            throw new NotSupportedException("The FMMIS batch builder supports Florida only; California requires a separately accepted DHCS EDI path.");
        if (submissionList.Any(s => s.Status is not (EncounterSubmissionStatus.Pending
                or EncounterSubmissionStatus.DeadlineWarning or EncounterSubmissionStatus.Rejected)))
            throw new InvalidOperationException("Only pending, warning or rejected submissions can be batched.");
        if (submissionList.Any(s => string.IsNullOrWhiteSpace(s.Id) || string.IsNullOrWhiteSpace(s.ClaimId))
            || submissionList.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != submissionList.Count
            || submissionList.Select(s => s.ClaimId).Distinct(StringComparer.Ordinal).Count() != submissionList.Count)
            throw new ArgumentException("Submission and claim identifiers must be present and unique within a batch.");
        var batchId = Guid.NewGuid().ToString();

        _logger.LogInformation(
            "Building FMMIS batch {BatchId} with {Count} encounters for tenant {TenantId}",
            batchId, submissionList.Count, tenantId);

        // Call claims-service to fetch each claim individually
        var claimsClient = _httpClientFactory.CreateClient("ClaimsService");
        var claimIds = submissionList.Select(s => s.ClaimId).ToList();

        var claimDataList = new List<JsonElement>();
        foreach (var claimId in claimIds)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/claims/{Uri.EscapeDataString(claimId)}");
            request.Headers.Add("X-Tenant-ID", tenantId);

            using var response = await claimsClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Claims service read failed while building an encounter batch: {StatusCode}",
                    response.StatusCode);
                // Never mark missing claims as batched alongside the ones we
                // happened to fetch. Leave every record retryable on failure.
                throw new HttpRequestException("Claim data unavailable; no encounter records were batched.",
                    null, response.StatusCode);
            }

            var claimData = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            if (claimData.ValueKind != JsonValueKind.Object
                || !claimData.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.String || id.GetString() != claimId)
                throw new JsonException("Claim response did not identify the requested claim; no encounter records were batched.");
            claimDataList.Add(claimData);
        }

        if (claimDataList.Count == 0)
        {
            throw new InvalidOperationException(
                "Claims service returned no valid claims for FMMIS batch");
        }

        // Build the batch file locally from individual claim data
        var batchContent = JsonSerializer.SerializeToUtf8Bytes(claimDataList, JsonOptions);
        var fileName = $"FMMIS_{tenantId}_{batchId}_{DateTime.UtcNow:yyyyMMddHHmmss}.json";

        var fileResult = new FmmisSubmissionFileDto
        {
            BatchId = batchId,
            FileName = fileName,
            Content = batchContent,
            TransactionCount = claimDataList.Count,
            ClaimIds = claimIds
        };

        // Update all submissions to Batched status
        var submissionIds = submissionList.Select(s => s.Id).ToList();
        var updateFilter = Builders<EncounterSubmission>.Filter.And(
            Builders<EncounterSubmission>.Filter.In(s => s.Id, submissionIds),
            Builders<EncounterSubmission>.Filter.Eq(s => s.TenantId, tenantId)
        );
        var updateDef = Builders<EncounterSubmission>.Update
            .Set(s => s.Status, EncounterSubmissionStatus.Batched)
            .Set(s => s.BatchId, batchId)
            .Set(s => s.UpdatedAt, DateTime.UtcNow);

        var updateResult = await _collection.UpdateManyAsync(updateFilter, updateDef);

        _logger.LogInformation(
            "FMMIS batch {BatchId} built: {FileName}, {TransactionCount} transactions, " +
            "{UpdatedCount} submissions marked Batched",
            batchId, fileResult.FileName, fileResult.TransactionCount, updateResult.ModifiedCount);

        return fileResult;
    }

    // ── Method 4: ProcessAcknowledgment ──────────────────────────────

    public async Task ProcessAcknowledgmentAsync(string batchId, string acknowledgmentContent, string tenantId)
    {
        _logger.LogInformation("Processing 999 acknowledgment for batch {BatchId}", LogSanitizer.Sanitize(batchId));

        // Parse 999 acknowledgment code from content
        // A = Accepted, E = Accepted with errors (partial), R = Rejected
        var (ackCode, errors) = Parse999Acknowledgment(acknowledgmentContent);

        // Florida only: Texas and California responses are reconciled per record by
        // StateResponseReconciler, and must never be overwritten batch-wide here.
        var batchFilter = Builders<EncounterSubmission>.Filter.And(
            Builders<EncounterSubmission>.Filter.Eq(s => s.BatchId, batchId),
            Builders<EncounterSubmission>.Filter.Eq(s => s.TenantId, tenantId),
            Builders<EncounterSubmission>.Filter.Eq(s => s.StateCode, "FL")
        );
        var submissions = await _collection.Find(batchFilter).ToListAsync();

        if (submissions.Count == 0)
        {
            _logger.LogWarning("No submissions found for batch {BatchId}", LogSanitizer.Sanitize(batchId));
            return;
        }

        EncounterSubmissionStatus newStatus;
        switch (ackCode.ToUpperInvariant())
        {
            case "A":
                newStatus = EncounterSubmissionStatus.SyntaxAccepted;
                break;
            case "E":
                newStatus = EncounterSubmissionStatus.SyntaxAcceptedWithErrors;
                break;
            case "R":
            default:
                newStatus = EncounterSubmissionStatus.Rejected;
                break;
        }

        if (newStatus == EncounterSubmissionStatus.Rejected)
        {
            // For rejections: populate LastError, increment RetryCount
            var errorMessage = errors.Count > 0
                ? string.Join("; ", errors)
                : $"FMMIS 999 rejected batch {batchId}";

            var rejectUpdate = Builders<EncounterSubmission>.Update
                .Set(s => s.Status, EncounterSubmissionStatus.Rejected)
                .Set(s => s.AcknowledgmentCode, ackCode)
                .Set(s => s.AcknowledgedAt, DateTime.UtcNow)
                .Set(s => s.LastError, errorMessage)
                .Inc(s => s.RetryCount, 1)
                .Set(s => s.UpdatedAt, DateTime.UtcNow);

            await _collection.UpdateManyAsync(batchFilter, rejectUpdate);

            _logger.LogWarning(
                "Batch {BatchId} REJECTED: {ErrorCount} errors, {Count} submissions affected — {Errors}",
                LogSanitizer.Sanitize(batchId), errors.Count, submissions.Count, LogSanitizer.Sanitize(errorMessage));
        }
        else
        {
            var acceptUpdate = Builders<EncounterSubmission>.Update
                .Set(s => s.Status, newStatus)
                .Set(s => s.AcknowledgmentCode, ackCode)
                .Set(s => s.AcknowledgedAt, DateTime.UtcNow)
                .Set(s => s.UpdatedAt, DateTime.UtcNow);

            await _collection.UpdateManyAsync(batchFilter, acceptUpdate);

            _logger.LogInformation(
                "Batch {BatchId} acknowledged as {Status}: {Count} submissions updated",
                LogSanitizer.Sanitize(batchId), newStatus, submissions.Count);
        }
    }

    // ── Flag Deadline Warning ────────────────────────────────────────

    public async Task FlagDeadlineWarningAsync(EncounterSubmission submission)
    {
        var filter = Builders<EncounterSubmission>.Filter.And(
            Builders<EncounterSubmission>.Filter.Eq(s => s.Id, submission.Id),
            Builders<EncounterSubmission>.Filter.Eq(s => s.TenantId, submission.TenantId),
            Builders<EncounterSubmission>.Filter.Eq(s => s.Status, EncounterSubmissionStatus.Pending)
        );

        var update = Builders<EncounterSubmission>.Update
            .Set(s => s.Status, EncounterSubmissionStatus.DeadlineWarning)
            .Set(s => s.UpdatedAt, DateTime.UtcNow);

        await _collection.UpdateOneAsync(filter, update);

        _logger.LogWarning(
            "Flagged encounter submission {Id} (claim {ClaimId}) as DeadlineWarning — " +
            "deadline {Deadline:yyyy-MM-dd}, {DaysLeft:F1} days remaining",
            submission.Id, submission.ClaimId, submission.SubmissionDeadline,
            (submission.SubmissionDeadline - DateTime.UtcNow).TotalDays);
    }

    // ── GetDeadlineWarnings (tenant-scoped) ─────────────────────────

    public async Task<IEnumerable<EncounterSubmission>> GetDeadlineWarningsAsync(
        string tenantId, int warningDays = 7)
    {
        var warningCutoff = DateTime.UtcNow.AddDays(warningDays);

        var filter = Builders<EncounterSubmission>.Filter.And(
            Builders<EncounterSubmission>.Filter.Eq(s => s.TenantId, tenantId),
            Builders<EncounterSubmission>.Filter.In(s => s.Status, new[]
            {
                EncounterSubmissionStatus.Pending,
                EncounterSubmissionStatus.DeadlineWarning
            }),
            Builders<EncounterSubmission>.Filter.Lte(s => s.SubmissionDeadline, warningCutoff),
            Builders<EncounterSubmission>.Filter.Gt(s => s.SubmissionDeadline, DateTime.UtcNow)
        );

        var sort = Builders<EncounterSubmission>.Sort.Ascending(s => s.SubmissionDeadline);

        return await _collection.Find(filter).Sort(sort).ToListAsync();
    }

    // ── GetStatusSummary ─────────────────────────────────────────────

    public async Task<EncounterStatusSummary> GetStatusSummaryAsync(string tenantId)
    {
        var tenantFilter = Builders<EncounterSubmission>.Filter.Eq(s => s.TenantId, tenantId);
        var allForTenant = await _collection.Find(tenantFilter).ToListAsync();

        var summary = new EncounterStatusSummary
        {
            TenantId = tenantId,
            Pending = allForTenant.Count(s => s.Status == EncounterSubmissionStatus.Pending),
            Batched = allForTenant.Count(s => s.Status == EncounterSubmissionStatus.Batched),
            Submitted = allForTenant.Count(s => s.Status == EncounterSubmissionStatus.Submitted),
            Accepted = allForTenant.Count(s => s.Status == EncounterSubmissionStatus.Accepted),
            PartialAccept = allForTenant.Count(s => s.Status == EncounterSubmissionStatus.PartialAccept),
            SyntaxAccepted = allForTenant.Count(s => s.Status == EncounterSubmissionStatus.SyntaxAccepted),
            SyntaxAcceptedWithErrors = allForTenant.Count(s => s.Status == EncounterSubmissionStatus.SyntaxAcceptedWithErrors),
            Rejected = allForTenant.Count(s => s.Status == EncounterSubmissionStatus.Rejected),
            DeadlineWarning = allForTenant.Count(s => s.Status == EncounterSubmissionStatus.DeadlineWarning),
            Total = allForTenant.Count
        };

        _logger.LogInformation(
            "Status summary for tenant {TenantId}: {Pending} pending, {Batched} batched, " +
            "{Accepted} accepted, {Warning} warning, {Rejected} rejected",
            LogSanitizer.Sanitize(tenantId), summary.Pending, summary.Batched,
            summary.Accepted, summary.DeadlineWarning, summary.Rejected);

        return summary;
    }

    // ── RetrySubmission ──────────────────────────────────────────────

    public async Task<EncounterSubmission> RetrySubmissionAsync(string submissionId, string tenantId)
    {
        var filter = Builders<EncounterSubmission>.Filter.And(
            Builders<EncounterSubmission>.Filter.Eq(s => s.Id, submissionId),
            Builders<EncounterSubmission>.Filter.Eq(s => s.TenantId, tenantId)
        );

        var submission = await _collection.Find(filter).FirstOrDefaultAsync()
            ?? throw new KeyNotFoundException($"Submission '{submissionId}' not found for tenant '{tenantId}'");

        if (submission.Status != EncounterSubmissionStatus.Rejected)
        {
            throw new InvalidOperationException(
                $"Only rejected submissions can be retried; current status is '{submission.Status}'");
        }

        if (submission.RetryCount >= MaxRetryCount)
        {
            throw new InvalidOperationException(
                $"Submission '{submissionId}' has exhausted all {MaxRetryCount} retries");
        }

        var update = Builders<EncounterSubmission>.Update
            .Set(s => s.Status, EncounterSubmissionStatus.Pending)
            .Set(s => s.BatchId, null)
            .Set(s => s.LastError, null)
            .Set(s => s.UpdatedAt, DateTime.UtcNow);

        await _collection.UpdateOneAsync(filter, update);

        submission.Status = EncounterSubmissionStatus.Pending;
        submission.BatchId = null;
        submission.LastError = null;
        submission.UpdatedAt = DateTime.UtcNow;

        _logger.LogInformation(
            "Submission {SubmissionId} (claim {ClaimId}) reset to Pending for retry " +
            "(attempt {RetryCount}/{MaxRetries})",
            LogSanitizer.Sanitize(submissionId), submission.ClaimId, submission.RetryCount + 1, MaxRetryCount);

        return submission;
    }

    // ── Private Helpers ──────────────────────────────────────────────

    /// <summary>
    /// Fetch the encounter submission window (days) from the tenant's compliance config.
    /// Falls back to 60 days if the reference-data-service is unavailable.
    /// </summary>
    private async Task<int> GetEncounterSubmissionDaysAsync(string tenantId)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("ReferenceDataService");
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"/api/compliance-config/{tenantId}/state");
            request.Headers.Add("X-Tenant-ID", tenantId);

            var response = await client.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                var stateConfig = await response.Content
                    .ReadFromJsonAsync<StateComplianceConfigDto>(JsonOptions);

                if (stateConfig?.EncounterSubmissionDays > 0)
                {
                    return stateConfig.EncounterSubmissionDays;
                }
            }

            _logger.LogWarning(
                "Could not fetch compliance config for tenant {TenantId}, using default {Days} days",
                tenantId, DefaultSubmissionWindowDays);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Error fetching compliance config for tenant {TenantId}, using default {Days} days",
                tenantId, DefaultSubmissionWindowDays);
        }

        return DefaultSubmissionWindowDays;
    }

    /// <summary>
    /// Parse a 999 acknowledgment response to extract the status code and error list.
    /// Simplified parser — looks for AK9 segment status code.
    /// </summary>
    internal static (string AckCode, List<string> Errors) Parse999Acknowledgment(string content)
    {
        var errors = new List<string>();
        var ackCode = "R"; // Default to rejected if we can't parse

        if (string.IsNullOrWhiteSpace(content))
        {
            errors.Add("Empty 999 acknowledgment content");
            return (ackCode, errors);
        }

        var segments = content.Split('~', StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            var trimmed = segment.Trim();
            var elements = trimmed.Split('*');

            // AK9 — Functional Group Response Trailer
            // AK901: A = Accepted, E = Accepted with errors, R = Rejected
            if (elements.Length > 1 && elements[0] == "AK9")
            {
                ackCode = elements[1];
            }

            // IK3 — Implementation Segment Note (error detail)
            if (elements.Length > 3 && elements[0] == "IK3")
            {
                errors.Add($"Segment {elements[1]} position {elements[2]}: error code {elements[3]}");
            }

            // IK4 — Implementation Data Element Note (element-level error)
            if (elements.Length > 2 && elements[0] == "IK4")
            {
                errors.Add($"Element error at position {elements[1]}: code {elements[2]}");
            }
        }

        return (ackCode, errors);
    }
}

/// <summary>
/// DTO for the state compliance config returned by reference-data-service
/// <c>GET /api/compliance-config/{tenantId}/state</c>.
/// </summary>
internal class StateComplianceConfigDto
{
    public int EncounterSubmissionDays { get; set; }
    public int PromptPayElectronicDays { get; set; }
    public int PromptPayPaperDays { get; set; }
}
