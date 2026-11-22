using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// What the CRD hook requests read from CHO before the kit runs: the seeded
/// member's Coverage id, and the lapsed member's, so the EHR's Coverage in
/// each request names one CHO holds.
/// </summary>
public sealed record CrdSourceData(string CoverageId, string LapsedMemberId, string LapsedCoverageId)
{
    /// <summary>The seed's member whose only coverage has ended.</summary>
    public const string LapsedMember = "INTEROP-MEM-3";

    public static async Task<CrdSourceData> ObtainAsync(ChoSeedFixture fixture, string hostRootUrl, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.DevelopmentToken());

        var coverage = await CoverageIdAsync(http, hostRootUrl, fixture.Member.MemberId, ct)
            ?? throw new InteropEnvironmentException(
                $"CHO holds no Coverage for the seeded member {fixture.Member.MemberId}, so the hook requests would " +
                "name a coverage CHO does not hold.");

        // A search may answer only current coverage; the lapsed member's id is
        // then the one CHO's FHIR projection gives every coverage it stores.
        var lapsed = await CoverageIdAsync(http, hostRootUrl, LapsedMember, ct) ?? $"{LapsedMember}-INTEROP-GRP-1";
        return new CrdSourceData(coverage, LapsedMember, lapsed);
    }

    private static async Task<string?> CoverageIdAsync(HttpClient http, string root, string memberId, CancellationToken ct)
    {
        var response = await http.GetAsync(
            $"{root.TrimEnd('/')}/fhir/r4/Coverage?patient={Uri.EscapeDataString(memberId)}", ct);
        if (!response.IsSuccessStatusCode) return null;
        var body = await response.Content.ReadAsStringAsync(ct);
        return (JsonNode.Parse(body)?["entry"] as JsonArray)?
            .Select(e => e?["resource"])
            .FirstOrDefault(r => r?["resourceType"]?.GetValue<string>() == "Coverage")?["id"]?.GetValue<string>();
    }
}
