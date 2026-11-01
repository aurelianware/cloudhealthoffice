using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaimsService.Models;
using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.Extensions.Caching.Memory;

namespace ClaimsService.Services.Resolution;

/// <summary>
/// The PA rule set a claim's benefit plan resolves to (its program's state,
/// line of business and program code): the same rule set CRD and PAS evaluate
/// a member of the plan against, read from benefit-plan-service
/// <c>GET /api/v1/prior-auth-rules/plans/{planId}/required-services</c>.
/// </summary>
public interface IPriorAuthRuleScopeClient
{
    /// <summary>
    /// The plan's rule scope, or null when the plan has no rule set to read
    /// (no program, unknown program, or no such plan): an answer, not an
    /// outage. Any failure to get an answer throws
    /// <see cref="PriorAuthRuleScopeUnavailableException"/>.
    /// </summary>
    Task<PriorAuthRuleScope?> GetScopeAsync(string tenantId, string planId, CancellationToken ct = default);
}

public sealed class PriorAuthRuleScopeUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Which claim lines a plan's PA rule set says need an authorization. Only
/// rules whose answer a claim can settle on its own are applied:
/// <list type="bullet">
///   <item><c>Always</c>: the code (or its prefix), at the listed places of service if any;</item>
///   <item><c>WithDiagnosis</c>: the code billed with one of the listed diagnoses.</item>
/// </list>
/// <c>AboveQuantityLimit</c> rules are not applied: claims cannot yet count a
/// member's use against the limit. Exemptions (gold card, provider type,
/// member age) are not evaluated either; <see cref="Exemptions"/> names them so
/// the stage can send a missing authorization to review instead of denying.
/// </summary>
public sealed class PriorAuthRuleScope
{
    public string? RuleSet { get; init; }
    public IReadOnlyList<PriorAuthRuleScopeEntry> Entries { get; init; } = [];
    /// <summary>Kinds of exemption rules in the set, e.g. TxGoldCardExemption.</summary>
    public IReadOnlyList<string> Exemptions { get; init; } = [];

    public bool HasExemptions => Exemptions.Count > 0;

    /// <summary>The entry that puts this line in scope, or null.</summary>
    public PriorAuthRuleScopeEntry? Match(AdapterClaim claim, AdapterClaimLine line)
    {
        var code = line.ProcedureCode?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(code)) return null;
        var pos = (string.IsNullOrWhiteSpace(line.PlaceOfServiceCode) ? claim.PlaceOfServiceCode : line.PlaceOfServiceCode)?.Trim();
        HashSet<string>? diagnoses = null;

        foreach (var entry in Entries)
        {
            if (!entry.AppliesTo(code)) continue;
            switch (entry.Condition)
            {
                case PriorAuthRuleCondition.Always:
                    if (entry.PlacesOfService.Count == 0
                        || (pos is not null && entry.PlacesOfService.Contains(pos.PadLeft(2, '0'), StringComparer.Ordinal)))
                        return entry;
                    break;
                case PriorAuthRuleCondition.WithDiagnosis:
                    diagnoses ??= claim.DiagnosisCodes
                        .Select(d => d.Code.Replace(".", "", StringComparison.Ordinal).Trim().ToUpperInvariant())
                        .Where(d => d.Length > 0)
                        .ToHashSet(StringComparer.Ordinal);
                    if (entry.Diagnoses.Any(diagnoses.Contains)) return entry;
                    break;
            }
        }
        return null;
    }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PriorAuthRuleCondition
{
    Always,
    WithDiagnosis,
    AboveQuantityLimit,
}

public sealed class PriorAuthRuleScopeEntry
{
    public string RuleId { get; init; } = string.Empty;
    public string? Name { get; init; }
    public bool AllServices { get; init; }
    public IReadOnlyList<string> Codes { get; init; } = [];
    public IReadOnlyList<string> CodePrefixes { get; init; } = [];
    public PriorAuthRuleCondition Condition { get; init; }
    public IReadOnlyList<string> PlacesOfService { get; init; } = [];
    public IReadOnlyList<string> Diagnoses { get; init; } = [];

    internal bool AppliesTo(string code) =>
        AllServices
        || Codes.Contains(code, StringComparer.OrdinalIgnoreCase)
        || CodePrefixes.Any(p => code.StartsWith(p, StringComparison.OrdinalIgnoreCase));
}

/// <summary>HTTP client for <see cref="IPriorAuthRuleScopeClient"/>, on the benefit-plan-service client.</summary>
public sealed class HttpPriorAuthRuleScopeClient(
    IHttpClientFactory httpClientFactory,
    ILogger<HttpPriorAuthRuleScopeClient> logger) : IPriorAuthRuleScopeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<PriorAuthRuleScope?> GetScopeAsync(string tenantId, string planId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(planId)) return null;
        try
        {
            var client = httpClientFactory.CreateClient(HttpBenefitPlanResolver.HttpClientName);
            var url = $"/api/v1/prior-auth-rules/plans/{Uri.EscapeDataString(planId.Trim())}/required-services?includeDrugs=true";
            using var request = new HttpRequestMessage(HttpMethod.Get, url).UseChoServiceIdentity();
            request.Headers.Add("X-Tenant-ID", tenantId);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode)
                throw new PriorAuthRuleScopeUnavailableException(
                    $"benefit-plan-service returned {(int)response.StatusCode} for the plan's PA rule set");

            var dto = await response.Content.ReadFromJsonAsync<PlanRequiredServicesDto>(JsonOptions, ct).ConfigureAwait(false)
                ?? throw new PriorAuthRuleScopeUnavailableException("benefit-plan-service returned an empty PA rule set body");
            if (!dto.Resolved || dto.List is null) return null;

            return new PriorAuthRuleScope
            {
                RuleSet = dto.List.RuleSet,
                Entries = dto.List.Entries ?? [],
                Exemptions = (dto.List.Exemptions ?? []).Select(e => e.Kind ?? "Exemption").Distinct().ToList(),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not PriorAuthRuleScopeUnavailableException)
        {
            logger.LogWarning(ex, "Failed to read the PA rule set for plan {PlanId}", LogSanitizer.Sanitize(planId));
            throw new PriorAuthRuleScopeUnavailableException($"PA rule set lookup failed ({ex.GetType().Name})", ex);
        }
    }

    private sealed class PlanRequiredServicesDto
    {
        public bool Resolved { get; set; }
        public ListDto? List { get; set; }
    }

    private sealed class ListDto
    {
        public string? RuleSet { get; set; }
        public List<PriorAuthRuleScopeEntry>? Entries { get; set; }
        public List<ExemptionDto>? Exemptions { get; set; }
    }

    private sealed class ExemptionDto
    {
        public string? Kind { get; set; }
    }
}

/// <summary>Caches answers (including "no rule set") per tenant and plan for five minutes, like the plan resolver. Failures are not cached.</summary>
public sealed class CachingPriorAuthRuleScopeClient(IPriorAuthRuleScopeClient inner, IMemoryCache cache) : IPriorAuthRuleScopeClient
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    public async Task<PriorAuthRuleScope?> GetScopeAsync(string tenantId, string planId, CancellationToken ct = default)
    {
        var key = $"pa-rule-scope:{tenantId}:{planId}";
        if (cache.TryGetValue(key, out PriorAuthRuleScope? cached)) return cached;
        var scope = await inner.GetScopeAsync(tenantId, planId, ct).ConfigureAwait(false);
        cache.Set(key, scope, Ttl);
        return scope;
    }
}
