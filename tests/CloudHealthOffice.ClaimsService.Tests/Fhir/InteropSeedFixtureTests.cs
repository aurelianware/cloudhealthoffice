using System.Text.Json;
using ClaimsService.Fhir;
using ClaimsService.Models;
using FluentAssertions;

namespace CloudHealthOffice.ClaimsService.Tests.Fhir;

/// <summary>
/// <c>interop/cho-seed.json</c> is the record set the interop-cho profile serves
/// to the Inferno suites. It speaks claims-service's HTTP API, which means it
/// carries two things nothing else would catch if they drifted: the numeric
/// values of <see cref="ClaimType"/> and <see cref="LineOfBusiness"/> (the
/// service serializes enums as numbers), and the UB-04
/// <see cref="InstitutionalClaimDetails.FacilityTypeCode"/> that decides which
/// CARIN Basis profile an institutional claim lands on.
///
/// <para>
/// Both failure modes are quiet. A renumbered enum makes the fixture a
/// different claim; a <c>facilityTypeCode</c> the projector cannot read as
/// inpatient falls to the outpatient profile, which is the weaker of the two.
/// Either way the suite would run against something other than what the fixture
/// says, and report it as a result. These tests deserialize the fixture with
/// the service's own types and assert what it actually means.
/// </para>
/// </summary>
public sealed class InteropSeedFixtureTests
{
    private static readonly JsonElement Fixture = LoadFixture();

    [Fact]
    public void Each_seeded_claim_projects_to_the_CARIN_profile_it_declares()
    {
        var projector = new CarinNonFinancialEobProjector();

        foreach (var element in AllClaims())
        {
            var declared = element.GetProperty("expectProfile").GetString();
            declared.Should().NotBeNullOrWhiteSpace(
                "every seeded claim must say which profile it is there to produce");

            var claim = Deserialize(element).ToClaim();
            var profiles = projector.Project(claim)["meta"]!["profile"]!.AsArray()
                .Select(p => p!.GetValue<string>());

            profiles.Should().Contain(declared!,
                "{0} declares {1}", element.GetProperty("claimNumber").GetString(), declared);
        }
    }

    [Fact]
    public void The_fixture_covers_more_than_one_CARIN_profile()
    {
        // A seed that exercised one profile would leave the institutional
        // branch — the one with the bill-classification rule in it — untested
        // by every suite run against this profile.
        Fixture.GetProperty("claims").EnumerateArray()
            .Select(c => c.GetProperty("expectProfile").GetString())
            .Distinct()
            .Should().HaveCountGreaterThan(1);
    }

    [Fact]
    public void Every_seeded_claim_names_the_member_the_fixture_seeds()
    {
        foreach (var (member, claims) in Households())
        {
            var memberId = member.GetProperty("memberId").GetString();
            foreach (var element in claims)
            {
                element.GetProperty("memberId").GetString().Should().Be(memberId,
                    "a claim for a member the fixture does not seed is served to nobody");
            }
        }
    }

    [Fact]
    public void More_than_one_member_is_in_the_bulk_data_clients_panel()
    {
        // The Bulk Data kit fails an export that holds fewer than two Patients
        // (run 40979709678). The export is the backend client's provider's
        // panel: the members with a claim from that provider.
        var provider = Fixture.GetProperty("backendClient").GetProperty("providerId").GetString();

        Households()
            .Where(h => h.Claims.Any(c => c.GetProperty("billingProviderNPI").GetString() == provider))
            .Select(h => h.Member.GetProperty("memberId").GetString())
            .Distinct()
            .Should().HaveCountGreaterThan(1);
    }

    /// <summary>The first member and each of <c>additionalMembers</c>, with their claims.</summary>
    private static IEnumerable<(JsonElement Member, JsonElement[] Claims)> Households()
    {
        yield return (Fixture.GetProperty("member"), Fixture.GetProperty("claims").EnumerateArray().ToArray());
        if (!Fixture.TryGetProperty("additionalMembers", out var additional)) yield break;
        foreach (var household in additional.EnumerateArray())
        {
            yield return (household.GetProperty("member"), household.GetProperty("claims").EnumerateArray().ToArray());
        }
    }

    private static IEnumerable<JsonElement> AllClaims() => Households().SelectMany(h => h.Claims);

    /// <summary>
    /// Deserializes as the controller's model binder would, minus the keys the
    /// fixture keeps for itself (<c>_comment</c>, <c>expectProfile</c>), which
    /// <c>scripts/interop/seed-cho.mjs</c> strips before it posts.
    /// </summary>
    private static AdapterClaim Deserialize(JsonElement claim)
    {
        var payload = new Dictionary<string, JsonElement>();
        foreach (var property in claim.EnumerateObject())
        {
            if (property.Name is "_comment" or "expectProfile") continue;
            payload[property.Name] = property.Value;
        }

        return JsonSerializer.Deserialize<AdapterClaim>(
            JsonSerializer.Serialize(payload),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("The seed fixture did not deserialize to a claim.");
    }

    private static JsonElement LoadFixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "interop", "cho-seed.json")))
        {
            directory = directory.Parent;
        }

        var path = directory is null
            ? throw new DirectoryNotFoundException(
                $"No ancestor of '{AppContext.BaseDirectory}' contains interop/cho-seed.json.")
            : Path.Combine(directory.FullName, "interop", "cho-seed.json");

        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    }
}
