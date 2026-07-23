using CloudHealthOffice.BenchmarkClaimGenerator.Generators;
using CloudHealthOffice.BenchmarkClaimGenerator.Models;
using CloudHealthOffice.BenchmarkClaimGenerator.ReferenceData;
using CloudHealthOffice.Tools.MccPlatformValidator;

namespace CloudHealthOffice.MccPlatformValidator.Tests;

public class MccFixtureIsolationTests
{
    [Fact]
    public void IsolateCobPendMembers_GivesSupportedCobPendClaimsDistinctRunScopedMemberIds()
    {
        var generator = new EdgeCaseClaimGenerator(new InMemoryReferenceDataProvider());
        var first = generator.Generate(1, nameof(EdgeCaseScenario.CobSecondaryPayer), new Random(42));
        var second = generator.Generate(2, nameof(EdgeCaseScenario.CobSecondaryPayer), new Random(43));
        first.ClaimId = "MCC-E-0000001";
        second.ClaimId = "MCC-E-0000002";
        first.Member.MemberId = "MBR-DUPLICATE";
        first.Member.SubscriberId = "SUB-DUPLICATE";
        second.Member.MemberId = "MBR-DUPLICATE";
        second.Member.SubscriberId = "SUB-DUPLICATE";

        MccFixtureIsolation.IsolateCobPendMembers([first, second], seed: 42);

        Assert.NotEqual(first.Member.MemberId, second.Member.MemberId);
        Assert.StartsWith("MCCCB42", first.Member.MemberId, StringComparison.Ordinal);
        Assert.StartsWith("MCCCB42", second.Member.MemberId, StringComparison.Ordinal);
        Assert.Equal(14, first.Member.MemberId.Length);
        Assert.Equal(14, second.Member.MemberId.Length);
        Assert.True($"MCCMED{first.Member.MemberId}".Length <= 20);
        Assert.True($"MCCMED{second.Member.MemberId}".Length <= 20);
        Assert.Equal(first.Member.MemberId, first.Member.SubscriberId);
        Assert.Equal(second.Member.MemberId, second.Member.SubscriberId);
    }

    [Fact]
    public void IsolateCobPendMembers_UpdatesDependentSubscriberLinks()
    {
        var generator = new EdgeCaseClaimGenerator(new InMemoryReferenceDataProvider());
        var claim = generator.Generate(1, nameof(EdgeCaseScenario.CobSecondaryPayer), new Random(42));
        claim.Member.Dependents.Add(new SyntheticDependent
        {
            MemberId = "DEP-1",
            SubscriberMemberId = "OLD-MEMBER",
            SubscriberId = "OLD-SUBSCRIBER",
            Coverages =
            {
                new SyntheticCoverage
                {
                    MemberId = "DEP-1",
                    SubscriberId = "OLD-SUBSCRIBER"
                }
            }
        });

        MccFixtureIsolation.IsolateCobPendMembers([claim], seed: 42);

        var dependent = Assert.Single(claim.Member.Dependents);
        Assert.Equal(claim.Member.MemberId, dependent.SubscriberMemberId);
        Assert.Equal(claim.Member.SubscriberId, dependent.SubscriberId);
        Assert.Equal("DEP-1", dependent.MemberId);
        Assert.Equal("DEP-1", dependent.Coverages[0].MemberId);
        Assert.Equal(claim.Member.SubscriberId, dependent.Coverages[0].SubscriberId);
    }

    [Theory]
    [InlineData(123456, "", "MCCCB560000001")]
    [InlineData(-42, "MCC-E-0000477", "MCCCB420000477")]
    [InlineData(7, "CLAIM-X-ABC", "MCCCB070000001")]
    public void IsolateCobPendMembers_BoundsIsolatedMemberIdForCoverageValidation(
        int seed,
        string claimId,
        string expectedMemberId)
    {
        var generator = new EdgeCaseClaimGenerator(new InMemoryReferenceDataProvider());
        var claim = generator.Generate(1, nameof(EdgeCaseScenario.MedicaidDualEligible), new Random(42));
        claim.ClaimId = claimId;

        MccFixtureIsolation.IsolateCobPendMembers([claim], seed);

        Assert.Equal(expectedMemberId, claim.Member.MemberId);
        Assert.DoesNotContain("-", claim.Member.MemberId, StringComparison.Ordinal);
        Assert.Equal(14, claim.Member.MemberId.Length);
        Assert.True($"MCCMED{claim.Member.MemberId}".Length <= 20);
    }

    [Fact]
    public void IsolateCobPendMembers_DoesNotRewriteUnsupportedOrPrimaryCobScenarios()
    {
        var generator = new EdgeCaseClaimGenerator(new InMemoryReferenceDataProvider());
        var primary = generator.Generate(1, nameof(EdgeCaseScenario.CobPrimaryPayer), new Random(42));
        var subrogation = generator.Generate(2, nameof(EdgeCaseScenario.SubrogationWorkersComp), new Random(43));
        var originalPrimaryMemberId = primary.Member.MemberId;
        var originalSubrogationMemberId = subrogation.Member.MemberId;

        MccFixtureIsolation.IsolateCobPendMembers([primary, subrogation], seed: 42);

        Assert.Equal(originalPrimaryMemberId, primary.Member.MemberId);
        Assert.Equal(originalSubrogationMemberId, subrogation.Member.MemberId);
    }

    [Fact]
    public void IsolateValidationMembers_GivesScoreableNonCobScenariosRunScopedMemberIds()
    {
        var generator = new EdgeCaseClaimGenerator(new InMemoryReferenceDataProvider());
        var retroAdd = generator.Generate(1, nameof(EdgeCaseScenario.RetroEligibilityAdd), new Random(42));
        retroAdd.ClaimId = "MCC-E-0000013";
        retroAdd.Member.MemberId = "MBR-SHARED";
        retroAdd.Member.SubscriberId = "SUB-SHARED";

        var runId = Guid.Parse("906f49d1-18cc-4d19-9c77-69822ad6d88b");

        MccFixtureIsolation.IsolateValidationMembers([retroAdd], seed: 42, runId);

        Assert.Equal("MCCV906F49D1E0000013", retroAdd.Member.MemberId);
        Assert.Equal(retroAdd.Member.MemberId, retroAdd.Member.SubscriberId);
        Assert.DoesNotContain("-", retroAdd.Member.MemberId, StringComparison.Ordinal);
    }

    [Fact]
    public void IsolateValidationMembers_PreservesClaimTypeToPreventCrossCorpusCollisions()
    {
        var professional = new SyntheticClaim
        {
            ClaimId = "MCC-P-0000787",
            ClaimType = "Professional",
            BenefitPlanId = MccWorkflowValidation.CleanProfessionalPaidPlanId,
            PlaceOfService = "11",
            PriorAuthStatus = "NotRequired",
            Member = new SyntheticMember { MemberId = "SHARED", SubscriberId = "SHARED" }
        };
        var generator = new EdgeCaseClaimGenerator(new InMemoryReferenceDataProvider());
        var edge = generator.Generate(787, nameof(EdgeCaseScenario.RetroEligibilityAdd), new Random(42));
        edge.ClaimId = "MCC-E-0000787";
        edge.Member.MemberId = "SHARED";
        edge.Member.SubscriberId = "SHARED";
        var runId = Guid.Parse("906f49d1-18cc-4d19-9c77-69822ad6d88b");

        MccFixtureIsolation.IsolateValidationMembers([professional, edge], seed: 42, runId);

        Assert.Equal("MCCV906F49D1P0000787", professional.Member.MemberId);
        Assert.Equal("MCCV906F49D1E0000787", edge.Member.MemberId);
        Assert.NotEqual(professional.Member.MemberId, edge.Member.MemberId);
    }

    [Fact]
    public void IsolateValidationMembers_IsolatesFormerlyUnsupportedSubrogationScenario()
    {
        // SubrogationWorkersComp was unsupported (and thus skipped by isolation,
        // since ExpectedOutcome was null) before it was converted to a real scored
        // Pend. Now that it's scoreable, it must be isolated like any other scored
        // edge case -- confirms the isolation pass keys off ExpectedOutcome, not a
        // scenario allowlist that would otherwise need updating by hand.
        var generator = new EdgeCaseClaimGenerator(new InMemoryReferenceDataProvider());
        var subrogation = generator.Generate(2, nameof(EdgeCaseScenario.SubrogationWorkersComp), new Random(43));
        var runId = Guid.Parse("906f49d1-18cc-4d19-9c77-69822ad6d88b");

        MccFixtureIsolation.IsolateValidationMembers([subrogation], seed: 42, runId);

        Assert.Equal("MCCV906F49D1E0000002", subrogation.Member.MemberId);
        Assert.Equal(subrogation.Member.MemberId, subrogation.Member.SubscriberId);
    }
}
