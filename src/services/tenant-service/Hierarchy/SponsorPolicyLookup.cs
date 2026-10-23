using System.Net;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;

namespace TenantService.Hierarchy;

/// <summary>A sponsor policy and the eligibility classes under it.</summary>
/// <param name="Id">The policy's id, which is also its node id at sponsor-policy level.</param>
/// <param name="ClassScopeIds">Each class's node id at class level.</param>
public sealed record SponsorPolicyNode(string Id, IReadOnlyList<string> ClassScopeIds);

/// <summary>A sponsor's policies and their classes, from sponsor-service.</summary>
public interface ISponsorPolicyLookup
{
    /// <summary>
    /// Every policy the group offers, whether or not it is currently offered,
    /// each with its classes. Empty where the group has none, which is every
    /// group until a contract is entered. Throws where sponsor-service could
    /// not be asked, so a caller narrowing a list by sponsor is never handed
    /// "no policies" because a service was down.
    /// </summary>
    Task<IReadOnlyList<SponsorPolicyNode>> GetPoliciesAsync(string tenantId, string groupNumber, CancellationToken ct = default);
}

/// <summary>
/// Calls sponsor-service's <c>GET api/v1/sponsors/{group}/policies</c>
/// (plan-configuration.md §9b, built in step 3a) with tenant-service's own
/// service identity rather than the caller's token — the same reasoning as
/// <see cref="HttpBenefitPlanProgramLookup"/>: following the hierarchy is this
/// service's job, and a caller allowed to read a sponsor's settings need not be
/// allowed to read its contract. The token is minted for the tenant in
/// <c>X-Tenant-ID</c>, which is always the caller's.
/// </summary>
public sealed class HttpSponsorPolicyLookup(IHttpClientFactory factory) : ISponsorPolicyLookup
{
    public const string HttpClientName = "sponsor-service";

    public async Task<IReadOnlyList<SponsorPolicyNode>> GetPoliciesAsync(
        string tenantId, string groupNumber, CancellationToken ct = default)
    {
        var client = factory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"api/v1/sponsors/{Uri.EscapeDataString(groupNumber)}/policies");
        request.Headers.Add("X-Tenant-ID", tenantId);
        request.UseChoServiceIdentity();
        using var response = await client.SendAsync(request, ct);

        // A 404 here is the sponsor itself being unknown, which is a real
        // answer: that group has no policies. Anything else is thrown, because
        // a route that has moved or a service that is down must not read as an
        // empty contract.
        if (response.StatusCode == HttpStatusCode.NotFound) return [];
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];

        var policies = new List<SponsorPolicyNode>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            if (Text(element, "id") is not { Length: > 0 } id) continue;

            var classes = new List<string>();
            if (element.TryGetProperty("classes", out var classArray) && classArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in classArray.EnumerateArray())
                {
                    if (c.ValueKind != JsonValueKind.Object) continue;
                    if (Text(c, "code") is { Length: > 0 } code) classes.Add(SponsorScopeIds.ForClass(id, code));
                }
            }
            policies.Add(new SponsorPolicyNode(id, classes));
        }
        return policies;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>
/// How a sponsor policy and one of its classes are named as nodes of the
/// setting hierarchy.
///
/// <para>
/// Both forms are sponsor-service's: <c>SponsorPolicy.IdFor</c> builds
/// <c>{tenant}|{group}|{policyNumber}</c> and
/// <c>SponsorClass.ScopeIdFor</c> builds <c>{policyId}|{classCode}</c>. They
/// are restated here because sponsor-service and tenant-service share no
/// package, and the alternative — tenant-service asking sponsor-service to
/// name its own nodes on every read — would put a call in the path of a
/// question the id already answers. The format is pinned by a test, so a change
/// on either side fails rather than quietly narrowing to nothing; if a third
/// service ever needs it, it belongs in a shared contract instead.
/// </para>
/// </summary>
public static class SponsorScopeIds
{
    public static string ForClass(string policyId, string classCode) => $"{policyId}|{classCode}";

    /// <summary>
    /// The group number inside a policy's id, or null where the id is not in
    /// that form. Needed because sponsor-service lists policies by group and
    /// a policy node names only itself; null is answered as "cannot tell"
    /// rather than guessed at, so the caller reports the level unnarrowed
    /// instead of narrowing it to nothing.
    /// </summary>
    public static string? GroupNumberOf(string policyId)
    {
        if (string.IsNullOrWhiteSpace(policyId)) return null;
        var parts = policyId.Split('|');
        return parts.Length == 3 && parts[1].Length > 0 ? parts[1] : null;
    }
}
