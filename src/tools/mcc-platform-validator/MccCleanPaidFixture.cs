using CloudHealthOffice.BenchmarkClaimGenerator.Models;

namespace CloudHealthOffice.Tools.MccPlatformValidator;

internal static class MccCleanPaidFixture
{
    public static void NormalizeClaims(IEnumerable<SyntheticClaim> claims)
    {
        foreach (var claim in claims.Where(claim =>
                     string.Equals(
                         claim.BenefitPlanId,
                         MccWorkflowValidation.CleanProfessionalPaidPlanId,
                         StringComparison.Ordinal)))
        {
            NormalizeMember(claim.Member, claim.DateOfService);
        }
    }

    public static void NormalizeMember(SyntheticMember member, DateTime serviceDate)
    {
        member.CoverageEffectiveDate = serviceDate.Date.AddYears(-1);
        member.CoverageTermDate = null;
        member.EnrollmentStatus = "Active";
        member.MaintenanceTypeCode = "021";

        foreach (var coverage in member.Coverages)
        {
            coverage.EffectiveDate = member.CoverageEffectiveDate;
            coverage.TermDate = null;
            coverage.Status = "Active";
        }
    }
}
