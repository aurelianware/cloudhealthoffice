using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CHO.TmppmIngestionService.Models;

namespace CHO.TmppmIngestionService.Services;

/// <summary>
/// Persists TMPPM rules, editions and diffs, and publishes overrides, through
/// terminology-service's API (<c>/api/v1/tmppm/*</c>). The tool no longer
/// connects to terminology-service's database: that service owns it and
/// authorizes every write.
/// <list type="bullet">
///   <item>Rules, editions and diffs are shared by every tenant: the token
///   must hold platform:admin (a platform administrator's CHO token).</item>
///   <item>Overrides (<c>--tenant</c>) go to the token's tenant, which must be
///   the one named: the request carries it in X-Tenant-ID and
///   terminology-service refuses a mismatch. Needs settings:manage.</item>
/// </list>
/// The bearer token comes from <c>Terminology:AccessTokenFile</c> (read again
/// before every request, so a refresher can replace it; CHO tokens last 5
/// minutes) or <c>Terminology:AccessToken</c>. Writes happen after the
/// downloads and parsing, at the end of the run.
/// </summary>
public class TmppmRuleStore(HttpClient http, IConfiguration config, ILogger<TmppmRuleStore> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpRequestMessage Request(HttpMethod method, string path, object? body = null, string? tenantId = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: Json);

        var token = ReadToken();
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (!string.IsNullOrEmpty(tenantId))
            request.Headers.Add("X-Tenant-ID", tenantId);
        return request;
    }

    private string? ReadToken()
    {
        var file = config["Terminology:AccessTokenFile"];
        if (!string.IsNullOrWhiteSpace(file))
            return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        return config["Terminology:AccessToken"];
    }

    private async Task SendAsync(HttpRequestMessage request, string what)
    {
        using (request)
        using (var response = await http.SendAsync(request))
        {
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException(
                    $"terminology-service refused {what}: {(int)response.StatusCode} {detail}");
            }
        }
    }

    /// <summary>Upsert extracted PA rules (unique by RuleId). Needs platform:admin.</summary>
    public async Task<int> UpsertRulesAsync(IEnumerable<TmppmPaRule> rules)
    {
        var list = rules.ToList();
        if (list.Count == 0)
            return 0;

        await SendAsync(Request(HttpMethod.Put, "api/v1/tmppm/rules", list), "the rule upsert");
        logger.LogInformation("Upserted {Count} PA rules", list.Count);
        return list.Count;
    }

    /// <summary>
    /// Publish rules as ConceptMap overrides for <paramref name="tenantId"/>
    /// (the token's tenant). Without a tenant nothing is published: an override
    /// with no tenant was never matched by any translation.
    /// </summary>
    public async Task<int> PublishAsConceptMapOverridesAsync(
        IEnumerable<TmppmPaRule> rules, string editionId, string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            logger.LogWarning("No --tenant given: no ConceptMap overrides published (overrides belong to a tenant)");
            return 0;
        }

        var list = rules.Where(r => r.ProcedureCodes.Count > 0).ToList();
        if (list.Count == 0)
            return 0;

        using var request = Request(HttpMethod.Post, "api/v1/tmppm/overrides",
            new { editionId, rules = list }, tenantId);
        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"terminology-service refused the override publish for tenant {tenantId}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }

        var result = await response.Content.ReadFromJsonAsync<PublishResult>(Json);
        logger.LogInformation("Published {Count} ConceptMap overrides for tenant {Tenant}", result?.OverridesPublished ?? 0, tenantId);
        return result?.OverridesPublished ?? 0;
    }

    /// <summary>Save edition metadata. Needs platform:admin.</summary>
    public async Task SaveEditionAsync(TmppmEdition edition)
    {
        await SendAsync(Request(HttpMethod.Put, $"api/v1/tmppm/editions/{Uri.EscapeDataString(edition.EditionId)}", edition),
            "the edition save");
        logger.LogInformation("Saved edition {Id} with {Count} chapters", edition.EditionId, edition.Chapters.Count);
    }

    /// <summary>The most recent edition, for change detection; null when none exists.</summary>
    public async Task<TmppmEdition?> GetLatestEditionAsync()
    {
        using var request = Request(HttpMethod.Get, "api/v1/tmppm/editions/current");
        using var response = await http.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"terminology-service refused the edition read: {(int)response.StatusCode}");
        return await response.Content.ReadFromJsonAsync<TmppmEdition>(Json);
    }

    /// <summary>Save a diff report. Needs platform:admin.</summary>
    public async Task SaveDiffReportAsync(TmppmDiffReport diff)
    {
        await SendAsync(Request(HttpMethod.Post, "api/v1/tmppm/diffs", diff), "the diff save");
        logger.LogInformation("Saved diff report: {From} → {To} ({Added} added, {Modified} modified, {Removed} removed)",
            diff.FromEdition, diff.ToEdition, diff.AddedCount, diff.ModifiedCount, diff.RemovedCount);
    }

    private sealed record PublishResult(string MapVersionId, int OverridesPublished);
}
