using CloudHealthOffice.RiskAdjustmentEngine.Domain;
using CloudHealthOffice.RiskAdjustmentEngine.Services;
using Xunit;

namespace CloudHealthOffice.RiskAdjustmentEngine.Tests;

public class RiskAdjustmentEngineTests
{
    private static RiskAdjustmentEngine.Services.RiskAdjustmentEngine Make() =>
        new(new IcdToHccMapper(), new HccHierarchyResolver(), new RiskScoreCalculator());

    private static RiskScoreInput BaseInput(
        string memberId = "M1",
        int age = 70,
        MemberGender gender = MemberGender.Female,
        params string[] codes) =>
        new()
        {
            MemberId           = memberId,
            SubscriberId       = "S1",
            Model              = HccModel.CmsHccV28,
            Segment            = EnrollmentSegment.CommunityNonDual,
            AgeAsOfPaymentYear = age,
            Gender             = gender,
            DiagnosisCodes     = [.. codes]
        };

    // ── Pipeline smoke tests ──────────────────────────────────────────────

    [Fact]
    public void NoDiagnoses_ScoreEqualsDemographicFactorOnly()
    {
        var result = Make().ComputeRiskScore(BaseInput("M1", 70, MemberGender.Female));

        Assert.Equal(0m, result.TotalHccFactor);
        Assert.Equal(result.DemographicFactor, result.FinalRiskScore);
        Assert.Empty(result.HccContributions);
        Assert.Empty(result.SuppressedHccs);
    }

    [Fact]
    public void SingleHcc_ScoreIsDemo_PlusHccFactor()
    {
        // E11.9 → HCC 19 (DM without complication, factor 0.136)
        var result = Make().ComputeRiskScore(BaseInput("M1", 70, MemberGender.Female, "E119"));

        Assert.Single(result.HccContributions);
        Assert.Equal(19, result.HccContributions[0].CategoryCode);
        Assert.Equal(0.136m, result.TotalHccFactor);
        Assert.Equal(result.DemographicFactor + 0.136m, result.FinalRiskScore);
    }

    [Fact]
    public void TwoUnrelatedHccs_BothFactorsSummed()
    {
        // DM no-complication (HCC 19, 0.136) + COPD (HCC 110, 0.332)
        var result = Make().ComputeRiskScore(BaseInput("M1", 65, MemberGender.Male, "E119", "J440"));

        Assert.Equal(2, result.HccContributions.Count);
        Assert.Equal(0.136m + 0.332m, result.TotalHccFactor);
    }

    [Fact]
    public void HierarchyApplied_DominantHccOnly_CountsTowardScore()
    {
        // E11.40 → HCC 18 (DM chronic), E11.9 → HCC 19 (DM no comp)
        // Hierarchy: 18 > 19 → only HCC 18 should contribute
        var result = Make().ComputeRiskScore(BaseInput("M1", 70, MemberGender.Female, "E1140", "E119"));

        Assert.Single(result.HccContributions);
        Assert.Equal(18, result.HccContributions[0].CategoryCode);
        Assert.Equal(0.263m, result.TotalHccFactor);
        Assert.Contains(19, result.SuppressedHccs);
    }

    [Fact]
    public void UnmappedDiagnosis_NotInHccContributions()
    {
        // Z00.00 is a routine exam code — no HCC mapping
        var result = Make().ComputeRiskScore(BaseInput("M1", 70, MemberGender.Female, "Z00.00"));

        Assert.Empty(result.HccContributions);
        Assert.Null(result.DiagnosisToHccMap["Z00.00"]);
    }

    [Fact]
    public void DiagnosisToHccMap_ContainsAllInputCodes()
    {
        var result = Make().ComputeRiskScore(BaseInput("M1", 70, MemberGender.Female, "E119", "Z00.00"));

        Assert.True(result.DiagnosisToHccMap.ContainsKey("E119"));
        Assert.True(result.DiagnosisToHccMap.ContainsKey("Z00.00"));
    }

    [Fact]
    public void SourceDiagnosisCodes_TrackedPerHcc()
    {
        // Two T2D codes both map to HCC 19
        var result = Make().ComputeRiskScore(BaseInput("M1", 70, MemberGender.Female, "E119", "E139"));

        var hcc19 = result.HccContributions.FirstOrDefault(c => c.CategoryCode == 19);
        Assert.NotNull(hcc19);
        Assert.Contains("E119", hcc19.SourceDiagnosisCodes);
        Assert.Contains("E139", hcc19.SourceDiagnosisCodes);
    }

    // ── Demographic factor correctness ────────────────────────────────────

    public static TheoryData<int, MemberGender, decimal> DemographicFactorCases => new()
    {
        { 65, MemberGender.Male,   0.453m },
        { 70, MemberGender.Male,   0.534m },
        { 75, MemberGender.Male,   0.618m },
        { 65, MemberGender.Female, 0.417m },
        { 70, MemberGender.Female, 0.486m },
        { 80, MemberGender.Female, 0.648m },
    };

    [Theory]
    [MemberData(nameof(DemographicFactorCases))]
    public void DemographicFactor_CorrectForAgeSex(int age, MemberGender gender, decimal expectedFactor)
    {
        var result = Make().ComputeRiskScore(BaseInput("M1", age, gender));
        Assert.Equal(expectedFactor, result.DemographicFactor);
    }

    // ── Batch processing ──────────────────────────────────────────────────

    [Fact]
    public void ComputeRiskScores_Batch_ReturnsOneResultPerInput()
    {
        var inputs = new[]
        {
            BaseInput("M1", 65, MemberGender.Male,   "E119"),
            BaseInput("M2", 70, MemberGender.Female, "I501"),
            BaseInput("M3", 75, MemberGender.Male)
        };

        var results = Make().ComputeRiskScores(inputs);

        Assert.Equal(3, results.Count);
        Assert.Equal("M1", results[0].MemberId);
        Assert.Equal("M2", results[1].MemberId);
        Assert.Equal("M3", results[2].MemberId);
    }

    [Fact]
    public void ComputeRiskScores_EmptyBatch_ReturnsEmptyList()
    {
        var results = Make().ComputeRiskScores([]);
        Assert.Empty(results);
    }

    // ── Complex scenario: multiple comorbidities + hierarchy ──────────────

    [Fact]
    public void ComplexMember_MultipleComorbidities_CorrectScore()
    {
        // Member has:
        //   DM with CKD (E11.40 → HCC 18) — chronic complications
        //   DM no comp  (E11.9  → HCC 19) — suppressed by HCC 18
        //   CHF         (I50.1  → HCC 85)
        //   CKD stage 5 (N18.5  → HCC 136)
        // Expected HCCs after hierarchy: 18, 85, 136
        // Expected HCC factor: 0.263 + 0.323 + 0.289 = 0.875

        var result = Make().ComputeRiskScore(
            BaseInput("M1", 72, MemberGender.Female, "E1140", "E119", "I501", "N185"));

        var hccCodes = result.HccContributions.Select(c => c.CategoryCode).OrderBy(x => x).ToList();
        Assert.Equal([18, 85, 136], hccCodes);
        Assert.Contains(19, result.SuppressedHccs);
        Assert.Equal(0.263m + 0.323m + 0.289m, result.TotalHccFactor);
        Assert.Equal(result.DemographicFactor + result.TotalHccFactor, result.FinalRiskScore);
    }

    // ── MemberId and model echoed in result ───────────────────────────────

    [Fact]
    public void Result_EchosMemberIdAndModel()
    {
        var result = Make().ComputeRiskScore(BaseInput("MEMBER-42", 68, MemberGender.Male));

        Assert.Equal("MEMBER-42", result.MemberId);
        Assert.Equal(HccModel.CmsHccV28, result.Model);
        Assert.Equal(EnrollmentSegment.CommunityNonDual, result.Segment);
    }
}
