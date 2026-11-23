using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Infrastructure;

/// <summary>
/// <c>GET /shadow-adjudication/findings/export.csv?status=Approved</c>: the
/// recovery worklist CSV the plan loads into its own mass adjustment. The
/// browser holds no CHO token, so the portal fetches the file for the signed-in
/// user through <see cref="IShadowFindingsClient"/>; claims-service enforces
/// <c>claims:import</c> and the tenant.
/// </summary>
public static class ShadowFindingsExportEndpoint
{
    public const string Route = "/shadow-adjudication/findings/export.csv";

    public static string PathFor(IEnumerable<string> statuses)
    {
        var query = string.Join("&", statuses.Select(s => $"status={Uri.EscapeDataString(s)}"));
        return query.Length == 0 ? Route : $"{Route}?{query}";
    }

    public static RouteHandlerBuilder MapShadowFindingsExport(this IEndpointRouteBuilder endpoints)
        => endpoints.MapGet(Route, HandleAsync).RequireAuthorization().WithName("ShadowFindingsExport");

    internal static async Task<IResult> HandleAsync(string[]? status, HttpContext http, IShadowFindingsClient findings)
    {
        var statuses = (status ?? []).Where(s => ShadowFindingStatuses.All.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
        if (statuses.Count == 0) statuses.Add(ShadowFindingStatuses.Approved);
        byte[]? csv;
        try
        {
            csv = await findings.GetExportCsvAsync(statuses);
        }
        catch (ServiceUnavailableException)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        if (csv is null)
            return Results.NotFound();
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        http.Response.Headers["Cache-Control"] = "no-store";
        return Results.File(csv, "text/csv; charset=utf-8",
            $"shadow-findings-{string.Join("-", statuses).ToLowerInvariant()}-{DateTime.UtcNow:yyyyMMdd}.csv");
    }
}
