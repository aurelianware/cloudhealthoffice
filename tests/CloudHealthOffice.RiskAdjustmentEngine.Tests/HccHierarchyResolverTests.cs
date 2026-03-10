using CloudHealthOffice.RiskAdjustmentEngine.Domain;
using CloudHealthOffice.RiskAdjustmentEngine.Services;
using Xunit;

namespace CloudHealthOffice.RiskAdjustmentEngine.Tests;

public class HccHierarchyResolverTests
{
    private static HccHierarchyResolver Make() => new();

    // ── Diabetes hierarchy: HCC 17 > 18 > 19 ─────────────────────────────

    [Fact]
    public void Diabetes_17Dominant_Suppresses18And19()
    {
        var result = Make().Resolve(new HashSet<int> { 17, 18, 19 }, HccModel.CmsHccV28);

        Assert.Contains(17, result.RemainingHccs);
        Assert.DoesNotContain(18, result.RemainingHccs);
        Assert.DoesNotContain(19, result.RemainingHccs);
        Assert.Contains(18, result.SuppressedHccs);
        Assert.Contains(19, result.SuppressedHccs);
    }

    [Fact]
    public void Diabetes_18Dominant_Suppresses19_Not17()
    {
        // 17 not present → 18 dominates 19
        var result = Make().Resolve(new HashSet<int> { 18, 19 }, HccModel.CmsHccV28);

        Assert.Contains(18, result.RemainingHccs);
        Assert.DoesNotContain(19, result.RemainingHccs);
        Assert.Contains(19, result.SuppressedHccs);
    }

    [Fact]
    public void Diabetes_OnlyHcc19_NoSuppression()
    {
        var result = Make().Resolve(new HashSet<int> { 19 }, HccModel.CmsHccV28);

        Assert.Contains(19, result.RemainingHccs);
        Assert.Empty(result.SuppressedHccs);
    }

    // ── CHF hierarchy: HCC 85 > 86 ───────────────────────────────────────

    [Fact]
    public void Chf_85Dominant_Suppresses86()
    {
        var result = Make().Resolve(new HashSet<int> { 85, 86 }, HccModel.CmsHccV28);

        Assert.Contains(85, result.RemainingHccs);
        Assert.DoesNotContain(86, result.RemainingHccs);
        Assert.Contains(86, result.SuppressedHccs);
    }

    [Fact]
    public void Chf_Only86_NoSuppression()
    {
        var result = Make().Resolve(new HashSet<int> { 86 }, HccModel.CmsHccV28);

        Assert.Contains(86, result.RemainingHccs);
        Assert.Empty(result.SuppressedHccs);
    }

    // ── Cancer hierarchy: HCC 8 > 9 > 10 ─────────────────────────────────

    [Fact]
    public void Cancer_8Dominant_Suppresses9And10()
    {
        var result = Make().Resolve(new HashSet<int> { 8, 9, 10 }, HccModel.CmsHccV28);

        Assert.Contains(8, result.RemainingHccs);
        Assert.DoesNotContain(9, result.RemainingHccs);
        Assert.DoesNotContain(10, result.RemainingHccs);
    }

    // ── COPD hierarchy: HCC 110 > 111 ────────────────────────────────────

    [Fact]
    public void Copd_110Dominant_Suppresses111()
    {
        var result = Make().Resolve(new HashSet<int> { 110, 111 }, HccModel.CmsHccV28);

        Assert.Contains(110, result.RemainingHccs);
        Assert.DoesNotContain(111, result.RemainingHccs);
    }

    // ── CKD hierarchy: HCC 136 > 137 > 138 ───────────────────────────────

    [Fact]
    public void Ckd_136Dominant_Suppresses137And138()
    {
        var result = Make().Resolve(new HashSet<int> { 136, 137, 138 }, HccModel.CmsHccV28);

        Assert.Contains(136, result.RemainingHccs);
        Assert.DoesNotContain(137, result.RemainingHccs);
        Assert.DoesNotContain(138, result.RemainingHccs);
    }

    [Fact]
    public void Ckd_137Dominant_Suppresses138()
    {
        var result = Make().Resolve(new HashSet<int> { 137, 138 }, HccModel.CmsHccV28);

        Assert.Contains(137, result.RemainingHccs);
        Assert.DoesNotContain(138, result.RemainingHccs);
    }

    // ── Multiple independent disease groups ───────────────────────────────

    [Fact]
    public void MultipleGroups_HierarchyAppliedIndependently()
    {
        // DM (18 dominates 19) + CHF (85 dominates 86) — two independent hierarchies
        var result = Make().Resolve(new HashSet<int> { 18, 19, 85, 86 }, HccModel.CmsHccV28);

        Assert.Contains(18, result.RemainingHccs);
        Assert.DoesNotContain(19, result.RemainingHccs);
        Assert.Contains(85, result.RemainingHccs);
        Assert.DoesNotContain(86, result.RemainingHccs);
        Assert.Equal(2, result.SuppressedHccs.Count);
    }

    // ── No hierarchy applicable ───────────────────────────────────────────

    [Fact]
    public void UnrelatedHccs_NoneSupressed()
    {
        // HIV (1) + morbid obesity (22) — no shared hierarchy
        var result = Make().Resolve(new HashSet<int> { 1, 22 }, HccModel.CmsHccV28);

        Assert.Contains(1, result.RemainingHccs);
        Assert.Contains(22, result.RemainingHccs);
        Assert.Empty(result.SuppressedHccs);
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        var result = Make().Resolve(new HashSet<int>(), HccModel.CmsHccV28);

        Assert.Empty(result.RemainingHccs);
        Assert.Empty(result.SuppressedHccs);
    }

    // ── HHS model — no hierarchy embedded ────────────────────────────────

    [Fact]
    public void HhsModel_NoHierarchyRules_NothingSuppressed()
    {
        // HHS hierarchy not embedded → all HCCs survive
        var result = Make().Resolve(new HashSet<int> { 18, 19 }, HccModel.HhsHcc);

        Assert.Contains(18, result.RemainingHccs);
        Assert.Contains(19, result.RemainingHccs);
        Assert.Empty(result.SuppressedHccs);
    }
}
