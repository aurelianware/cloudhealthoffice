using System.Buffers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloudHealthOffice.Infrastructure.Logging;
using FhirService.Services.PublicDirectory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace FhirService.Controllers;

/// <summary>
/// The public Provider Directory API (CMS-9115-F, D-29), a Da Vinci PDex
/// Plan-Net 1.2.0 server (G-36): read, vread and search of a plan's
/// Practitioner, PractitionerRole, Organization (plan, networks and provider
/// organizations), Location, HealthcareService, OrganizationAffiliation,
/// InsurancePlan and Endpoint resources, with no user authentication, as CMS
/// requires and Plan-Net expects.
///
/// The plan is named by the public directory id in the URL
/// (<c>/fhir/r4/directory/{directory}/...</c>), which maps to a tenant only
/// through <see cref="PublicProviderDirectoryOptions"/>: a plan that has not
/// published its directory has none here. That is the one place in fhir-service
/// where a tenant is chosen by something other than a validated token, so the
/// route serves directory data and nothing else:
///
///   * Any credential the request carries is ignored. The caller is treated as
///     anonymous for the whole request, so no token's tenant or scopes shape
///     the answer and no caller's credential travels upstream.
///   * Directory data is read by <see cref="PlanNetDirectorySource"/> as
///     fhir-service, for the directory's tenant only, outside any request.
///   * Everything read passes through <see cref="PublicDirectoryResponseFilter"/>,
///     which removes CHO-internal extensions (such as the integrity score), tax
///     ids and inactive providers, before it is kept.
///   * Requests are rate limited per client address.
///
/// Answers come from the directory's snapshot (<see cref="PlanNetDirectoryStore"/>),
/// searched in memory by <see cref="PlanNetSearchEngine"/>. Plan-Net's
/// searches cross resource types (chains, reverse chains, includes) in ways
/// the owning services' own search APIs were never built for, and a directory
/// is public, low-churn data, so one consistent snapshot per directory,
/// refreshed on a timer, is both the simpler and the more faithful design.
///
/// The authenticated directory on <c>/fhir/r4/Practitioner</c> and friends is
/// unchanged.
/// </summary>
[Route("fhir/r4/directory/{directory}")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicy)]
[TypeFilter(typeof(PublicDirectoryAccessFilter))]
public sealed class PublicProviderDirectoryController : FhirControllerBase
{
    public const string RateLimitPolicy = "public-provider-directory";
    private const string FhirJson = "application/fhir+json";

    private readonly PlanNetDirectoryStore _store;
    private readonly ILogger<PublicProviderDirectoryController> _logger;

    public PublicProviderDirectoryController(
        PlanNetDirectoryStore store,
        ILogger<PublicProviderDirectoryController> logger)
    {
        _store = store;
        _logger = logger;
    }

    private string DirectoryBaseUrl
    {
        get
        {
            var req = HttpContext.Request;
            return $"{req.Scheme}://{req.Host}{req.PathBase}/fhir/r4/directory/{PublicDirectoryAccessFilter.Directory(HttpContext)}";
        }
    }

    [HttpGet("metadata")]
    [Produces(FhirJson)]
    public IActionResult Metadata()
        => Json(PlanNetCapabilityStatement.Build(
            DirectoryBaseUrl, PublicDirectoryAccessFilter.Directory(HttpContext), DateTimeOffset.UtcNow));

    [HttpGet("{type}")]
    [Produces(FhirJson)]
    public async Task<IActionResult> Search(string type, CancellationToken ct)
    {
        if (!PlanNetSearchParameters.IsDirectoryType(type)) return NotADirectoryType(type);

        var snapshot = await SnapshotAsync(ct);
        if (snapshot.Result is not null) return snapshot.Result;

        var query = HttpContext.Request.Query
            .SelectMany(q => q.Value.Select(v => new KeyValuePair<string, string>(q.Key, v ?? string.Empty)));

        PlanNetSearchResult result;
        try
        {
            result = PlanNetSearchEngine.Search(snapshot.Value!, type, query);
        }
        catch (PlanNetSearchException ex)
        {
            return FhirBadRequest(LogSanitizer.Sanitize(ex.Message));
        }

        return Content(SearchBundle(type, result, snapshot.Value!), FhirJson, Encoding.UTF8);
    }

    [HttpGet("{type}/{id}")]
    [Produces(FhirJson)]
    public async Task<IActionResult> Read(string type, string id, CancellationToken ct)
    {
        if (!PlanNetSearchParameters.IsDirectoryType(type)) return NotADirectoryType(type);

        var snapshot = await SnapshotAsync(ct);
        if (snapshot.Result is not null) return snapshot.Result;

        var resource = snapshot.Value!.Get(type, id);
        return resource is null ? FhirNotFound(type, id) : Resource(resource);
    }

    [HttpGet("{type}/{id}/_history/{versionId}")]
    [Produces(FhirJson)]
    public async Task<IActionResult> VRead(string type, string id, string versionId, CancellationToken ct)
    {
        if (!PlanNetSearchParameters.IsDirectoryType(type)) return NotADirectoryType(type);

        var snapshot = await SnapshotAsync(ct);
        if (snapshot.Result is not null) return snapshot.Result;

        // Only the current version is kept; an earlier one is gone, not hidden.
        var resource = snapshot.Value!.Get(type, id);
        return resource is not null && resource["meta"]?["versionId"]?.GetValue<string>() == versionId
            ? Resource(resource)
            : FhirNotFound(type, $"{id}/_history/{versionId}");
    }

    private async Task<(PlanNetDirectorySnapshot? Value, IActionResult? Result)> SnapshotAsync(CancellationToken ct)
    {
        var directory = PublicDirectoryAccessFilter.Directory(HttpContext);
        try
        {
            return (await _store.GetAsync(directory, PublicDirectoryAccessFilter.Tenant(HttpContext), ct), null);
        }
        catch (PlanNetDirectoryUnavailableException ex)
        {
            // The reason is for operators; upstream detail is not for the public.
            _logger.LogWarning("Public directory {Directory} unavailable: {Reason}",
                LogSanitizer.Sanitize(directory), LogSanitizer.Sanitize(ex.Message));
            return (null, FhirBadGateway("The provider directory is temporarily unavailable."));
        }
    }

    private IActionResult NotADirectoryType(string type)
        => StatusCode(404, new Hl7.Fhir.Model.OperationOutcome
        {
            Issue =
            [
                new Hl7.Fhir.Model.OperationOutcome.IssueComponent
                {
                    Severity = Hl7.Fhir.Model.OperationOutcome.IssueSeverity.Error,
                    Code = Hl7.Fhir.Model.OperationOutcome.IssueType.NotSupported,
                    Diagnostics = $"The provider directory does not serve '{LogSanitizer.Sanitize(type)}'.",
                },
            ],
        });

    private IActionResult Resource(JsonObject resource)
    {
        if (resource["meta"]?["versionId"]?.GetValue<string>() is { } version)
            HttpContext.Response.Headers.ETag = $"W/\"{version}\"";
        return Json(resource);
    }

    private ContentResult Json(JsonNode node)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            node.WriteTo(writer);
        return Content(Encoding.UTF8.GetString(buffer.WrittenSpan), FhirJson, Encoding.UTF8);
    }

    private string SearchBundle(string type, PlanNetSearchResult result, PlanNetDirectorySnapshot snapshot)
    {
        var baseUrl = DirectoryBaseUrl;
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("resourceType", "Bundle");
            writer.WriteString("id", Guid.NewGuid().ToString());
            writer.WriteStartObject("meta");
            writer.WriteString("lastUpdated", snapshot.BuiltAt.ToString("o"));
            writer.WriteEndObject();
            writer.WriteString("type", "searchset");
            writer.WriteNumber("total", result.Total);

            writer.WriteStartArray("link");
            WriteLink(writer, "self", PageUrl(baseUrl, type, result, result.Page));
            if (result.PageSize > 0 && result.Page * result.PageSize < result.Total)
                WriteLink(writer, "next", PageUrl(baseUrl, type, result, result.Page + 1));
            if (result.Page > 1)
                WriteLink(writer, "previous", PageUrl(baseUrl, type, result, result.Page - 1));
            writer.WriteEndArray();

            writer.WriteStartArray("entry");
            foreach (var resource in result.Matches) WriteEntry(writer, baseUrl, resource, "match");
            foreach (var resource in result.Included) WriteEntry(writer, baseUrl, resource, "include");
            if (result.Ignored.Count > 0)
            {
                // Lenient handling: say what was not applied, so a client never
                // mistakes an unfiltered page for a filtered one.
                var outcome = new JsonObject
                {
                    ["resourceType"] = "OperationOutcome",
                    ["issue"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["severity"] = "warning",
                            ["code"] = "not-supported",
                            ["diagnostics"] = "Not applied: " + string.Join(", ", result.Ignored.Select(i => LogSanitizer.Sanitize(i))),
                        },
                    },
                };
                writer.WriteStartObject();
                writer.WritePropertyName("resource");
                outcome.WriteTo(writer);
                writer.WriteStartObject("search");
                writer.WriteString("mode", "outcome");
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteEntry(Utf8JsonWriter writer, string baseUrl, JsonObject resource, string mode)
    {
        writer.WriteStartObject();
        writer.WriteString("fullUrl", $"{baseUrl}/{PlanNetDirectorySnapshot.Key(resource)}");
        writer.WritePropertyName("resource");
        resource.WriteTo(writer);
        writer.WriteStartObject("search");
        writer.WriteString("mode", mode);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteLink(Utf8JsonWriter writer, string relation, string url)
    {
        writer.WriteStartObject();
        writer.WriteString("relation", relation);
        writer.WriteString("url", url);
        writer.WriteEndObject();
    }

    private static string PageUrl(string baseUrl, string type, PlanNetSearchResult result, int page)
    {
        var parts = result.Applied
            .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}")
            .Append($"_count={result.PageSize}")
            .Append($"_page={page}");
        return $"{baseUrl}/{type}?{string.Join('&', parts)}";
    }
}

/// <summary>
/// Resolves the public directory id to its tenant before the action runs, and
/// makes the caller anonymous for the rest of the request. See
/// <see cref="PublicProviderDirectoryController"/>.
/// </summary>
public sealed class PublicDirectoryAccessFilter : IAsyncActionFilter
{
    private const string TenantItemKey = "PublicDirectoryTenant";
    private const string DirectoryItemKey = "PublicDirectoryId";

    private readonly PublicProviderDirectoryOptions _options;

    public PublicDirectoryAccessFilter(IOptions<PublicProviderDirectoryOptions> options) => _options = options.Value;

    /// <summary>The tenant of the directory this request reads.</summary>
    public static string Tenant(HttpContext http)
        => http.Items[TenantItemKey] as string
           ?? throw new InvalidOperationException("No public directory was resolved for this request.");

    /// <summary>The published id of the directory this request reads.</summary>
    public static string Directory(HttpContext http)
        => http.Items[DirectoryItemKey] as string
           ?? throw new InvalidOperationException("No public directory was resolved for this request.");

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var directory = context.RouteData.Values["directory"] as string;
        var tenant = _options.ResolveTenant(directory);
        if (tenant is null)
        {
            context.Result = new NotFoundObjectResult(new Hl7.Fhir.Model.OperationOutcome
            {
                Issue =
                [
                    new Hl7.Fhir.Model.OperationOutcome.IssueComponent
                    {
                        Severity = Hl7.Fhir.Model.OperationOutcome.IssueSeverity.Error,
                        Code = Hl7.Fhir.Model.OperationOutcome.IssueType.NotFound,
                        Diagnostics = "No public provider directory is published under this name.",
                    },
                ],
            });
            return;
        }

        var http = context.HttpContext;
        http.User = new ClaimsPrincipal(new ClaimsIdentity());
        http.Items.Remove("TenantId");
        http.Items[TenantItemKey] = tenant;
        http.Items[DirectoryItemKey] = directory;
        await next();
    }
}
