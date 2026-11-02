using System.Net;
using System.Text.Json;
using AppealsService.Models;
using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.PlanConfiguration;

namespace AppealsService.Services;

/// <summary>
/// The program (line of business in a State, e.g. TX STAR) a member's case
/// falls under, found the way the plan was sold: the member's active
/// coverage names a benefit plan, and the plan names its program
/// (docs/architecture/plan-configuration.md §3).
/// </summary>
public interface IMemberProgramResolver
{
    /// <summary>
    /// The member's program for <paramref name="lineOfBusiness"/> on
    /// <paramref name="asOf"/>, or <c>null</c> when the member has no active
    /// coverage then, no coverage names a plan with a program of that line of
    /// business, or the member's programs of that line of business are in
    /// more than one State. Throws <see cref="HttpRequestException"/> when
    /// coverage-service or tenant-service could not be asked, so a caller can
    /// tell "no program" from "did not get to look".
    /// </summary>
    Task<ProgramDescriptor?> ResolveAsync(
        string tenantId, string memberId, LineOfBusiness lineOfBusiness, DateTime asOf, CancellationToken ct);
}

/// <summary>Reads coverage-service's active coverage, then tenant-service's programs.</summary>
public sealed class HttpMemberProgramResolver(
    IHttpClientFactory clients,
    IProgramDirectory programs,
    ILogger<HttpMemberProgramResolver> logger) : IMemberProgramResolver
{
    public const string CoverageClientName = "AppealCoverage";
    public const string DefaultCoverageServiceUrl = "http://coverage-service:8080";

    public async Task<ProgramDescriptor?> ResolveAsync(
        string tenantId, string memberId, LineOfBusiness lineOfBusiness, DateTime asOf, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(memberId)) return null;

        var planIds = await ActivePlanIdsAsync(tenantId, memberId, asOf, ct);
        var wanted = ProgramLineOfBusiness(lineOfBusiness);
        var matches = new List<ProgramDescriptor>();
        foreach (var planId in planIds)
        {
            if (await programs.ForBenefitPlanAsync(tenantId, planId, ct) is not { } program) continue;
            if (!program.Active) continue;
            if (!string.Equals(LinesOfBusiness.Normalize(program.LineOfBusiness), wanted, StringComparison.Ordinal)) continue;
            if (string.IsNullOrWhiteSpace(program.StateCode)) continue;
            matches.Add(program);
        }

        var states = matches.Select(p => p.StateCode.Trim().ToUpperInvariant()).Distinct().ToList();
        if (states.Count > 1)
        {
            logger.LogWarning(
                "Member {MemberId} has {LineOfBusiness} programs in more than one State ({States}); no State taken from the program",
                LogSanitizer.MaskId(memberId), lineOfBusiness, string.Join(",", states));
            return null;
        }
        return matches.FirstOrDefault();
    }

    /// <summary>
    /// The program line-of-business name (<see cref="LinesOfBusiness"/>) an
    /// appeal's line of business falls under. Part D is a Medicare program;
    /// the appeal model's Marketplace is the program model's Exchange.
    /// </summary>
    public static string ProgramLineOfBusiness(LineOfBusiness lineOfBusiness) => lineOfBusiness switch
    {
        LineOfBusiness.Medicare or LineOfBusiness.MedicarePartD => LinesOfBusiness.Medicare,
        LineOfBusiness.Medicaid => LinesOfBusiness.Medicaid,
        LineOfBusiness.Marketplace => LinesOfBusiness.Exchange,
        _ => LinesOfBusiness.Commercial
    };

    private async Task<IReadOnlyList<string>> ActivePlanIdsAsync(
        string tenantId, string memberId, DateTime asOf, CancellationToken ct)
    {
        var path = $"api/v1/coverage/member/{Uri.EscapeDataString(memberId)}/active?serviceDate={asOf:yyyy-MM-dd}";
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Tenant-ID", tenantId);
        // A lookup for this service's own deadline calculation: the caller
        // filing the appeal need not hold coverage:read.
        request.UseChoServiceIdentity();

        using var response = await clients.CreateClient(CoverageClientName).SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return [];
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (json.RootElement.ValueKind != JsonValueKind.Array) return [];
        return json.RootElement.EnumerateArray()
            .Select(c => c.TryGetProperty("planId", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
