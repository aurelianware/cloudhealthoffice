using CloudHealthOffice.GrouperEngine.Data;
using CloudHealthOffice.GrouperEngine.Domain;
using CloudHealthOffice.GrouperEngine.Models;
using CloudHealthOffice.GrouperEngine.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudHealthOffice.GrouperEngine.Tests;

/// <summary>
/// The grouping logic, exercised against the hand-built seed slice in
/// <see cref="MsDrgSeedTables"/>. These assert the <i>logic</i> — severity
/// resolution, the surgical hierarchy, CC exclusions, the MCE edits — not
/// agreement with CMS, which needs the real tables and the parity corpus
/// (ADR 024 §5 step 7).
/// </summary>
public class TableDrivenMsDrgGrouperTests
{
    private static readonly DateOnly InWindow = new(2026, 3, 15);

    private static TableDrivenMsDrgGrouper Grouper(MsDrgTableSet? tables = null) =>
        new(new InMemoryMsDrgTableProvider(tables ?? MsDrgSeedTables.Build()),
            NullLogger<TableDrivenMsDrgGrouper>.Instance);

    private static MsDrgGroupingRequest HeartFailureStay(
        params GroupingDiagnosis[] secondary) => new()
    {
        PrincipalDiagnosis = new GroupingDiagnosis("I509", "Y"),
        SecondaryDiagnoses = secondary,
        DischargeDate = InWindow,
        AgeYears = 71,
        Sex = "M",
        DischargeStatus = "01",
        LengthOfStayDays = 4,
    };

    // ─────────────────────────────────────────────────────────
    //  Severity resolution
    // ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Mcc_on_a_three_way_family_groups_to_the_with_mcc_drg()
    {
        var result = await Grouper().GroupAsync(
            HeartFailureStay(new GroupingDiagnosis("J9600", "Y")));

        Assert.Equal(GroupingStatus.Grouped, result.Status);
        Assert.Equal("291", result.Drg);
        Assert.Equal(5, result.Mdc);
        Assert.Equal(DrgPartition.Medical, result.Partition);
        Assert.Equal(DrgSeverity.WithMcc, result.Severity);
        Assert.Equal(ComplicationLevel.Mcc, result.ComplicationLevel);
        Assert.Equal("J9600", result.DrivingComplicationCode);
        Assert.Equal(DrgSource.ChoGrouper, result.Source);
        Assert.Equal(MsDrgSeedTables.SeedVersion, result.GrouperVersion);
    }

    [Fact]
    public async Task Cc_on_a_three_way_family_groups_to_the_with_cc_drg()
    {
        var result = await Grouper().GroupAsync(
            HeartFailureStay(new GroupingDiagnosis("N179", "Y")));

        Assert.Equal("292", result.Drg);
        Assert.Equal(DrgSeverity.WithCc, result.Severity);
        Assert.Equal(ComplicationLevel.Cc, result.ComplicationLevel);
        Assert.Equal("N179", result.DrivingComplicationCode);
    }

    [Fact]
    public async Task No_comorbidity_groups_to_the_without_cc_mcc_drg()
    {
        var result = await Grouper().GroupAsync(HeartFailureStay());

        Assert.Equal("293", result.Drg);
        Assert.Equal(DrgSeverity.WithoutCcMcc, result.Severity);
        Assert.Equal(ComplicationLevel.None, result.ComplicationLevel);
        Assert.Null(result.DrivingComplicationCode);
    }

    [Fact]
    public async Task An_mcc_outranks_a_cc_and_is_reported_as_the_driver()
    {
        var result = await Grouper().GroupAsync(HeartFailureStay(
            new GroupingDiagnosis("N179", "Y"),    // CC, listed first
            new GroupingDiagnosis("A419", "Y")));  // MCC

        Assert.Equal("291", result.Drg);
        Assert.Equal("A419", result.DrivingComplicationCode);
    }

    [Fact]
    public async Task A_secondary_diagnosis_repeating_the_principal_does_not_raise_severity()
    {
        var result = await Grouper().GroupAsync(new MsDrgGroupingRequest
        {
            PrincipalDiagnosis = new GroupingDiagnosis("N179", "Y"),
            SecondaryDiagnoses = [new GroupingDiagnosis("N179", "Y")],
            DischargeDate = InWindow,
        });

        // N179 is a CC but is not an MDC-assignment code in the seed slice, so
        // the stay cannot group — the point is that it did not count itself.
        Assert.Equal(GroupingStatus.NoMdcAssignment, result.Status);
    }

    // ─────────────────────────────────────────────────────────
    //  Families that are not split at every severity
    // ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_cc_on_a_two_way_family_pays_at_without_mcc_and_says_so()
    {
        var result = await Grouper().GroupAsync(KneeReplacement(
            new GroupingDiagnosis("N179", "Y")));   // CC only

        Assert.Equal("470", result.Drg);
        Assert.Equal(DrgSeverity.WithoutCcMcc, result.Severity);
        Assert.Equal(ComplicationLevel.Cc, result.ComplicationLevel);
        Assert.Contains(result.Notes, n => n.Contains("not split at WithCc"));
    }

    [Fact]
    public async Task An_mcc_on_a_two_way_family_pays_at_with_mcc()
    {
        var result = await Grouper().GroupAsync(KneeReplacement(
            new GroupingDiagnosis("J9601", "Y")));

        Assert.Equal("469", result.Drg);
        Assert.Equal(DrgSeverity.WithMcc, result.Severity);
    }

    // ─────────────────────────────────────────────────────────
    //  Partition and the surgical hierarchy
    // ─────────────────────────────────────────────────────────

    [Fact]
    public async Task An_operating_room_procedure_puts_the_stay_in_the_surgical_partition()
    {
        var result = await Grouper().GroupAsync(KneeReplacement());

        Assert.Equal(DrgPartition.Surgical, result.Partition);
        Assert.Equal(MsDrgSeedTables.SurgicalClassKeys.MajorJointLower, result.SurgicalClass);
        Assert.Equal("0SRC0J9", result.DrivingProcedureCode);
        Assert.Equal("470", result.Drg);
    }

    [Fact]
    public async Task The_same_stay_without_the_procedure_falls_to_the_medical_partition()
    {
        var result = await Grouper().GroupAsync(new MsDrgGroupingRequest
        {
            PrincipalDiagnosis = new GroupingDiagnosis("M1711", "Y"),
            DischargeDate = InWindow,
        });

        // MDC 08 has no medical family in the seed slice, which is the honest
        // answer: the stay reached the medical partition and found no rule.
        Assert.Equal(GroupingStatus.NoDrgRuleMatched, result.Status);
        Assert.Null(result.Drg);
    }

    [Fact]
    public async Task A_procedure_outside_the_stays_mdc_does_not_make_it_surgical()
    {
        var result = await Grouper().GroupAsync(new MsDrgGroupingRequest
        {
            // Heart-failure admission (MDC 05) that also carries a joint
            // replacement, which is only an OR procedure in MDC 08.
            PrincipalDiagnosis = new GroupingDiagnosis("I509", "Y"),
            Procedures = [new GroupingProcedure("0SRC0J9")],
            DischargeDate = InWindow,
        });

        Assert.Equal(DrgPartition.Medical, result.Partition);
        Assert.Equal("293", result.Drg);
        Assert.Null(result.SurgicalClass);
    }

    // ─────────────────────────────────────────────────────────
    //  Pre-MDC
    // ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_pre_mdc_procedure_groups_before_mdc_assignment()
    {
        var result = await Grouper().GroupAsync(new MsDrgGroupingRequest
        {
            PrincipalDiagnosis = new GroupingDiagnosis("I509", "Y"),
            SecondaryDiagnoses = [new GroupingDiagnosis("J9600", "Y")],
            Procedures = [new GroupingProcedure("02YA0Z0")],
            DischargeDate = InWindow,
        });

        Assert.Equal("001", result.Drg);
        Assert.Equal(DrgPartition.PreMdc, result.Partition);
        Assert.Null(result.Mdc);
        Assert.Contains(result.Notes, n => n.Contains("Pre-MDC"));
    }

    [Fact]
    public async Task A_pre_mdc_family_is_still_split_by_severity()
    {
        var result = await Grouper().GroupAsync(new MsDrgGroupingRequest
        {
            PrincipalDiagnosis = new GroupingDiagnosis("I509", "Y"),
            Procedures = [new GroupingProcedure("02YA0Z0")],
            DischargeDate = InWindow,
        });

        Assert.Equal("002", result.Drg);
        Assert.Equal(DrgSeverity.WithoutCcMcc, result.Severity);
    }

    // ─────────────────────────────────────────────────────────
    //  CC exclusions and hospital-acquired conditions
    // ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_cc_excluded_by_the_principal_diagnosis_does_not_count_and_is_recorded()
    {
        var result = await Grouper().GroupAsync(new MsDrgGroupingRequest
        {
            PrincipalDiagnosis = new GroupingDiagnosis("J189", "Y"),
            SecondaryDiagnoses = [new GroupingDiagnosis("J181", "Y")],
            DischargeDate = InWindow,
        });

        Assert.Equal("195", result.Drg);
        Assert.Equal(ComplicationLevel.None, result.ComplicationLevel);
        var excluded = Assert.Single(result.ExcludedComplications);
        Assert.Equal("J181", excluded.Code);
        Assert.Equal(ComplicationLevel.Cc, excluded.Level);
        Assert.Contains("CC exclusion", excluded.Reason);
    }

    [Theory]
    [InlineData("N")]
    [InlineData("U")]
    public async Task An_mcc_that_was_not_present_on_admission_does_not_raise_severity(string poa)
    {
        var result = await Grouper().GroupAsync(
            HeartFailureStay(new GroupingDiagnosis("J9600", poa)));

        Assert.Equal("293", result.Drg);
        Assert.Equal(ComplicationLevel.None, result.ComplicationLevel);
        var excluded = Assert.Single(result.ExcludedComplications);
        Assert.Contains("Not present on admission", excluded.Reason);
    }

    [Theory]
    [InlineData("Y")]
    [InlineData("W")]
    [InlineData("1")]
    [InlineData(null)]
    public async Task An_mcc_present_on_admission_or_exempt_raises_severity(string? poa)
    {
        var result = await Grouper().GroupAsync(
            HeartFailureStay(new GroupingDiagnosis("J9600", poa)));

        Assert.Equal("291", result.Drg);
        Assert.Empty(result.ExcludedComplications);
    }

    // ─────────────────────────────────────────────────────────
    //  Input handling and the MCE edits
    // ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Codes_are_matched_without_their_decimal_point_and_case_insensitively()
    {
        var result = await Grouper().GroupAsync(new MsDrgGroupingRequest
        {
            PrincipalDiagnosis = new GroupingDiagnosis(" i50.9 ", "Y"),
            SecondaryDiagnoses = [new GroupingDiagnosis("j96.00", "Y")],
            DischargeDate = InWindow,
        });

        Assert.Equal("291", result.Drg);
    }

    [Fact]
    public async Task A_missing_principal_diagnosis_is_reported_not_thrown()
    {
        var result = await Grouper().GroupAsync(new MsDrgGroupingRequest
        {
            PrincipalDiagnosis = new GroupingDiagnosis("   "),
            DischargeDate = InWindow,
        });

        Assert.Equal(GroupingStatus.MissingPrincipalDiagnosis, result.Status);
        Assert.Null(result.Drg);
    }

    [Fact]
    public async Task A_principal_diagnosis_outside_the_version_code_set_is_reported_as_invalid()
    {
        var result = await Grouper().GroupAsync(new MsDrgGroupingRequest
        {
            PrincipalDiagnosis = new GroupingDiagnosis("X9999"),
            DischargeDate = InWindow,
        });

        Assert.Equal(GroupingStatus.InvalidPrincipalDiagnosis, result.Status);
    }

    [Fact]
    public async Task An_unacceptable_principal_diagnosis_is_reported_as_such()
    {
        var result = await Grouper().GroupAsync(new MsDrgGroupingRequest
        {
            PrincipalDiagnosis = new GroupingDiagnosis("Z0289"),
            DischargeDate = InWindow,
        });

        Assert.Equal(GroupingStatus.UnacceptablePrincipalDiagnosis, result.Status);
    }

    [Fact]
    public async Task A_discharge_date_no_version_covers_does_not_fall_back_to_another_version()
    {
        var result = await Grouper().GroupAsync(HeartFailureStay() with
        {
            DischargeDate = new DateOnly(2019, 6, 1),
        });

        Assert.Equal(GroupingStatus.NoTablesForDischargeDate, result.Status);
        Assert.Null(result.Drg);
    }

    [Fact]
    public async Task A_sex_specific_drg_rejects_a_conflicting_claim()
    {
        var tables = MsDrgSeedTables.Build();
        var femaleOnly = tables.Drgs.Values
            .Select(d => d.FamilyKey == MsDrgSeedTables.Families.HeartFailure
                ? d with { RequiredSex = "F" }
                : d)
            .ToDictionary(d => d.Drg, StringComparer.OrdinalIgnoreCase);

        var result = await Grouper(tables with { Drgs = femaleOnly })
            .GroupAsync(HeartFailureStay());   // the stay carries Sex = "M"

        Assert.Equal(GroupingStatus.DemographicConflict, result.Status);
    }

    // ─────────────────────────────────────────────────────────
    //  Agreement with the billed DRG
    // ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_billed_drg_that_differs_from_the_assigned_one_is_flagged()
    {
        var result = await Grouper().GroupAsync(KneeReplacement() with { BilledDrg = "469" });

        Assert.Equal("470", result.Drg);
        Assert.Equal("469", result.BilledDrg);
        Assert.True(result.DisagreesWithBilled);
    }

    [Theory]
    [InlineData("470")]
    [InlineData("0470")]
    public async Task A_billed_drg_matching_the_assigned_one_is_not_flagged(string billed)
    {
        var result = await Grouper().GroupAsync(KneeReplacement() with { BilledDrg = billed });

        Assert.False(result.DisagreesWithBilled);
    }

    [Fact]
    public async Task A_claim_with_no_billed_drg_reports_no_agreement_either_way()
    {
        var result = await Grouper().GroupAsync(KneeReplacement());

        Assert.Null(result.DisagreesWithBilled);
    }

    private static MsDrgGroupingRequest KneeReplacement(params GroupingDiagnosis[] secondary) => new()
    {
        PrincipalDiagnosis = new GroupingDiagnosis("M1711", "Y"),
        SecondaryDiagnoses = secondary,
        Procedures = [new GroupingProcedure("0SRC0J9", new DateOnly(2026, 3, 12))],
        DischargeDate = InWindow,
        AgeYears = 68,
        Sex = "F",
        DischargeStatus = "01",
        LengthOfStayDays = 3,
    };
}
