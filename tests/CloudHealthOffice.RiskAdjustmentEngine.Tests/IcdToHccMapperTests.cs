using CloudHealthOffice.RiskAdjustmentEngine.Domain;
using CloudHealthOffice.RiskAdjustmentEngine.Services;
using Xunit;

namespace CloudHealthOffice.RiskAdjustmentEngine.Tests;

public class IcdToHccMapperTests
{
    private static IcdToHccMapper Make() => new();

    // ── Known mappings ────────────────────────────────────────────────────

    [Theory]
    [InlineData("E119",  19)]   // T2D without complication
    [InlineData("E109",  19)]   // T1D without complication
    [InlineData("E1140", 18)]   // T2D with CKD → DM with chronic complications
    [InlineData("E1040", 18)]   // T1D with CKD
    [InlineData("E1110", 17)]   // T2D with ketoacidosis → DM with acute complications
    [InlineData("I501",  85)]   // Left ventricular failure → CHF
    [InlineData("I509",  86)]   // CHF unspecified
    [InlineData("J440",  110)]  // COPD
    [InlineData("J4520", 111)]  // Asthma, mild intermittent
    [InlineData("N185",  136)]  // CKD stage 5
    [InlineData("N184",  137)]  // CKD stage 4
    [InlineData("N1831", 138)]  // CKD stage 3a
    [InlineData("B20",   1)]    // HIV/AIDS
    [InlineData("C7800", 8)]    // Metastatic cancer
    public void Map_KnownCode_ReturnsExpectedHcc(string icd10, int expectedHcc)
    {
        var result = Make().Map(icd10, HccModel.CmsHccV28);
        Assert.Equal(expectedHcc, result);
    }

    [Fact]
    public void Map_UnknownCode_ReturnsNull()
    {
        var result = Make().Map("Z00.00", HccModel.CmsHccV28);
        Assert.Null(result);
    }

    [Fact]
    public void Map_CodeWithDot_NormalizedAndMapped()
    {
        // "E11.9" with dot should map the same as "E119"
        var result = Make().Map("E11.9", HccModel.CmsHccV28);
        Assert.Equal(19, result);
    }

    [Fact]
    public void Map_LowercaseCode_NormalizedAndMapped()
    {
        var result = Make().Map("e119", HccModel.CmsHccV28);
        Assert.Equal(19, result);
    }

    [Fact]
    public void Map_HhsModel_UsesHhsCrosswalk()
    {
        // J440 maps to HHS HCC 161 (not CMS-HCC 110)
        var result = Make().Map("J440", HccModel.HhsHcc);
        Assert.Equal(161, result);
    }

    [Fact]
    public void Map_CmsCodeInHhsModel_ReturnsNull_IfNotInHhsCrosswalk()
    {
        // B20 (HIV) is in CMS crosswalk but not HHS subset embedded here
        var result = Make().Map("B20", HccModel.HhsHcc);
        Assert.Null(result);
    }

    // ── MapAll ────────────────────────────────────────────────────────────

    [Fact]
    public void MapAll_MixedCodes_ReturnsMappedAndNull()
    {
        var codes = new[] { "E119", "Z00.00", "I501" };
        var result = Make().MapAll(codes, HccModel.CmsHccV28);

        Assert.Equal(3, result.Count);
        Assert.Equal(19, result["E119"]);
        Assert.Null(result["Z00.00"]);
        Assert.Equal(85, result["I501"]);
    }

    [Fact]
    public void MapAll_DuplicateCodes_DeduplicatedInResult()
    {
        var codes = new[] { "E119", "E119", "E119" };
        var result = Make().MapAll(codes, HccModel.CmsHccV28);
        Assert.Single(result);
    }

    [Fact]
    public void MapAll_EmptyList_ReturnsEmptyDictionary()
    {
        var result = Make().MapAll([], HccModel.CmsHccV28);
        Assert.Empty(result);
    }
}
