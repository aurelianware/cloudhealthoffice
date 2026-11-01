using System.Net;
using System.Text.Json;
using CloudHealthOffice.Events;
using CloudHealthOffice.Infrastructure.Logging;
using EncounterSubmissionService.Models;
using MongoDB.Driver;

namespace EncounterSubmissionService.StateRoutes;

/// <summary>What a claims.finalized.v1 event did to the encounter records.</summary>
public enum EncounterLifecycleAction
{
    Ignored,
    CreatedOriginal,
    CreatedReplacement,
    CreatedVoid,
    ClosedBeforeSubmission
}

public sealed record EncounterLifecycleResult(EncounterLifecycleAction Action, string? SubmissionId, string? Reason);

public interface IEncounterLifecycleService
{
    /// <summary>
    /// Turns one finalized claim into encounter work on the tenant's state route:
    /// a new original, a replacement (CLM05-3 = 7) of an encounter the state already
    /// has, a void (8), or closing an encounter that was never sent.
    /// </summary>
    Task<EncounterLifecycleResult> HandleFinalizedAsync(ClaimFinalizedEvent evt, CancellationToken ct = default);
}

/// <summary>
/// The claims.finalized.v1 side of state encounters (Texas, California).
///
/// <para>Rules, from how state encounter systems treat corrections: an encounter
/// the state has never accepted is not replaced or voided at the state. If it was
/// never sent it is simply closed (and a replacement goes as a new original); if it
/// was sent and is still in flight, the replacement or void waits until the state's
/// claim reference arrives, because REF*F8 must carry it.</para>
/// </summary>
public sealed class EncounterLifecycleService : IEncounterLifecycleService
{
    private static readonly EncounterSubmissionStatus[] NotYetSent =
    {
        EncounterSubmissionStatus.Pending,
        EncounterSubmissionStatus.DeadlineWarning,
        EncounterSubmissionStatus.ValidationFailed
    };

    private readonly IMongoCollection<EncounterSubmission> _submissions;
    private readonly IStateEncounterRouteRegistry _registry;
    private readonly IStateEncounterBatchService _batches;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<EncounterLifecycleService> _logger;
    private readonly TimeProvider _clock;

    public EncounterLifecycleService(
        IMongoDatabase database,
        IStateEncounterRouteRegistry registry,
        IStateEncounterBatchService batches,
        IHttpClientFactory httpClientFactory,
        ILogger<EncounterLifecycleService> logger,
        TimeProvider? clock = null)
    {
        _submissions = database.GetCollection<EncounterSubmission>("encounter_submissions");
        _registry = registry;
        _batches = batches;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<EncounterLifecycleResult> HandleFinalizedAsync(ClaimFinalizedEvent evt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(evt.TenantId) || string.IsNullOrWhiteSpace(evt.ClaimId))
            return Ignore("event has no tenant or claim");

        var routes = _registry.EnrolledX12Routes(evt.TenantId);
        if (routes.Count == 0) return Ignore("tenant is not on a state X12 encounter route");
        if (routes.Count > 1)
        {
            // The event carries no state; a tenant on two state routes would need the
            // member's program state to choose. Refuse rather than guess.
            _logger.LogWarning("Tenant {TenantId} is configured for {Count} state encounter routes; " +
                               "claims.finalized events cannot be routed without a member state",
                LogSanitizer.Sanitize(evt.TenantId), routes.Count);
            return Ignore("tenant is configured for more than one state route");
        }
        var route = routes[0];

        var lob = await ReadLineOfBusinessAsync(evt.TenantId, evt.ClaimId, ct);
        if (!string.Equals(lob, route.LineOfBusiness, StringComparison.OrdinalIgnoreCase))
            return Ignore($"line of business '{lob}' is not {route.LineOfBusiness}");

        var adjudicatedAt = evt.AdjudicationTimestamp.UtcDateTime;
        var isVoid = string.Equals(evt.FinalStatus, "Reversed", StringComparison.OrdinalIgnoreCase)
                     || evt.ClaimFrequencyCode == "8";
        if (isVoid) return await VoidAsync(route, evt.TenantId, evt.ClaimId, adjudicatedAt, ct);

        var paid = string.Equals(evt.FinalStatus, "Paid", StringComparison.OrdinalIgnoreCase);
        var denied = string.Equals(evt.FinalStatus, "Denied", StringComparison.OrdinalIgnoreCase);
        if (!paid && !(denied && _registry.GetOptions(route.StateCode)?.ReportDeniedClaims == true))
            return Ignore($"final status '{evt.FinalStatus}' is not reported on {route.StateCode}");

        if (!string.IsNullOrWhiteSpace(evt.OriginalClaimId) && evt.OriginalClaimId != evt.ClaimId)
            return await ReplaceAsync(route, evt.TenantId, evt.ClaimId, evt.OriginalClaimId, adjudicatedAt, ct);

        var created = await _batches.CreateSubmissionRecordAsync(route, evt.ClaimId, evt.TenantId, adjudicatedAt);
        return new EncounterLifecycleResult(EncounterLifecycleAction.CreatedOriginal, created.Id, null);
    }

    private async Task<EncounterLifecycleResult> ReplaceAsync(StateEncounterRoute route, string tenantId,
        string claimId, string originalClaimId, DateTime adjudicatedAt, CancellationToken ct)
    {
        var original = await FindLatestForClaimAsync(route, tenantId, originalClaimId, ct);

        // Nothing for the original on this route (it predates the route, or was never
        // reportable): the adjusted claim is a new original.
        if (original is null || original.Status is EncounterSubmissionStatus.ClosedNotReportable
                or EncounterSubmissionStatus.Rejected)
        {
            if (original is { Status: EncounterSubmissionStatus.Rejected })
                await CloseAsync(original, tenantId, $"superseded by adjusted claim {claimId} before the state accepted it", ct);
            var fresh = await _batches.CreateSubmissionRecordAsync(route, claimId, tenantId, adjudicatedAt);
            return new EncounterLifecycleResult(EncounterLifecycleAction.CreatedOriginal, fresh.Id,
                original is null ? "no encounter for the original claim on this route" : "original was never accepted");
        }

        if (NotYetSent.Contains(original.Status))
        {
            await CloseAsync(original, tenantId, $"superseded by adjusted claim {claimId} before submission", ct);
            var fresh = await _batches.CreateSubmissionRecordAsync(route, claimId, tenantId, adjudicatedAt);
            return new EncounterLifecycleResult(EncounterLifecycleAction.CreatedOriginal, fresh.Id,
                "original was never sent; the adjusted claim goes as an original");
        }

        var replacement = await CreateLinkedAsync(route, tenantId, claimId, original, "7",
            $"{tenantId}:{route.StateCode}:{claimId}", adjudicatedAt, ct);
        return new EncounterLifecycleResult(EncounterLifecycleAction.CreatedReplacement, replacement.Id, null);
    }

    private async Task<EncounterLifecycleResult> VoidAsync(StateEncounterRoute route, string tenantId,
        string claimId, DateTime adjudicatedAt, CancellationToken ct)
    {
        var original = await FindLatestForClaimAsync(route, tenantId, claimId, ct);
        if (original is null) return Ignore("no encounter for the voided claim on this route");
        if (original.Status == EncounterSubmissionStatus.ClosedNotReportable)
            return Ignore("the encounter was already closed");

        if (NotYetSent.Contains(original.Status) || original.Status == EncounterSubmissionStatus.Rejected)
        {
            await CloseAsync(original, tenantId, original.Status == EncounterSubmissionStatus.Rejected
                ? "claim voided; the state never accepted the encounter"
                : "claim voided before the encounter was submitted", ct);
            return new EncounterLifecycleResult(EncounterLifecycleAction.ClosedBeforeSubmission, original.Id, null);
        }

        var voidRecord = await CreateLinkedAsync(route, tenantId, claimId, original, "8",
            $"{tenantId}:{route.StateCode}:{claimId}:void", adjudicatedAt, ct);
        return new EncounterLifecycleResult(EncounterLifecycleAction.CreatedVoid, voidRecord.Id, null);
    }

    private async Task<EncounterSubmission> CreateLinkedAsync(StateEncounterRoute route, string tenantId,
        string claimId, EncounterSubmission original, string frequency, string id, DateTime adjudicatedAt,
        CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var record = new EncounterSubmission
        {
            Id = id,
            TenantId = tenantId,
            ClaimId = claimId,
            ClaimAdjudicatedAt = adjudicatedAt,
            StateCode = route.StateCode,
            Receiver = route.Receiver,
            FrequencyCode = frequency,
            OriginalSubmissionId = original.Id,
            SubmissionDeadline = route.ComputeDeadline(adjudicatedAt, _registry.GetOptions(route.StateCode)),
            Status = EncounterSubmissionStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now
        };

        try
        {
            await _submissions.InsertOneAsync(record, cancellationToken: ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // Redelivered event: the linked record already exists.
            record = await _submissions.Find(Builders<EncounterSubmission>.Filter.And(
                         Builders<EncounterSubmission>.Filter.Eq(s => s.Id, id),
                         Builders<EncounterSubmission>.Filter.Eq(s => s.TenantId, tenantId))).FirstOrDefaultAsync(ct)
                     ?? throw new InvalidOperationException("Encounter submission id collides with a record in another tenant.");
            return record;
        }

        await _submissions.UpdateOneAsync(
            Builders<EncounterSubmission>.Filter.And(
                Builders<EncounterSubmission>.Filter.Eq(s => s.Id, original.Id),
                Builders<EncounterSubmission>.Filter.Eq(s => s.TenantId, tenantId)),
            Builders<EncounterSubmission>.Update
                .Set(s => s.SupersededBySubmissionId, record.Id)
                .Set(s => s.UpdatedAt, now),
            cancellationToken: ct);

        _logger.LogInformation("{Kind} encounter {SubmissionId} created for {State}, replacing {OriginalId}",
            frequency == "8" ? "Void" : "Replacement", LogSanitizer.Sanitize(record.Id), route.StateCode,
            LogSanitizer.Sanitize(original.Id));
        return record;
    }

    private async Task CloseAsync(EncounterSubmission original, string tenantId, string reason, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await _submissions.UpdateOneAsync(
            Builders<EncounterSubmission>.Filter.And(
                Builders<EncounterSubmission>.Filter.Eq(s => s.Id, original.Id),
                Builders<EncounterSubmission>.Filter.Eq(s => s.TenantId, tenantId),
                Builders<EncounterSubmission>.Filter.Eq(s => s.Status, original.Status)),
            Builders<EncounterSubmission>.Update
                .Set(s => s.Status, EncounterSubmissionStatus.ClosedNotReportable)
                .Set(s => s.LastError, $"Closed without reporting: {reason}")
                .Set(s => s.UpdatedAt, now)
                .Push(s => s.ReworkHistory, new EncounterReworkEvent
                {
                    Action = "Closed",
                    ReasonCode = "CLAIM_ADJUSTED",
                    ActorId = "claims.finalized.v1",
                    Note = reason,
                    At = now
                }),
            cancellationToken: ct);
    }

    /// <summary>The newest non-void record for a claim on the route (the one a replacement or void acts on).</summary>
    private Task<EncounterSubmission?> FindLatestForClaimAsync(StateEncounterRoute route, string tenantId,
        string claimId, CancellationToken ct) =>
        _submissions.Find(Builders<EncounterSubmission>.Filter.And(
                Builders<EncounterSubmission>.Filter.Eq(s => s.TenantId, tenantId),
                Builders<EncounterSubmission>.Filter.Eq(s => s.StateCode, route.StateCode),
                Builders<EncounterSubmission>.Filter.Eq(s => s.ClaimId, claimId),
                Builders<EncounterSubmission>.Filter.Ne(s => s.FrequencyCode, "8")))
            .SortByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct)!;

    private async Task<string?> ReadLineOfBusinessAsync(string tenantId, string claimId, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("ClaimsService");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/claims/{Uri.EscapeDataString(claimId)}");
        request.Headers.Add("X-Tenant-ID", tenantId);
        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        // Anything else unreadable throws, so the event is redelivered rather than dropped.
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("lineOfBusiness", out var lob)) return null;
        return lob.ValueKind switch
        {
            JsonValueKind.String => lob.GetString(),
            JsonValueKind.Number => lob.GetInt32() switch
            {
                1 => "Commercial", 2 => "Medicare", 3 => "Medicaid", 4 => "Exchange", 5 => "TRICARE", 6 => "VA",
                _ => null
            },
            _ => null
        };
    }

    private static EncounterLifecycleResult Ignore(string reason) =>
        new(EncounterLifecycleAction.Ignored, null, reason);
}
