using System.Net;
using System.Text.Json.Nodes;
using CloudHealthOffice.Events;
using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.ManagedFhirStore;

namespace FhirService.Services.ManagedFhirStore;

/// <summary>What happened to one finalized claim's projection.</summary>
public enum ManagedFhirProjectionOutcome
{
    /// <summary>The ExplanationOfBenefit was written to the managed store.</summary>
    Published,

    /// <summary>The event belongs to a tenant this deployment does not publish.</summary>
    OtherTenant,

    /// <summary>claims-service has no ExplanationOfBenefit for the claim.</summary>
    NotFound,

    /// <summary>The managed store refused the resource (profile validation, a reference). Not retried.</summary>
    Rejected
}

/// <summary>
/// Publishes a finalized claim's ExplanationOfBenefit into the plan's managed
/// FHIR store (ADR 023). claims-service owns the projection (ADR 008); this
/// fetches it through the same endpoint the FHIR façade proxies, so the store
/// holds exactly what CHO serves, and writes it at its own id. Replaying an
/// event re-reads the chain head and overwrites, so redelivery converges.
///
/// A managed store belongs to one plan: only events for
/// <c>ManagedFhirStore:TenantId</c> are published.
/// </summary>
public sealed class ManagedFhirStoreProjector
{
    public const string TenantIdKey = "ManagedFhirStore:TenantId";
    private const string ResourceType = "ExplanationOfBenefit";

    private readonly IHttpClientFactory _clients;
    private readonly IManagedFhirStore _store;
    private readonly string _tenantId;
    private readonly ILogger<ManagedFhirStoreProjector> _logger;

    public ManagedFhirStoreProjector(
        IHttpClientFactory clients,
        IManagedFhirStore store,
        IConfiguration configuration,
        ILogger<ManagedFhirStoreProjector> logger)
    {
        _clients = clients;
        _store = store;
        _logger = logger;
        _tenantId = configuration[TenantIdKey] is { Length: > 0 } tenantId
            ? tenantId.Trim()
            : throw new InvalidOperationException(
                $"{TenantIdKey} is required when ManagedFhirStore:Provider is set: a managed FHIR store holds one plan's data.");
    }

    public async Task<ManagedFhirProjectionOutcome> ProjectClaimAsync(ClaimFinalizedEvent evt, CancellationToken ct)
    {
        if (!string.Equals(evt.TenantId, _tenantId, StringComparison.Ordinal))
        {
            return ManagedFhirProjectionOutcome.OtherTenant;
        }

        var eobId = string.IsNullOrWhiteSpace(evt.ClaimVersionId) ? evt.ClaimId : evt.ClaimVersionId;

        var client = _clients.CreateClient(UpstreamClientNames.ClaimsService);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"fhir/{ResourceType}/{Uri.EscapeDataString(eobId)}");
        // No inbound caller here: the shared outbound handler mints this
        // service's token for the tenant the request names.
        request.Headers.Add(TenantHeaderPropagationHandler.HeaderName, evt.TenantId);

        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning("No ExplanationOfBenefit for finalized claim {ClaimId}; nothing published",
                LogSanitizer.Sanitize(eobId));
            return ManagedFhirProjectionOutcome.NotFound;
        }
        if (!response.IsSuccessStatusCode)
        {
            // Retried by the consumer: claims-service may be restarting.
            throw new HttpRequestException(
                $"claims-service returned {(int)response.StatusCode} for {ResourceType}/{eobId}.", null, response.StatusCode);
        }

        var eobJson = await response.Content.ReadAsStringAsync(ct);
        var id = JsonNode.Parse(eobJson)?["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"claims-service returned an {ResourceType} without an id.");

        var result = await _store.UpsertAsync(ResourceType, id, eobJson, ct);
        if (!result.Succeeded)
        {
            _logger.LogWarning(
                "Managed FHIR store {Provider} rejected {ResourceType}/{Id} with {StatusCode}: {Outcome}",
                _store.ProviderName, ResourceType, LogSanitizer.Sanitize(id), result.StatusCode,
                LogSanitizer.Sanitize(SummarizeIssues(result.OperationOutcomeJson)));
            return ManagedFhirProjectionOutcome.Rejected;
        }

        _logger.LogInformation("Published {ResourceType}/{Id} to {Provider} (version {VersionId})",
            ResourceType, LogSanitizer.Sanitize(id), _store.ProviderName, result.VersionId);
        return ManagedFhirProjectionOutcome.Published;
    }

    /// <summary>Issue codes and diagnostics only; the resource itself (PHI) is never logged.</summary>
    internal static string SummarizeIssues(string? operationOutcomeJson)
    {
        if (string.IsNullOrWhiteSpace(operationOutcomeJson)) return "no OperationOutcome";
        try
        {
            var issues = JsonNode.Parse(operationOutcomeJson)?["issue"]?.AsArray();
            if (issues is null || issues.Count == 0) return "no issues";
            return string.Join("; ", issues.Take(5).Select(i =>
                $"{i?["code"]?.GetValue<string>() ?? "?"}: {i?["diagnostics"]?.GetValue<string>() ?? ""}".TrimEnd(' ', ':')));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            return "unreadable OperationOutcome";
        }
    }
}
