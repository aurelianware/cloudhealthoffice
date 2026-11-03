using System.Text.RegularExpressions;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Infrastructure;

/// <summary>
/// <c>GET /shadow-adjudication/runs/{runId}/discrepancies.csv</c>: the link
/// the Shadow Adjudication page gives the browser for a run's discrepancy CSV.
/// The browser holds no CHO token, so the portal fetches the file for the
/// signed-in user through <see cref="IShadowAdjudicationClient"/>, whose
/// HttpClient attaches that user's token; claims-service enforces
/// <c>claims:import</c> and the tenant. A run that is not the caller's answers 404.
/// </summary>
public static class ShadowDiscrepancyDownloadEndpoint
{
    public const string Route = "/shadow-adjudication/runs/{runId}/discrepancies.csv";

    private static readonly Regex RunIdFormat = new(
        "^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static bool IsValidRunId(string? runId) => !string.IsNullOrEmpty(runId) && RunIdFormat.IsMatch(runId);

    public static string PathFor(string runId, bool includeUndecided)
        => $"/shadow-adjudication/runs/{Uri.EscapeDataString(runId)}/discrepancies.csv" +
           (includeUndecided ? "?includeUndecided=true" : "");

    public static RouteHandlerBuilder MapShadowDiscrepancyDownload(this IEndpointRouteBuilder endpoints)
        => endpoints.MapGet(Route, HandleAsync).RequireAuthorization().WithName("ShadowDiscrepancyDownload");

    internal static async Task<IResult> HandleAsync(
        string runId, bool? includeUndecided, HttpContext http, IShadowAdjudicationClient shadow)
    {
        if (!IsValidRunId(runId))
            return Results.NotFound();
        byte[]? csv;
        try
        {
            csv = await shadow.GetDiscrepanciesCsvAsync(runId, includeUndecided ?? false);
        }
        catch (ServiceUnavailableException)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        if (csv is null)
            return Results.NotFound();
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        http.Response.Headers["Cache-Control"] = "no-store";
        return Results.File(csv, "text/csv; charset=utf-8", $"shadow-adjudication-{runId}.csv");
    }
}
