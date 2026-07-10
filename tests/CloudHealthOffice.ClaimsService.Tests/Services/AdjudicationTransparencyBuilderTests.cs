using ClaimsService.Models;
using ClaimsService.Services;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services;

public sealed class AdjudicationTransparencyBuilderTests
{
    [Fact]
    public void Build_ForDeniedClaim_ProjectsDispositionBenefitAndLinePricing()
    {
        var claim = CreateClaim();
        claim.Status = ClaimStatus.Denied;
        claim.AdjudicationResult = new AdjudicationResult
        {
            NetworkTier = "InNetwork",
            AllowedAmount = 0m,
            DeductibleAmount = 0m,
            CoinsuranceAmount = 0m,
            CopayAmount = 0m,
            PatientResponsibility = 0m,
            PayerPayment = 0m,
            DenialReasonCode = "96",
            DenialReason = "Non-covered charge"
        };
        claim.ClaimLines[0].AdjudicationResult = new LineAdjudicationResult
        {
            AllowedAmount = 0m,
            PaidAmount = 0m,
            PatientResponsibility = 0m
        };

        var result = AdjudicationTransparencyBuilder.Build(claim);

        Assert.NotNull(result);
        Assert.Contains(result!.Steps, s => s.StepName == "Disposition" && s.Status == "Passed" && s.Summary!.Contains("CARC 96"));
        Assert.Single(result.FeeScheduleResults);
        Assert.Equal("99213", result.FeeScheduleResults[0].ProcedureCode);
        Assert.NotNull(result.BenefitCalculation);
        Assert.Equal("CARC 96", result.BenefitCalculation!.BenefitRuleApplied);
    }

    [Fact]
    public void Build_ForPendedClaim_ProjectsPendStepAndNcciFailures()
    {
        var claim = CreateClaim();
        claim.Status = ClaimStatus.Pended;
        claim.PendDetails = new PendDetails
        {
            PendCode = "NCCI",
            PendReason = "NCCI edit requires review",
            PendedAt = new DateTime(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc),
            EditFailures = new List<NcciEditFailureSnapshot>
            {
                new()
                {
                    RuleId = "NE001",
                    EditType = "NcciPair",
                    Message = "Mutually exclusive procedures",
                    Column1Code = "99213",
                    Column2Code = "99214",
                    SuggestedCarc = "151"
                }
            }
        };

        var result = AdjudicationTransparencyBuilder.Build(claim);

        Assert.NotNull(result);
        Assert.Contains(result!.Steps, s => s.StepName == "Pend Review" && s.Status == "Warning");
        var ncci = Assert.Single(result.NcciResults);
        Assert.False(ncci.Passed);
        Assert.Equal("NE001", ncci.EditCode);
        Assert.Equal("99214", ncci.AffectedProcedureCode);
    }

    [Fact]
    public void Build_ForClaimWithoutPersistedProjection_ReturnsNull()
    {
        var claim = CreateClaim();

        var result = AdjudicationTransparencyBuilder.Build(claim);

        Assert.Null(result);
    }

    private static Claim CreateClaim()
    {
        var serviceDate = new DateTime(2026, 7, 9, 0, 0, 0, DateTimeKind.Utc);
        return new Claim
        {
            Id = "claim-1",
            ClaimNumber = "MCC-P-0000001",
            TenantId = "test-tenant",
            MemberId = "MEM-001",
            BillingProviderNPI = "1234567890",
            ClaimType = ClaimType.Professional,
            Status = ClaimStatus.Submitted,
            ReceivedDate = serviceDate,
            LastUpdatedDate = serviceDate.AddMinutes(1),
            ServiceDateFrom = serviceDate,
            ServiceDateTo = serviceDate,
            ClaimLines = new List<ClaimLine>
            {
                new()
                {
                    LineNumber = 1,
                    ProcedureCode = "99213",
                    ChargeAmount = 150m,
                    Units = 1,
                    ServiceDateFrom = serviceDate,
                    ServiceDateTo = serviceDate
                }
            }
        };
    }
}
