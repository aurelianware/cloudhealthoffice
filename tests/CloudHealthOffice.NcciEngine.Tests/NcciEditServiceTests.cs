using CloudHealthOffice.NcciEngine.Domain;
using CloudHealthOffice.NcciEngine.Models;
using CloudHealthOffice.NcciEngine.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudHealthOffice.NcciEngine.Tests;

/// <summary>
/// Unit tests for NcciEditService.
///
/// NE001 — NCCI Column 1/Column 2 bundling edits.
/// NE002 — MUE maximum units edits.
///
/// Naming convention: Scenario_Condition_ExpectedOutcome
/// </summary>
public class NcciEditServiceTests
{
    // ═══════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════

    private const string Tenant = "test-tenant";
    private static readonly DateOnly Dos = new(2025, 3, 1);
    private static readonly DateTime EffDt = new(2025, 1, 1);

    private static NcciEditService BuildService(FakeNcciRepository repo, NcciLookupCache? cache = null)
        => new(repo, NullLogger<NcciEditService>.Instance, cache);

    private static NcciScrubRequest OneLineClaim(
        string code, decimal units = 1, List<string>? modifiers = null)
        => new()
        {
            TenantId = Tenant,
            ClaimId = "CLM-001",
            ClaimType = "837P",
            ServiceLines =
            [
                new ClaimServiceLine
                {
                    LineNumber = 1,
                    ProcedureCode = code,
                    Units = units,
                    Modifiers = modifiers ?? [],
                    ServiceDate = Dos,
                }
            ]
        };

    private static NcciScrubRequest TwoLineClaim(
        string code1, string code2,
        List<string>? modifiers1 = null, List<string>? modifiers2 = null,
        decimal units1 = 1, decimal units2 = 1,
        DateOnly? dos2 = null)
        => new()
        {
            TenantId = Tenant,
            ClaimId = "CLM-002",
            ClaimType = "837P",
            ServiceLines =
            [
                new ClaimServiceLine
                {
                    LineNumber = 1,
                    ProcedureCode = code1,
                    Units = units1,
                    Modifiers = modifiers1 ?? [],
                    ServiceDate = Dos,
                },
                new ClaimServiceLine
                {
                    LineNumber = 2,
                    ProcedureCode = code2,
                    Units = units2,
                    Modifiers = modifiers2 ?? [],
                    ServiceDate = dos2 ?? Dos,
                }
            ]
        };

    private static NcciEditPair MakePair(
        string col1, string col2,
        NcciModifierIndicator mi = NcciModifierIndicator.NotAllowed)
        => new()
        {
            Id = $"{Tenant}_{col1}_{col2}_20250101",
            TenantId = Tenant,
            Column1Code = col1,
            Column2Code = col2,
            ModifierIndicator = mi,
            PolicyType = NcciPolicyType.ProcedureToProc,
            EffectiveDate = EffDt,
        };

    private static MueEntry MakeMue(
        string code, int maxUnits,
        MueAdjudicationIndicator mai = MueAdjudicationIndicator.ClaimLine,
        bool professional = true, bool facility = true)
        => new()
        {
            Id = $"{Tenant}_{code}_20250101",
            TenantId = Tenant,
            ProcedureCode = code,
            MaxUnits = maxUnits,
            AdjudicationIndicator = mai,
            AppliesToProfessional = professional,
            AppliesToOutpatientFacility = facility,
            EffectiveDate = EffDt,
        };

    // ═══════════════════════════════════════════════════════════════════
    // NE001 — NCCI PAIR BUNDLING
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task NE001_Mi0NoBundleEdit_NoPairOnClaim_Passes()
    {
        // No edit pair seeded — single code claim should pass with no failures
        var repo = new FakeNcciRepository();
        var svc = BuildService(repo);
        var request = OneLineClaim("99213");

        var result = await svc.ScrubAsync(request);

        Assert.True(result.Passed);
        Assert.Empty(result.EditFailures);
    }

    [Fact]
    public async Task NE001_Mi0_BothCodesOnSameDos_DeniesCol2WithCarc97()
    {
        // ModifierIndicator=0: Column 2 is denied, CARC 97
        var repo = new FakeNcciRepository();
        repo.AddEditPair(MakePair("99213", "99212", NcciModifierIndicator.NotAllowed));
        var svc = BuildService(repo);
        var request = TwoLineClaim("99213", "99212");

        var result = await svc.ScrubAsync(request);

        Assert.False(result.Passed);
        Assert.Single(result.EditFailures);
        var failure = result.EditFailures[0];
        Assert.Equal("NE001", failure.RuleId);
        Assert.Equal(NcciEditType.NcciPair, failure.EditType);
        Assert.Equal("99213", failure.Column1Code);
        Assert.Equal("99212", failure.Column2Code);
        Assert.Equal("97", failure.SuggestedCarc);
        Assert.Equal("N519", failure.SuggestedRarc);
        Assert.Contains(2, failure.AffectedLineNumbers); // col2 is line 2
    }

    [Fact]
    public async Task NE001_Mi0_WithModifier59OnCol2_StillDenies()
    {
        // MI=0: modifier cannot override — still denied
        var repo = new FakeNcciRepository();
        repo.AddEditPair(MakePair("99213", "99212", NcciModifierIndicator.NotAllowed));
        var svc = BuildService(repo);
        var request = TwoLineClaim("99213", "99212", modifiers2: ["59"]);

        var result = await svc.ScrubAsync(request);

        Assert.False(result.Passed);
        Assert.Equal("97", result.EditFailures[0].SuggestedCarc);
    }

    [Fact]
    public async Task NE001_Mi1_NoModifierOnCol2_DeniesWithCarcB20()
    {
        // MI=1 without -59/X modifier → denied, CARC B20
        var repo = new FakeNcciRepository();
        repo.AddEditPair(MakePair("99213", "99212", NcciModifierIndicator.Allowed));
        var svc = BuildService(repo);
        var request = TwoLineClaim("99213", "99212");

        var result = await svc.ScrubAsync(request);

        Assert.False(result.Passed);
        Assert.Single(result.EditFailures);
        Assert.Equal("B20", result.EditFailures[0].SuggestedCarc);
    }

    [Fact]
    public async Task NE001_Mi1_Modifier59OnCol2_Passes()
    {
        // MI=1 + -59 on Column 2 line → bundling overridden
        var repo = new FakeNcciRepository();
        repo.AddEditPair(MakePair("99213", "99212", NcciModifierIndicator.Allowed));
        var svc = BuildService(repo);
        var request = TwoLineClaim("99213", "99212", modifiers2: ["59"]);

        var result = await svc.ScrubAsync(request);

        Assert.True(result.Passed);
        Assert.Empty(result.EditFailures);
    }

    [Theory]
    [InlineData("XE")]
    [InlineData("XS")]
    [InlineData("XP")]
    [InlineData("XU")]
    public async Task NE001_Mi1_XModifierOnCol2_Passes(string xModifier)
    {
        // MI=1 + any X{EPSU} modifier overrides bundling
        var repo = new FakeNcciRepository();
        repo.AddEditPair(MakePair("99213", "99212", NcciModifierIndicator.Allowed));
        var svc = BuildService(repo);
        var request = TwoLineClaim("99213", "99212", modifiers2: [xModifier]);

        var result = await svc.ScrubAsync(request);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task NE001_Mi1_Modifier59OnCol1NotCol2_StillDenies()
    {
        // -59 on Column 1 line (not Column 2) → does not override
        var repo = new FakeNcciRepository();
        repo.AddEditPair(MakePair("99213", "99212", NcciModifierIndicator.Allowed));
        var svc = BuildService(repo);
        var request = TwoLineClaim("99213", "99212", modifiers1: ["59"]);

        var result = await svc.ScrubAsync(request);

        Assert.False(result.Passed);
        Assert.Equal("B20", result.EditFailures[0].SuggestedCarc);
    }

    [Fact]
    public async Task NE001_Mi9_RetiredPair_NoFailure()
    {
        // MI=9: retired/informational edit — no denial
        var repo = new FakeNcciRepository();
        repo.AddEditPair(MakePair("99213", "99212", NcciModifierIndicator.NotApplicable));
        var svc = BuildService(repo);
        var request = TwoLineClaim("99213", "99212");

        var result = await svc.ScrubAsync(request);

        Assert.True(result.Passed);
        Assert.Empty(result.EditFailures);
    }

    [Fact]
    public async Task NE001_DifferentDos_NoBundlingApplied()
    {
        // Same codes but on different dates of service → no bundling check
        var repo = new FakeNcciRepository();
        repo.AddEditPair(MakePair("99213", "99212", NcciModifierIndicator.NotAllowed));
        var svc = BuildService(repo);
        var request = TwoLineClaim("99213", "99212", dos2: Dos.AddDays(1));

        var result = await svc.ScrubAsync(request);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task NE001_ReversedCodeOrder_LooksUpBothDirections()
    {
        // Edit pair seeded as (99213, 99212) — request has (99212, 99213).
        // Service should look up both orderings and still find the edit.
        var repo = new FakeNcciRepository();
        repo.AddEditPair(MakePair("99213", "99212", NcciModifierIndicator.NotAllowed));
        var svc = BuildService(repo);
        // Line 1 = 99212 (Col2 code), Line 2 = 99213 (Col1 code)
        var request = TwoLineClaim("99212", "99213");

        var result = await svc.ScrubAsync(request);

        Assert.False(result.Passed);
        Assert.Single(result.EditFailures);
        Assert.Equal("99213", result.EditFailures[0].Column1Code);
        Assert.Equal("99212", result.EditFailures[0].Column2Code);
    }

    // ═══════════════════════════════════════════════════════════════════
    // NE002 — MUE MAXIMUM UNITS
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task NE002_Mai1_UnitsAtLimit_Passes()
    {
        var repo = new FakeNcciRepository();
        repo.AddMueEntry(MakeMue("99213", maxUnits: 3, MueAdjudicationIndicator.ClaimLine));
        var svc = BuildService(repo);
        var request = OneLineClaim("99213", units: 3);

        var result = await svc.ScrubAsync(request);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task NE002_Mai1_UnitsExceedLimit_DeniesWithCarc151()
    {
        var repo = new FakeNcciRepository();
        repo.AddMueEntry(MakeMue("99213", maxUnits: 3, MueAdjudicationIndicator.ClaimLine));
        var svc = BuildService(repo);
        var request = OneLineClaim("99213", units: 4);

        var result = await svc.ScrubAsync(request);

        Assert.False(result.Passed);
        Assert.Single(result.EditFailures);
        var failure = result.EditFailures[0];
        Assert.Equal("NE002", failure.RuleId);
        Assert.Equal(NcciEditType.Mue, failure.EditType);
        Assert.Equal("151", failure.SuggestedCarc);
        Assert.Equal("N115", failure.SuggestedRarc);
        Assert.Equal(4m, failure.UnitsBilled);
        Assert.Equal(3, failure.MueMaxUnits);
    }

    [Fact]
    public async Task NE002_Mai1_TwoLinesEachWithinLimit_Passes()
    {
        // MAI 1: each line checked independently — 2 lines × 3 units each is fine
        var repo = new FakeNcciRepository();
        repo.AddMueEntry(MakeMue("99213", maxUnits: 3, MueAdjudicationIndicator.ClaimLine));
        var svc = BuildService(repo);
        var request = new NcciScrubRequest
        {
            TenantId = Tenant,
            ClaimId = "CLM-003",
            ClaimType = "837P",
            ServiceLines =
            [
                new ClaimServiceLine { LineNumber = 1, ProcedureCode = "99213", Units = 3, ServiceDate = Dos },
                new ClaimServiceLine { LineNumber = 2, ProcedureCode = "99213", Units = 3, ServiceDate = Dos },
            ]
        };

        var result = await svc.ScrubAsync(request);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task NE002_Mai2_TwoLinesSumExceedsLimit_Denies()
    {
        // MAI 2: sum all lines for same code + DOS — 3 + 3 = 6 > MUE of 4
        var repo = new FakeNcciRepository();
        repo.AddMueEntry(MakeMue("99213", maxUnits: 4, MueAdjudicationIndicator.DateOfService));
        var svc = BuildService(repo);
        var request = new NcciScrubRequest
        {
            TenantId = Tenant,
            ClaimId = "CLM-004",
            ClaimType = "837P",
            ServiceLines =
            [
                new ClaimServiceLine { LineNumber = 1, ProcedureCode = "99213", Units = 3, ServiceDate = Dos },
                new ClaimServiceLine { LineNumber = 2, ProcedureCode = "99213", Units = 3, ServiceDate = Dos },
            ]
        };

        var result = await svc.ScrubAsync(request);

        Assert.False(result.Passed);
        var failure = result.EditFailures[0];
        Assert.Equal(6m, failure.UnitsBilled);
        Assert.Equal(4, failure.MueMaxUnits);
        Assert.Contains(1, failure.AffectedLineNumbers);
        Assert.Contains(2, failure.AffectedLineNumbers);
    }

    [Fact]
    public async Task NE002_Mai2_TwoLinesSumAtLimit_Passes()
    {
        var repo = new FakeNcciRepository();
        repo.AddMueEntry(MakeMue("99213", maxUnits: 6, MueAdjudicationIndicator.DateOfService));
        var svc = BuildService(repo);
        var request = new NcciScrubRequest
        {
            TenantId = Tenant,
            ClaimId = "CLM-005",
            ClaimType = "837P",
            ServiceLines =
            [
                new ClaimServiceLine { LineNumber = 1, ProcedureCode = "99213", Units = 3, ServiceDate = Dos },
                new ClaimServiceLine { LineNumber = 2, ProcedureCode = "99213", Units = 3, ServiceDate = Dos },
            ]
        };

        var result = await svc.ScrubAsync(request);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task NE002_Mai3_SumExceedsLimit_Denies()
    {
        // MAI 3 (absolute): same summation logic as MAI 2
        var repo = new FakeNcciRepository();
        repo.AddMueEntry(MakeMue("99213", maxUnits: 2, MueAdjudicationIndicator.DateOfServiceAbsolute));
        var svc = BuildService(repo);
        var request = new NcciScrubRequest
        {
            TenantId = Tenant,
            ClaimId = "CLM-006",
            ClaimType = "837P",
            ServiceLines =
            [
                new ClaimServiceLine { LineNumber = 1, ProcedureCode = "99213", Units = 2, ServiceDate = Dos },
                new ClaimServiceLine { LineNumber = 2, ProcedureCode = "99213", Units = 1, ServiceDate = Dos },
            ]
        };

        var result = await svc.ScrubAsync(request);

        Assert.False(result.Passed);
        Assert.Equal(3m, result.EditFailures[0].UnitsBilled);
    }

    [Fact]
    public async Task NE002_CodeNotInMueTable_Passes()
    {
        // Code has no MUE — should pass regardless of units
        var repo = new FakeNcciRepository();
        var svc = BuildService(repo);
        var request = OneLineClaim("99213", units: 99);

        var result = await svc.ScrubAsync(request);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task NE002_Facility837I_ProfessionalOnlyMue_Skipped()
    {
        // MUE applies only to professional — 837I claim should not be flagged
        var repo = new FakeNcciRepository();
        repo.AddMueEntry(MakeMue("99213", maxUnits: 1, professional: true, facility: false));
        var svc = BuildService(repo);
        var request = new NcciScrubRequest
        {
            TenantId = Tenant,
            ClaimId = "CLM-007",
            ClaimType = "837I",
            ServiceLines =
            [
                new ClaimServiceLine { LineNumber = 1, ProcedureCode = "99213", Units = 5, ServiceDate = Dos }
            ]
        };

        var result = await svc.ScrubAsync(request);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task NE002_Facility837I_FacilityMue_Applied()
    {
        // MUE applies to facility — 837I claim should be flagged
        var repo = new FakeNcciRepository();
        repo.AddMueEntry(MakeMue("99213", maxUnits: 2, professional: false, facility: true));
        var svc = BuildService(repo);
        var request = new NcciScrubRequest
        {
            TenantId = Tenant,
            ClaimId = "CLM-008",
            ClaimType = "837I",
            ServiceLines =
            [
                new ClaimServiceLine { LineNumber = 1, ProcedureCode = "99213", Units = 3, ServiceDate = Dos }
            ]
        };

        var result = await svc.ScrubAsync(request);

        Assert.False(result.Passed);
        Assert.Equal("151", result.EditFailures[0].SuggestedCarc);
    }

    // ═══════════════════════════════════════════════════════════════════
    // COMBINED NE001 + NE002
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task BothEdits_BundlingAndMue_TwoFailures()
    {
        // Claim triggers both a bundling edit (NE001) and an MUE violation (NE002)
        var repo = new FakeNcciRepository();
        repo.AddEditPair(MakePair("99213", "99212", NcciModifierIndicator.NotAllowed));
        repo.AddMueEntry(MakeMue("99213", maxUnits: 1, MueAdjudicationIndicator.ClaimLine));
        var svc = BuildService(repo);
        var request = TwoLineClaim("99213", "99212", units1: 5);

        var result = await svc.ScrubAsync(request);

        Assert.False(result.Passed);
        Assert.Equal(2, result.EditFailures.Count);
        Assert.Contains(result.EditFailures, f => f.RuleId == "NE001");
        Assert.Contains(result.EditFailures, f => f.RuleId == "NE002");
    }

    // ═══════════════════════════════════════════════════════════════════
    // EDGE CASES
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task NE002_Mai2_DifferentDos_UnitsNotSummedAcrossDates()
    {
        // MAI 2: grouping is (code, DOS) — units on different dates are independent
        var repo = new FakeNcciRepository();
        repo.AddMueEntry(MakeMue("99213", maxUnits: 3, MueAdjudicationIndicator.DateOfService));
        var svc = BuildService(repo);
        var request = new NcciScrubRequest
        {
            TenantId = Tenant,
            ClaimId = "CLM-009",
            ClaimType = "837P",
            ServiceLines =
            [
                new ClaimServiceLine { LineNumber = 1, ProcedureCode = "99213", Units = 3, ServiceDate = Dos },
                new ClaimServiceLine { LineNumber = 2, ProcedureCode = "99213", Units = 3, ServiceDate = Dos.AddDays(1) },
            ]
        };

        var result = await svc.ScrubAsync(request);

        // Each date-bucket is within limit — should pass
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task NE001_Mi1_LowercaseModifier_OverridesEdit()
    {
        // Modifier set uses OrdinalIgnoreCase — lowercase "xe" must override
        var repo = new FakeNcciRepository();
        repo.AddEditPair(MakePair("99213", "99212", NcciModifierIndicator.Allowed));
        var svc = BuildService(repo);
        var request = TwoLineClaim("99213", "99212", modifiers2: ["xe"]);

        var result = await svc.ScrubAsync(request);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task NE001_ThreeCodes_OnlyOnePairBundles_SingleFailure()
    {
        // Codes A, B, C on same DOS. Only A→B pair exists — C is unrelated.
        // Loop must not short-circuit; exactly one failure for the A/B pair.
        var repo = new FakeNcciRepository();
        repo.AddEditPair(MakePair("99213", "99212", NcciModifierIndicator.NotAllowed));
        var svc = BuildService(repo);
        var request = new NcciScrubRequest
        {
            TenantId = Tenant,
            ClaimId = "CLM-010",
            ClaimType = "837P",
            ServiceLines =
            [
                new ClaimServiceLine { LineNumber = 1, ProcedureCode = "99213", Units = 1, ServiceDate = Dos },
                new ClaimServiceLine { LineNumber = 2, ProcedureCode = "99212", Units = 1, ServiceDate = Dos },
                new ClaimServiceLine { LineNumber = 3, ProcedureCode = "99214", Units = 1, ServiceDate = Dos },
            ]
        };

        var result = await svc.ScrubAsync(request);

        Assert.False(result.Passed);
        Assert.Single(result.EditFailures);
        Assert.Equal("NE001", result.EditFailures[0].RuleId);
        Assert.Equal("99212", result.EditFailures[0].Column2Code);
        Assert.Equal(3, result.NcciPairsChecked); // pairs: (1,2), (1,3), (2,3)
    }

    [Fact]
    public async Task ScrubAsync_TracksCheckCounts()
    {
        // Counters should reflect actual checks performed
        var repo = new FakeNcciRepository();
        repo.AddEditPair(MakePair("99213", "99212", NcciModifierIndicator.NotAllowed));
        repo.AddMueEntry(MakeMue("99213", maxUnits: 10));
        repo.AddMueEntry(MakeMue("99212", maxUnits: 10));
        var svc = BuildService(repo);
        var request = TwoLineClaim("99213", "99212");

        var result = await svc.ScrubAsync(request);

        Assert.Equal(1, result.NcciPairsChecked); // one unordered pair
        Assert.Equal(2, result.MueChecked);        // one per distinct code+DOS
    }

    [Fact]
    public async Task ScrubAsync_RepeatedCodeLookups_ReusesSharedCache()
    {
        var repo = new FakeNcciRepository();
        repo.AddEditPair(MakePair("99213", "99212", NcciModifierIndicator.NotAllowed));
        repo.AddMueEntry(MakeMue("99213", maxUnits: 10));
        repo.AddMueEntry(MakeMue("99212", maxUnits: 10));
        var cache = new NcciLookupCache();
        var svc = BuildService(repo, cache);
        var request = TwoLineClaim("99213", "99212");

        await svc.ScrubAsync(request);
        var pairCountAfterFirst = repo.EditPairLookupCount;
        var mueCountAfterFirst = repo.MueLookupCount;

        await svc.ScrubAsync(request);

        Assert.Equal(pairCountAfterFirst, repo.EditPairLookupCount);
        Assert.Equal(mueCountAfterFirst, repo.MueLookupCount);
    }

    [Fact]
    public async Task ImportQuarterlyUpdate_InvalidatesTenantCache()
    {
        var repo = new FakeNcciRepository();
        repo.AddMueEntry(MakeMue("99213", maxUnits: 10));
        var cache = new NcciLookupCache();
        var svc = BuildService(repo, cache);
        var request = OneLineClaim("99213", units: 1);

        await svc.ScrubAsync(request);
        await svc.ImportQuarterlyUpdateAsync(Tenant, "2025Q2", [], []);
        await svc.ScrubAsync(request);

        Assert.Equal(2, repo.MueLookupCount);
    }
}
