namespace CloudHealthOffice.Architecture.Tests;

/// <summary>
/// The Provider Access <c>ExplanationOfBenefit</c> has exactly two upstreams:
/// claims-service for the member's claims, and authorization-service for their
/// prior authorizations. encounter-service is deliberately not one of them.
///
/// <para>
/// Why that is a decision rather than an omission is ADR 028. In short: every
/// CHO encounter record is generated from a claim CHO already serves on this
/// path — encounter-service is the outbound 837 envelope, with a required
/// <c>ClaimId</c>; encounter-submission-service is created from the
/// <c>adjudication-completed</c> event and submits the claim it fetches from
/// claims-service; and an inbound 837 becomes a <c>Claim</c>, never an
/// encounter. Projecting encounters onto the non-financial EOB as well would
/// hand an attributed provider two resources for one visit, the second
/// carrying less than the first.
/// </para>
///
/// <para>
/// This is a source check, so it is a floor rather than a proof: it catches
/// the change that would make the duplication real — wiring an encounter
/// upstream into the EOB controller — so that whoever makes it reaches ADR 028
/// instead of rediscovering the reasoning. It cannot see a deployment that
/// starts ingesting encounters directly, which is the other thing that would
/// overturn the decision; that one is stated in the ADR.
/// </para>
/// </summary>
public class ProviderAccessEncounterSourcesTests
{
    [Fact]
    public void TheProviderAccessEobControllerDoesNotReadEncounterService()
    {
        var controller = File.ReadAllText(Path.Combine(
            Root(), "src", "services", "fhir-service", "Controllers",
            "ExplanationOfBenefitController.cs"));

        // The upstreams it does name, asserted first so this test fails loudly
        // if the file is restructured rather than passing because it found
        // nothing to check.
        Assert.Contains("UpstreamClientNames.ClaimsService", controller);
        Assert.Contains("UpstreamClientNames.AuthorizationService", controller);

        Assert.False(
            controller.Contains("EncounterService", StringComparison.Ordinal),
            "The Provider Access EOB is projected from claims, not from encounter records: "
            + "every CHO encounter is generated from a claim this path already serves, so a "
            + "second projection would duplicate it. "
            + "See docs/adr/028-provider-access-encounters-are-claims.md.");
    }

    [Fact]
    public void ClaimsServiceIsTheOnlyProjectorOfTheNonFinancialEob()
    {
        // The CARIN BB Basis profiles are the Provider Access payload. One
        // projector owns them; a second one appearing elsewhere in the tree is
        // the duplication ADR 028 rules out.
        var projectors = Directory
            .EnumerateFiles(Path.Combine(Root(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains(
                "C4BB-ExplanationOfBenefit-Professional-NonClinician-Basis", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            projectors is ["CarinNonFinancialEobProjector.cs"],
            "claims-service owns the non-financial EOB projection, and is meant to be its only "
            + "owner. Found: " + string.Join(", ", projectors) + ". "
            + "See docs/adr/028-provider-access-encounters-are-claims.md.");
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Packages.props")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
