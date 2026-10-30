using System.Globalization;
using System.Text.Json.Nodes;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.Extensions.Options;
using SharedTenantMiddleware = CloudHealthOffice.Infrastructure.Middleware.TenantMiddleware;

namespace FhirService.Services.PublicDirectory;

/// <summary>Builds the snapshot of one published directory from the services that own its data.</summary>
public interface IPlanNetDirectorySource
{
    Task<PlanNetDirectorySnapshot> BuildAsync(string directoryId, string tenantId, CancellationToken ct);
}

/// <summary>The directory could not be built: an upstream refused or was unreachable.</summary>
public sealed class PlanNetDirectoryUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Reads a tenant's whole directory, in Plan-Net shape, into a
/// <see cref="PlanNetDirectorySnapshot"/>:
/// <list type="bullet">
///   <item>provider-service's export feed: networks, then providers a page at
///   a time (Practitioner, PractitionerRole, Organization, Location,
///   HealthcareService, OrganizationAffiliation);</item>
///   <item>benefit-plan-service's InsurancePlan and Endpoint searches;</item>
///   <item>the plan's own Organization, from configuration;</item>
///   <item>then the links only the whole directory can make
///   (<see cref="PlanNetLinking"/>).</item>
/// </list>
/// Every upstream body passes through <see cref="PublicDirectoryResponseFilter"/>
/// first, exactly as the proxied route did, so CHO-internal extensions, tax
/// ids and inactive resources never reach a snapshot. Calls are made as
/// fhir-service for the directory's tenant only.
/// </summary>
public sealed class PlanNetDirectorySource : IPlanNetDirectorySource
{
    private const int ProviderPageSize = 500;
    private const int PlanPageSize = 200;
    private const int MaxPages = 5_000;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PublicProviderDirectoryOptions _options;
    private readonly ILogger<PlanNetDirectorySource> _logger;

    public PlanNetDirectorySource(
        IHttpClientFactory httpClientFactory,
        IOptions<PublicProviderDirectoryOptions> options,
        ILogger<PlanNetDirectorySource> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<PlanNetDirectorySnapshot> BuildAsync(string directoryId, string tenantId, CancellationToken ct)
    {
        var providerService = _httpClientFactory.CreateClient("ProviderService");
        var benefitPlanService = _httpClientFactory.CreateClient(UpstreamClientNames.BenefitPlanService);
        var payer = _options.PayerFor(directoryId);
        var payerId = PayerOrganization.ResourceId;
        var resources = new List<JsonObject> { PlanNetShaping.PayerOrganization(payer, directoryId) };

        void AddAll(IEnumerable<JsonObject> batch)
        {
            resources.AddRange(batch);
            if (resources.Count > _options.MaxResources)
                throw new PlanNetDirectoryUnavailableException(
                    $"Directory '{directoryId}' exceeds PublicProviderDirectory:MaxResources ({_options.MaxResources}).");
        }

        var escapedPayer = Uri.EscapeDataString(payerId);
        var (networks, _) = await GetBundleAsync(
            providerService, $"fhir/plannet-export/networks?payer={escapedPayer}", tenantId, ct);
        AddAll(networks);

        string? next = $"providers?payer={escapedPayer}&skip=0&count={ProviderPageSize.ToString(CultureInfo.InvariantCulture)}";
        for (var page = 0; next is not null; page++)
        {
            if (page >= MaxPages) throw new PlanNetDirectoryUnavailableException("provider-service export did not end.");
            var (batch, bundle) = await GetBundleAsync(providerService, $"fhir/plannet-export/{next}", tenantId, ct);
            AddAll(batch);
            next = NextLink(bundle);
            if (next is not null && !next.StartsWith("providers?", StringComparison.Ordinal))
                throw new PlanNetDirectoryUnavailableException("provider-service export named an unexpected next page.");
        }

        AddAll((await GetAllPagesAsync(benefitPlanService, "fhir/InsurancePlan", tenantId, ct))
            .Select(plan => PlanNetShaping.InsurancePlan(plan, payer)));
        AddAll((await GetAllPagesAsync(benefitPlanService, "fhir/Endpoint", tenantId, ct))
            .Select(PlanNetShaping.Endpoint)
            .OfType<JsonObject>());

        PlanNetLinking.Link(resources, payerId);

        var snapshot = PlanNetDirectorySnapshot.Create(directoryId, resources, DateTimeOffset.UtcNow);
        _logger.LogInformation(
            "Public directory {Directory} built: {Count} Plan-Net resources", directoryId, snapshot.Count);
        return snapshot;
    }

    /// <summary>Pages a benefit-plan-service search (<c>_count</c>/<c>_page</c>) to its end.</summary>
    private async Task<List<JsonObject>> GetAllPagesAsync(
        HttpClient client, string path, string tenantId, CancellationToken ct)
    {
        var all = new List<JsonObject>();
        for (var page = 1; page <= MaxPages; page++)
        {
            var (batch, bundle) = await GetBundleAsync(
                client,
                $"{path}?_count={PlanPageSize.ToString(CultureInfo.InvariantCulture)}&_page={page.ToString(CultureInfo.InvariantCulture)}",
                tenantId, ct, countRawEntries: true);
            all.AddRange(batch);
            // Inactive entries were filtered out of batch, so the page size is
            // judged on what the upstream returned.
            if (bundle["rawEntryCount"]!.GetValue<int>() < PlanPageSize) return all;
        }
        throw new PlanNetDirectoryUnavailableException($"{path} paging did not end.");
    }

    private async Task<(List<JsonObject> Resources, JsonObject Bundle)> GetBundleAsync(
        HttpClient client, string path, string tenantId, CancellationToken ct, bool countRawEntries = false)
    {
        string body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path).UseChoServiceIdentity();
            request.Headers.Add(SharedTenantMiddleware.TenantHeaderName, tenantId);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                // The body is not for the public and not for the log either.
                throw new PlanNetDirectoryUnavailableException(
                    $"{client.BaseAddress?.Host} answered {(int)response.StatusCode} for {StripQuery(path)}.");
            }
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            throw new PlanNetDirectoryUnavailableException($"{client.BaseAddress?.Host} is unreachable.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new PlanNetDirectoryUnavailableException($"{client.BaseAddress?.Host} timed out.", ex);
        }

        var rawEntries = 0;
        if (countRawEntries)
        {
            try { rawEntries = (JsonNode.Parse(body)?["entry"] as JsonArray)?.Count ?? 0; }
            catch (System.Text.Json.JsonException) { rawEntries = 0; }
        }

        var filtered = PublicDirectoryResponseFilter.Filter(body);
        if (filtered is null || JsonNode.Parse(filtered) is not JsonObject bundle
            || bundle["resourceType"]?.GetValue<string>() != "Bundle")
            throw new PlanNetDirectoryUnavailableException($"{StripQuery(path)} did not answer a Bundle.");

        var resources = new List<JsonObject>();
        if (bundle["entry"] is JsonArray entries)
        {
            foreach (var entry in entries)
            {
                if (entry?["resource"] is JsonObject resource)
                {
                    entry.AsObject().Remove("resource");
                    resources.Add(resource);
                }
            }
        }
        bundle["rawEntryCount"] = rawEntries;
        return (resources, bundle);
    }

    private static string? NextLink(JsonObject bundle)
    {
        if (bundle["link"] is not JsonArray links) return null;
        foreach (var link in links)
        {
            if (link?["relation"]?.GetValue<string>() == "next")
                return link["url"]?.GetValue<string>();
        }
        return null;
    }

    private static string StripQuery(string path)
    {
        var q = path.IndexOf('?');
        return q < 0 ? path : path[..q];
    }
}
