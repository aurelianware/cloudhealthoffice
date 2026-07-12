using CloudHealthOffice.BenchmarkClaimGenerator.Models;
using CloudHealthOffice.Tools.MccPlatformValidator;

namespace CloudHealthOffice.MccPlatformValidator.Tests;

public class MccCleanPaidFixtureTests
{
    [Fact]
    public void NormalizeClaims_RepairsCleanFixtureAfterEarlierStateIsOverwritten()
    {
        var member = TerminatedMember();
        var clean = new SyntheticClaim
        {
            BenefitPlanId = MccWorkflowValidation.CleanProfessionalPaidPlanId,
            DateOfService = new DateTime(2026, 6, 15),
            Member = member
        };

        MccCleanPaidFixture.NormalizeClaims([clean]);

        AssertActiveForServiceDate(member, clean.DateOfService);
    }

    [Fact]
    public void NormalizeMember_RemovesTerminationAndMakesCoverageActiveForServiceDate()
    {
        var member = TerminatedMember();

        MccCleanPaidFixture.NormalizeMember(member, new DateTime(2026, 6, 15));

        AssertActiveForServiceDate(member, new DateTime(2026, 6, 15));
    }

    private static SyntheticMember TerminatedMember() => new()
    {
        CoverageEffectiveDate = new DateTime(2020, 1, 1),
        CoverageTermDate = new DateTime(2025, 1, 1),
        EnrollmentStatus = "Terminated",
        MaintenanceTypeCode = "024",
        Coverages =
        [
            new SyntheticCoverage
            {
                EffectiveDate = new DateTime(2020, 1, 1),
                TermDate = new DateTime(2025, 1, 1),
                Status = "Terminated"
            }
        ]
    };

    private static void AssertActiveForServiceDate(SyntheticMember member, DateTime serviceDate)
    {
        Assert.Equal(serviceDate.Date.AddYears(-1), member.CoverageEffectiveDate);
        Assert.Null(member.CoverageTermDate);
        Assert.Equal("Active", member.EnrollmentStatus);
        Assert.Equal("021", member.MaintenanceTypeCode);
        Assert.All(member.Coverages, coverage =>
        {
            Assert.Equal(member.CoverageEffectiveDate, coverage.EffectiveDate);
            Assert.Null(coverage.TermDate);
            Assert.Equal("Active", coverage.Status);
        });
    }
}
