using CloudHealthOffice.EncounterEngine.Domain;
using CloudHealthOffice.EncounterEngine.Services;
using Xunit;

namespace CloudHealthOffice.EncounterEngine.Tests;

public class EncounterTransformerTests
{
    private static EncounterTransformer Make() => new();

    // ── Helpers ───────────────────────────────────────────────────────────

    private static EncounterInput SimpleInput(
        ClaimFormType form = ClaimFormType.Professional,
        ClaimFrequencyCode freq = ClaimFrequencyCode.Original) =>
        new()
        {
            ClaimId          = "CLM001",
            TenantId         = "TENANT1",
            FormType         = form,
            FrequencyCode    = freq,
            ServiceDate      = new DateOnly(2026, 1, 15),
            PlaceOfService   = "11",
            MemberId         = "MEM001",
            SubscriberId     = "SUB001",
            MemberFirstName  = "Jane",
            MemberLastName   = "Doe",
            MemberDateOfBirth = new DateOnly(1980, 6, 15),
            MemberGender     = "F",
            BillingNpi       = "1234567890",
            BillingProviderName = "ACME MEDICAL GROUP",
            BillingTaxId     = "123456789",
            PlanSubmitterId  = "PLAN001",
            ReceiverSubmitterId = "CMS001",
            PlanName         = "Test Health Plan",
            PlanPayerId      = "88888",
            DiagnosisCodes   = ["Z00.00", "I10"],
            Lines            =
            [
                new EncounterLineInput
                {
                    LineNumber      = 1,
                    ProcedureCode   = "99213",
                    BilledAmount    = 200m,
                    AllowedAmount   = 160m,
                    PlanPaidAmount  = 128m,
                    MemberResponsibility = 32m,
                    DeductibleAmount    = 0m,
                    CopayAmount         = 30m,
                    CoinsuranceAmount   = 2m,
                    Units           = 1,
                    DiagnosisPointers = ["1", "2"]
                }
            ]
        };

    private static List<string> Segments(string rawX12) =>
        rawX12.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static string? FindSegment(string rawX12, string prefix) =>
        Segments(rawX12).FirstOrDefault(s => s.StartsWith(prefix + "*") || s == prefix);

    private static List<string> AllSegments(string rawX12, string prefix) =>
        Segments(rawX12).Where(s => s.StartsWith(prefix + "*") || s == prefix).ToList();

    // ── Transaction structure ─────────────────────────────────────────────

    [Fact]
    public void Transform_Professional_StartsWithST_837()
    {
        var result = Make().Transform(SimpleInput());
        var segs = Segments(result.RawX12);
        Assert.StartsWith("ST*837*", segs[0]);
        Assert.Contains("005010X222A2", segs[0]);
    }

    [Fact]
    public void Transform_Institutional_HasInstitutionalImplementationId()
    {
        var input = SimpleInput(ClaimFormType.Institutional) with
        {
            AdmitDate = new DateOnly(2026, 1, 14),
            DischargeDate = new DateOnly(2026, 1, 15),
            Lines =
            [
                new EncounterLineInput
                {
                    LineNumber = 1, ProcedureCode = "0360", RevenueCode = "0360",
                    BilledAmount = 5000m, AllowedAmount = 4000m,
                    PlanPaidAmount = 3600m, MemberResponsibility = 400m,
                    Units = 1
                }
            ]
        };
        var result = Make().Transform(input);
        Assert.Contains("005010X223A3", result.RawX12);
    }

    [Fact]
    public void Transform_SeCountMatchesActualSegments()
    {
        var result = Make().Transform(SimpleInput());
        var segs = Segments(result.RawX12);
        var seSeg = segs.First(s => s.StartsWith("SE*"));
        var parts = seSeg.Split('*');
        var declaredCount = int.Parse(parts[1]);
        Assert.Equal(declaredCount, segs.Count);
    }

    [Fact]
    public void Transform_EndsWithSE()
    {
        var result = Make().Transform(SimpleInput());
        var segs = Segments(result.RawX12);
        Assert.StartsWith("SE*", segs[^1]);
    }

    // ── Encounter metadata ────────────────────────────────────────────────

    [Fact]
    public void Transform_ResultHasCorrectClaimId()
    {
        var result = Make().Transform(SimpleInput());
        Assert.Equal("CLM001", result.ClaimId);
    }

    [Fact]
    public void Transform_ResultTotalsMatchLines()
    {
        var result = Make().Transform(SimpleInput());
        Assert.Equal(200m, result.TotalBilled);
        Assert.Equal(128m, result.TotalPlanPaid);
        Assert.Equal(32m,  result.TotalMemberResponsibility);
    }

    [Fact]
    public void Transform_StatusIsPending()
    {
        var result = Make().Transform(SimpleInput());
        Assert.Equal(EncounterStatus.Pending, result.Status);
    }

    // ── Key segments ─────────────────────────────────────────────────────

    [Fact]
    public void Transform_BhtContainsControlNumber()
    {
        var result = Make().Transform(SimpleInput());
        var bht = FindSegment(result.RawX12, "BHT");
        Assert.NotNull(bht);
        Assert.Contains(result.EncounterControlNumber, bht);
    }

    [Fact]
    public void Transform_BhtPurposeCode_Original_Is00()
    {
        var result = Make().Transform(SimpleInput(freq: ClaimFrequencyCode.Original));
        var bht = FindSegment(result.RawX12, "BHT")!;
        Assert.Contains("*00*", bht); // BHT02 = 00
    }

    [Fact]
    public void Transform_BhtPurposeCode_Corrected_Is18()
    {
        var input = SimpleInput(freq: ClaimFrequencyCode.Corrected) with
        {
            OriginalEncounterControlNumber = "000000001"
        };
        var result = Make().Transform(input);
        var bht = FindSegment(result.RawX12, "BHT")!;
        Assert.Contains("*18*", bht);
    }

    [Fact]
    public void Transform_Corrected_IncludesRefF8Segment()
    {
        var input = SimpleInput(freq: ClaimFrequencyCode.Corrected) with
        {
            OriginalEncounterControlNumber = "000000001"
        };
        var result = Make().Transform(input);
        var refF8 = FindSegment(result.RawX12, "REF*F8");
        Assert.NotNull(refF8);
        Assert.Contains("000000001", refF8);
    }

    [Fact]
    public void Transform_Original_NoRefF8Segment()
    {
        var result = Make().Transform(SimpleInput());
        Assert.DoesNotContain("REF*F8", result.RawX12);
    }

    [Fact]
    public void Transform_DiagnosisCodesInHiSegment()
    {
        var result = Make().Transform(SimpleInput());
        var hi = FindSegment(result.RawX12, "HI");
        Assert.NotNull(hi);
        Assert.Contains("ABK:Z00.00", hi); // principal
        Assert.Contains("ABF:I10", hi);    // additional
    }

    [Fact]
    public void Transform_MemberInfoInNm1_IL()
    {
        var result = Make().Transform(SimpleInput());
        var nm1 = AllSegments(result.RawX12, "NM1")
            .FirstOrDefault(s => s.Contains("*IL*"));
        Assert.NotNull(nm1);
        Assert.Contains("Doe", nm1);
        Assert.Contains("Jane", nm1);
        Assert.Contains("MEM001", nm1);
    }

    [Fact]
    public void Transform_PlanPaidAmtInClaimLevelAmtAU()
    {
        var result = Make().Transform(SimpleInput());
        var amtAU = AllSegments(result.RawX12, "AMT")
            .FirstOrDefault(s => s.StartsWith("AMT*AU*"));
        Assert.NotNull(amtAU);
        Assert.Contains("128.00", amtAU);
    }

    [Fact]
    public void Transform_ServiceLine_SV1_Present_Professional()
    {
        var result = Make().Transform(SimpleInput());
        var sv1 = FindSegment(result.RawX12, "SV1");
        Assert.NotNull(sv1);
        Assert.Contains("99213", sv1);
        Assert.Contains("200.00", sv1);
    }

    [Fact]
    public void Transform_ServiceLine_SV2_Present_Institutional()
    {
        var input = SimpleInput(ClaimFormType.Institutional) with
        {
            AdmitDate = new DateOnly(2026, 1, 14),
            DischargeDate = new DateOnly(2026, 1, 15),
            Lines =
            [
                new EncounterLineInput
                {
                    LineNumber = 1, ProcedureCode = "0360", RevenueCode = "0360",
                    BilledAmount = 5000m, AllowedAmount = 4000m,
                    PlanPaidAmount = 3600m, MemberResponsibility = 400m,
                    Units = 1
                }
            ]
        };
        var result = Make().Transform(input);
        var sv2 = FindSegment(result.RawX12, "SV2");
        Assert.NotNull(sv2);
        Assert.Contains("0360", sv2);
    }

    [Fact]
    public void Transform_CopayAmtSegment_Present_WhenNonZero()
    {
        var result = Make().Transform(SimpleInput());
        var amtF4 = AllSegments(result.RawX12, "AMT")
            .FirstOrDefault(s => s.StartsWith("AMT*F4*"));
        Assert.NotNull(amtF4);
        Assert.Contains("30.00", amtF4);
    }

    [Fact]
    public void Transform_DeductibleAmtSegment_Absent_WhenZero()
    {
        // SimpleInput has DeductibleAmount = 0
        var result = Make().Transform(SimpleInput());
        var amtA8 = AllSegments(result.RawX12, "AMT")
            .FirstOrDefault(s => s.StartsWith("AMT*A8*"));
        Assert.Null(amtA8);
    }

    // ── COB segments ──────────────────────────────────────────────────────

    [Fact]
    public void Transform_NoCob_NoOiSegment()
    {
        var result = Make().Transform(SimpleInput());
        Assert.DoesNotContain("OI*", result.RawX12);
    }

    [Fact]
    public void Transform_WithCob_OiSegmentPresent()
    {
        var input = SimpleInput() with
        {
            Cob = new EncounterCobContext
            {
                OtherPayerName       = "Primary Payer Inc",
                OtherPayerId         = "PRIMARY01",
                OtherPayerPaidAmount = 100m,
                PayerResponsibilityCode = "P"
            }
        };
        var result = Make().Transform(input);
        Assert.Contains("OI*", result.RawX12);
    }

    [Fact]
    public void Transform_WithCob_OtherPayerNm1TT_Present()
    {
        var input = SimpleInput() with
        {
            Cob = new EncounterCobContext
            {
                OtherPayerName       = "Primary Payer Inc",
                OtherPayerId         = "PRIMARY01",
                OtherPayerPaidAmount = 100m
            }
        };
        var result = Make().Transform(input);
        var nm1TT = AllSegments(result.RawX12, "NM1")
            .FirstOrDefault(s => s.Contains("*TT*"));
        Assert.NotNull(nm1TT);
        Assert.Contains("PRIMARY01", nm1TT);
    }

    [Fact]
    public void Transform_WithCob_PrimaryPaymentAmt_Present()
    {
        var input = SimpleInput() with
        {
            Cob = new EncounterCobContext
            {
                OtherPayerName       = "Primary Payer Inc",
                OtherPayerId         = "PRIMARY01",
                OtherPayerPaidAmount = 100m
            }
        };
        var result = Make().Transform(input);
        var amtD = AllSegments(result.RawX12, "AMT")
            .FirstOrDefault(s => s.StartsWith("AMT*D*"));
        Assert.NotNull(amtD);
        Assert.Contains("100.00", amtD);
    }

    // ── Rendering provider ─────────────────────────────────────────────────

    [Fact]
    public void Transform_RenderingProviderDifferentFromBilling_NM1_82_Present()
    {
        var input = SimpleInput() with
        {
            RenderingNpi = "9876543210",
            RenderingProviderLastName  = "Smith",
            RenderingProviderFirstName = "John"
        };
        var result = Make().Transform(input);
        var nm182 = AllSegments(result.RawX12, "NM1")
            .FirstOrDefault(s => s.Contains("*82*"));
        Assert.NotNull(nm182);
        Assert.Contains("9876543210", nm182);
    }

    [Fact]
    public void Transform_RenderingNpiSameAsBilling_NoNM1_82()
    {
        // When rendering == billing, don't emit the 2310B loop
        var input = SimpleInput() with
        {
            RenderingNpi = "1234567890" // same as BillingNpi
        };
        var result = Make().Transform(input);
        var nm182 = AllSegments(result.RawX12, "NM1")
            .FirstOrDefault(s => s.Contains("*82*"));
        Assert.Null(nm182);
    }

    // ── Institutional admit/discharge dates ────────────────────────────────

    [Fact]
    public void Transform_Institutional_AdmitAndDischargeDates_Present()
    {
        var input = SimpleInput(ClaimFormType.Institutional) with
        {
            AdmitDate     = new DateOnly(2026, 1, 10),
            DischargeDate = new DateOnly(2026, 1, 15),
            Lines =
            [
                new EncounterLineInput
                {
                    LineNumber = 1, ProcedureCode = "0360", RevenueCode = "0360",
                    BilledAmount = 5000m, AllowedAmount = 4000m,
                    PlanPaidAmount = 3600m, MemberResponsibility = 400m,
                    Units = 1
                }
            ]
        };
        var result = Make().Transform(input);
        Assert.Contains("DTP*435*D8*20260110", result.RawX12); // admit
        Assert.Contains("DTP*096*D8*20260115", result.RawX12); // discharge
    }

    // ── Control number uniqueness ──────────────────────────────────────────

    [Fact]
    public void GenerateControlNumber_DifferentClaimIds_ProduceDifferentNumbers()
    {
        var ecn1 = EncounterTransformer.GenerateControlNumber("CLM001");
        var ecn2 = EncounterTransformer.GenerateControlNumber("CLM999");
        Assert.NotEqual(ecn1, ecn2);
    }

    [Fact]
    public void GenerateControlNumber_SameClaimId_ProducesSameNumber()
    {
        var ecn1 = EncounterTransformer.GenerateControlNumber("CLM001");
        var ecn2 = EncounterTransformer.GenerateControlNumber("CLM001");
        Assert.Equal(ecn1, ecn2);
    }
}
