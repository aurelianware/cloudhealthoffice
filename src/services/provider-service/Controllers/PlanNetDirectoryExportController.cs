using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using ProviderService.Models;
using ProviderService.Repositories;
using ProviderService.Services;

namespace ProviderService.Controllers;

/// <summary>
/// The feed fhir-service reads to build a published public Provider Directory
/// (G-36, Da Vinci PDex Plan-Net 1.2.0). fhir-service holds each directory as
/// an in-memory snapshot and answers every public read and search from it, so
/// this controller only has to list the tenant's directory, in Plan-Net shape,
/// a page at a time. It serves no search of its own.
///
/// <code>
///   GET /fhir/plannet-export/networks?payer={id}
///   GET /fhir/plannet-export/providers?payer={id}&amp;skip={n}&amp;count={n}
/// </code>
///
/// Both answer a FHIR Bundle of type <c>collection</c>. The providers feed
/// pages over head-Active providers in a stable order and carries a
/// <c>next</c> link while a page came back full. <c>payer</c> is the id the
/// directory gives the plan's own Organization, which every network names as
/// its manager (Plan-Net Network.partOf is required).
///
/// Tenant scoping is the usual one: the tenant comes from the caller's token
/// (fhir-service calls with its service identity for the directory's tenant).
/// </summary>
[ApiController]
[Route("fhir/plannet-export")]
public class PlanNetDirectoryExportController : ControllerBase
{
    private const string FhirContentType = "application/fhir+json";
    private const int DefaultPageSize = 200;
    private const int MaxPageSize = 1000;
    private const int NetworkPageSize = 200;

    private readonly IProviderRepository _providers;
    private readonly IOrganizationRepository _organizations;
    private readonly IPlanNetDirectoryProjector _projector;

    public PlanNetDirectoryExportController(
        IProviderRepository providers,
        IOrganizationRepository organizations,
        IPlanNetDirectoryProjector projector)
    {
        _providers = providers;
        _organizations = organizations;
        _projector = projector;
    }

    /// <summary>Every directory-eligible network of the tenant, as plannet-Network.</summary>
    [HttpGet("networks")]
    [Produces(FhirContentType)]
    public async Task<IActionResult> Networks([FromQuery] string? payer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(payer))
            return BadRequestOutcome("payer is required: every Plan-Net network names the organization that manages it.");

        var entries = new JsonArray();
        foreach (var network in (await ActiveNetworksAsync(ct)).Values.OrderBy(n => n.OrganizationId, StringComparer.Ordinal))
        {
            var resource = _projector.ProjectNetwork(network, payer);
            if (resource is not null) entries.Add(new JsonObject { ["resource"] = resource });
        }
        return Collection(entries, next: null);
    }

    /// <summary>
    /// A page of head-Active providers, each expanded to every Plan-Net
    /// resource it contributes (see <see cref="IPlanNetDirectoryProjector.ProjectProvider"/>).
    /// </summary>
    [HttpGet("providers")]
    [Produces(FhirContentType)]
    public async Task<IActionResult> Providers(
        [FromQuery] string? payer,
        [FromQuery] int skip = 0,
        [FromQuery] int count = DefaultPageSize,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(payer))
            return BadRequestOutcome("payer is required: every Plan-Net network names the organization that manages it.");

        var tenantId = HttpContext.Items["TenantId"] as string;
        if (string.IsNullOrEmpty(tenantId))
            return Collection(new JsonArray(), next: null);

        skip = Math.Max(0, skip);
        count = Math.Clamp(count, 1, MaxPageSize);

        var networks = await ActiveNetworksAsync(ct);

        // Head-Active providers in ProviderId order. The integrity worker's
        // listing is exactly that when every due date qualifies; reusing it
        // keeps the repository's legacy-hydration rules in one place.
        var page = await _providers.ListProvidersForIntegrityRefreshAsync(
            tenantId, DateTimeOffset.MaxValue, includeNeverVerified: true, skip, count, ct);

        var asOf = DateTime.UtcNow;
        var entries = new JsonArray();
        foreach (var provider in page)
        {
            foreach (var resource in _projector.ProjectProvider(provider, networks, asOf))
                entries.Add(new JsonObject { ["resource"] = resource });
        }

        var next = page.Count == count
            ? $"providers?payer={Uri.EscapeDataString(payer)}&skip={(skip + count).ToString(CultureInfo.InvariantCulture)}&count={count.ToString(CultureInfo.InvariantCulture)}"
            : null;
        return Collection(entries, next);
    }

    private async Task<IReadOnlyDictionary<string, Organization>> ActiveNetworksAsync(CancellationToken ct)
    {
        var networks = new Dictionary<string, Organization>(StringComparer.Ordinal);
        for (var page = 1; ; page++)
        {
            ct.ThrowIfCancellationRequested();
            var (items, _) = await _organizations.ListAsync(
                networkType: null, lineOfBusiness: null, parentOrganizationId: null, page, NetworkPageSize);
            foreach (var network in items)
            {
                if (network.VersionState == OrganizationVersionState.Active && network.Status == OrganizationStatus.Active)
                    networks[network.OrganizationId] = network;
            }
            if (items.Count < NetworkPageSize) break;
        }
        return networks;
    }

    private static ContentResult Collection(JsonArray entries, string? next)
    {
        var bundle = new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "collection",
        };
        if (next is not null)
            bundle["link"] = new JsonArray { new JsonObject { ["relation"] = "next", ["url"] = next } };
        bundle["entry"] = entries;
        return new ContentResult { ContentType = FhirContentType, Content = bundle.ToJsonString(), StatusCode = 200 };
    }

    private static ContentResult BadRequestOutcome(string diagnostics)
    {
        var outcome = new JsonObject
        {
            ["resourceType"] = "OperationOutcome",
            ["issue"] = new JsonArray
            {
                new JsonObject { ["severity"] = "error", ["code"] = "required", ["diagnostics"] = diagnostics },
            },
        };
        return new ContentResult { ContentType = FhirContentType, Content = outcome.ToJsonString(), StatusCode = 400 };
    }
}
