using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloudHealthOffice.Infrastructure.Security;
using FhirService.Models;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using SharedTenantMiddleware = CloudHealthOffice.Infrastructure.Middleware.TenantMiddleware;

namespace FhirService.Services.BulkExport;

/// <summary>
/// The resources of one type that a bulk export writes for one member, as
/// compact FHIR JSON (one NDJSON line each).
/// </summary>
public interface IBulkExportResourceSource
{
    IAsyncEnumerable<string> ReadAsync(
        string tenantId, string memberId, string resourceType, DateTimeOffset? since, CancellationToken ct = default);
}

/// <summary>
/// Reads a member's resources from the same sources the per-member Provider
/// Access reads use: Patient, Coverage, Encounter and Claim through the
/// tenant's <see cref="IFhirDataAdapter"/>, and ExplanationOfBenefit from
/// claims-service's Provider Access projection (no paid amounts) and
/// authorization-service's prior authorizations. Upstream calls go as
/// fhir-service for the export's tenant; there is no caller to act for, since
/// exports run in the background.
///
/// Every resource is checked to belong to the member before it is written, so
/// an upstream that ignored its patient filter cannot put another member's
/// data in the file.
/// </summary>
public sealed class BulkExportResourceSource : IBulkExportResourceSource
{
    private const int PageSize = 100;
    private const int MaxPages = 1000;

    private static readonly JsonSerializerOptions FhirJson =
        new JsonSerializerOptions().ForFhir(typeof(Patient).Assembly);

    private readonly IFhirDataAdapter _adapter;
    private readonly IHttpClientFactory _clients;

    public BulkExportResourceSource(IFhirDataAdapter adapter, IHttpClientFactory clients)
    {
        _adapter = adapter;
        _clients = clients;
    }

    public IAsyncEnumerable<string> ReadAsync(
        string tenantId, string memberId, string resourceType, DateTimeOffset? since, CancellationToken ct = default)
        => resourceType switch
        {
            "Patient" => PatientAsync(tenantId, memberId, since, ct),
            "Coverage" => PagedAsync<Coverage>(
                page => _adapter.SearchCoverageAsync(
                    new CoverageSearchParams { Beneficiary = memberId, Count = PageSize, Page = page }, tenantId, ct),
                c => References(c.Beneficiary, memberId), since, ct),
            "Encounter" => PagedAsync<Encounter>(
                page => _adapter.SearchEncountersAsync(
                    new EncounterSearchParams { Patient = memberId, Count = PageSize, Page = page }, tenantId, ct),
                e => References(e.Subject, memberId), since, ct),
            "Claim" => PagedAsync<Claim>(
                page => _adapter.SearchClaimsAsync(
                    new ClaimSearchParams { Patient = memberId, Count = PageSize, Page = page }, tenantId, ct),
                c => References(c.Patient, memberId), since, ct),
            "ExplanationOfBenefit" => ExplanationsOfBenefitAsync(tenantId, memberId, since, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(resourceType), resourceType, "Not a bulk export type."),
        };

    private async IAsyncEnumerable<string> PatientAsync(
        string tenantId, string memberId, DateTimeOffset? since, [EnumeratorCancellation] CancellationToken ct)
    {
        var patient = await _adapter.GetPatientAsync(memberId, tenantId, ct);
        if (patient is not null && patient.Id == memberId && UpdatedSince(patient.Meta?.LastUpdated, since))
            yield return Serialize(patient);
    }

    private static async IAsyncEnumerable<string> PagedAsync<T>(
        Func<int, Task<(IReadOnlyList<T> Items, int Total)>> page,
        Func<T, bool> belongsToMember,
        DateTimeOffset? since,
        [EnumeratorCancellation] CancellationToken ct) where T : Resource
    {
        for (var number = 1; number <= MaxPages; number++)
        {
            ct.ThrowIfCancellationRequested();
            var (items, _) = await page(number);
            foreach (var item in items)
            {
                if (belongsToMember(item) && UpdatedSince(item.Meta?.LastUpdated, since))
                    yield return Serialize(item);
            }
            if (items.Count < PageSize)
                yield break;
        }
        throw new InvalidOperationException($"More than {MaxPages} pages of {typeof(T).Name} for one member.");
    }

    private async IAsyncEnumerable<string> ExplanationsOfBenefitAsync(
        string tenantId, string memberId, DateTimeOffset? since, [EnumeratorCancellation] CancellationToken ct)
    {
        var sources = new[]
        {
            (Client: UpstreamClientNames.ClaimsService, Path: "fhir/ProviderAccess/ExplanationOfBenefit"),
            (Client: UpstreamClientNames.AuthorizationService, Path: "fhir/ExplanationOfBenefit/prior-authorization"),
        };
        foreach (var (clientName, path) in sources)
        {
            var client = _clients.CreateClient(clientName);
            for (var number = 1; number <= MaxPages; number++)
            {
                var url = $"{path}?patient={Uri.EscapeDataString(memberId)}&_count={PageSize}&_page={number}";
                using var request = new HttpRequestMessage(HttpMethod.Get, url).UseChoServiceIdentity();
                request.Headers.Add(SharedTenantMiddleware.TenantHeaderName, tenantId);
                using var response = await client.SendAsync(request, ct);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    break;
                response.EnsureSuccessStatusCode();

                var bundle = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject;
                var entries = bundle?["entry"] as JsonArray ?? [];
                foreach (var resource in entries.Select(e => e?["resource"] as JsonObject).OfType<JsonObject>())
                {
                    if (resource["resourceType"]?.GetValue<string>() != "ExplanationOfBenefit"
                        || resource["patient"]?["reference"]?.GetValue<string>() is not { } reference
                        || !IsMember(reference, memberId))
                        continue;
                    if (!UpdatedSince(LastUpdated(resource), since))
                        continue;
                    yield return resource.ToJsonString();
                }
                if (entries.Count < PageSize)
                    break;
            }
        }
    }

    private static DateTimeOffset? LastUpdated(JsonObject resource)
        => resource["meta"]?["lastUpdated"]?.GetValue<string>() is { } value && DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// <c>_since</c> keeps resources changed at or after it. A resource that
    /// does not say when it last changed is kept: leaving it out could drop a
    /// change the client asked for.
    /// </summary>
    internal static bool UpdatedSince(DateTimeOffset? lastUpdated, DateTimeOffset? since)
        => since is null || lastUpdated is null || lastUpdated >= since;

    private static bool References(ResourceReference? reference, string memberId)
        => reference?.Reference is { } value && IsMember(value, memberId);

    internal static bool IsMember(string reference, string memberId)
        => string.Equals(reference, memberId, StringComparison.Ordinal)
           || string.Equals(reference, "Patient/" + memberId, StringComparison.Ordinal);

    private static string Serialize(Resource resource) => JsonSerializer.Serialize(resource, resource.GetType(), FhirJson);
}
