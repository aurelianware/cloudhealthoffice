using CloudHealthOffice.GrouperEngine.Domain;
using CloudHealthOffice.GrouperEngine.Formulas;
using CloudHealthOffice.GrouperEngine.Hac;
using CloudHealthOffice.GrouperEngine.Mask;
using CloudHealthOffice.GrouperEngine.Severity;
using CloudHealthOffice.GrouperEngine.Models;
using CloudHealthOffice.GrouperEngine.Services;
using Xunit;

namespace CloudHealthOffice.GrouperEngine.Tests;

/// <summary>
/// The grouper seam: the two implementations a deployment chooses between, and
/// what each of them promises.
///
/// <para>
/// The seam is what lets the pricing path ask for a DRG without knowing which
/// grouper answered, and the thing worth testing about it is that the default
/// implementation does nothing. A payer's claims keep being paid exactly as
/// they were until someone deliberately points the service at a CMS release.
/// </para>
/// </summary>
public class GrouperSeamTests
{
    private static MsDrgGroupingRequest Stay(string? billedDrg = null) => new()
    {
        PrincipalDiagnosis = new GroupingDiagnosis("E1165"),
        DischargeDate = new DateOnly(2025, 3, 4),
        BilledDrg = billedDrg
    };

    // ─────────────────────────────────────────────────────────
    //  The billed-DRG implementation
    // ─────────────────────────────────────────────────────────

    [Fact]
    public async Task The_billed_drg_grouper_reports_the_claims_own_drg_and_says_it_grouped_nothing()
    {
        var result = await new BilledDrgGrouper().GroupAsync(Stay(billedDrg: "470"));

        Assert.Equal(GroupingStatus.Grouped, result.Status);
        Assert.Equal("470", result.Drg);

        // The audit question a payer has to answer about any paid DRG is where
        // it came from, and "the facility said so" is a real answer.
        Assert.Equal(DrgSource.BilledByProvider, result.Source);
        Assert.Contains(result.Notes, note => note.Contains("No grouping was performed"));
    }

    [Fact]
    public async Task The_billed_drg_grouper_has_no_answer_when_the_claim_carries_no_drg()
    {
        var result = await new BilledDrgGrouper().GroupAsync(Stay(billedDrg: null));

        Assert.Equal(GroupingStatus.NoBilledDrg, result.Status);
        Assert.Null(result.Drg);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_billed_drg_is_no_drg(string billed)
    {
        var result = await new BilledDrgGrouper().GroupAsync(Stay(billedDrg: billed));

        Assert.Equal(GroupingStatus.NoBilledDrg, result.Status);
    }

    [Fact]
    public async Task The_billed_drg_grouper_never_disagrees_with_the_claim()
    {
        var result = await new BilledDrgGrouper().GroupAsync(Stay(billedDrg: "470"));

        // It reported the claim's own DRG, so the payment-integrity signal has
        // to stay quiet: a disagreement here would be the grouper disagreeing
        // with itself.
        Assert.False(result.DisagreesWithBilled);
    }

    // ─────────────────────────────────────────────────────────
    //  The formula engine behind the seam
    // ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_deployment_with_no_release_for_the_discharge_date_groups_nothing()
    {
        var grouper = new FormulaMsDrgGrouper(new ReleaseWindowProvider([]));

        var result = await grouper.GroupAsync(Stay());

        Assert.Equal(GroupingStatus.NoTablesForDischargeDate, result.Status);
        Assert.Null(result.Drg);
    }

    [Fact]
    public void A_release_prices_the_discharge_dates_in_its_own_window_and_no_others()
    {
        var window = new MsDrgReleaseWindow(
            Release("V440"), new DateOnly(2024, 10, 1), new DateOnly(2025, 9, 30));

        Assert.False(window.Covers(new DateOnly(2024, 9, 30)));  // the release had not started
        Assert.True(window.Covers(new DateOnly(2024, 10, 1)));
        Assert.True(window.Covers(new DateOnly(2025, 9, 30)));
        Assert.False(window.Covers(new DateOnly(2025, 10, 1)));  // the next release prices this one
    }

    [Fact]
    public void The_current_release_has_no_last_discharge_date()
    {
        var current = new MsDrgReleaseWindow(Release("V440"), new DateOnly(2024, 10, 1), null);

        Assert.True(current.Covers(new DateOnly(2030, 1, 1)));
    }

    [Fact]
    public async Task A_stay_is_grouped_by_the_release_in_force_on_its_discharge_date()
    {
        // Two windows that abut, and a discharge date in the second. The
        // version on the result is how a caller knows which tables answered,
        // and choosing the wrong one prices a stay against the wrong fiscal
        // year — which is a wrong payment, not an error.
        var provider = new ReleaseWindowProvider(
        [
            new MsDrgReleaseWindow(Release("V430"), new DateOnly(2023, 10, 1), new DateOnly(2024, 9, 30)),
            new MsDrgReleaseWindow(Release("V440"), new DateOnly(2024, 10, 1), null)
        ]);

        var result = await new FormulaMsDrgGrouper(provider).GroupAsync(Stay());

        Assert.Equal("V440", result.GrouperVersion);
    }

    // ─────────────────────────────────────────────────────────
    //  CMS's return codes, mapped onto the seam's statuses
    // ─────────────────────────────────────────────────────────

    [Fact]
    public void An_unrecognised_principal_diagnosis_arrives_as_cmss_own_edit()
    {
        // Not a pre-grouping check: CMS refuses the claim through the
        // invalid_pdx edit, return code 7, during grouping.
        Assert.Equal(GroupingStatus.InvalidPrincipalDiagnosis, FormulaMsDrgGrouper.StatusFor(7));
    }

    [Theory]
    [InlineData(MsDrgHacProcessor.MissingOnePoa)]
    [InlineData(MsDrgHacProcessor.UnknownStatusPoaNotPresent)]
    [InlineData(MsDrgHacProcessor.UnknownStatusPoaUnreadable)]
    [InlineData(MsDrgHacProcessor.UnknownStatusMultipleConditions)]
    public void Every_hac_refusal_is_about_unusable_present_on_admission_data(int returnCode)
    {
        Assert.Equal(
            GroupingStatus.PresentOnAdmissionUnusable, FormulaMsDrgGrouper.StatusFor(returnCode));
    }

    [Fact]
    public void Any_other_edit_is_named_by_its_return_code_rather_than_guessed_at()
    {
        // The edits are table data and a new fiscal year can add one, so an
        // unrecognised code must not be mapped onto a status that claims to
        // know what the edit was about.
        Assert.Equal(GroupingStatus.RefusedByCodeEdit, FormulaMsDrgGrouper.StatusFor(123));
    }

    [Fact]
    public void No_drg_and_no_return_code_means_no_row_matched()
    {
        Assert.Equal(GroupingStatus.NoDrgRuleMatched, FormulaMsDrgGrouper.StatusFor(null));
    }

    // ─────────────────────────────────────────────────────────
    //  Against a real release
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// Groups a stay through the seam against a licensed CMS release, so that
    /// the adapter's translation is checked against real tables rather than a
    /// stub of them.
    ///
    /// <para>
    /// Gated on <c>CMS_MSDRG_TABLES</c>: no CMS table is in this repository and
    /// none can be. CMS distributes the MS-DRG package per requester and it
    /// carries American Hospital Association copyright in the UB-04 code
    /// descriptions.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_real_release_groups_a_stay_through_the_seam()
    {
        var release = RealRelease(out var version);
        var grouper = new FormulaMsDrgGrouper(new ReleaseWindowProvider(
            [new MsDrgReleaseWindow(release, new DateOnly(2024, 10, 1), null)]));

        var result = await grouper.GroupAsync(new MsDrgGroupingRequest
        {
            PrincipalDiagnosis = new GroupingDiagnosis("I5021", "Y"),
            SecondaryDiagnoses = [new GroupingDiagnosis("N179", "Y")],
            DischargeDate = new DateOnly(2025, 3, 4),
            DischargeStatus = "01",
            AgeYears = 71,
            Sex = "F",
            PoaReporting = HospitalPoaReporting.NonExempt,
            BilledDrg = "291"
        });

        Assert.Equal(GroupingStatus.Grouped, result.Status);
        Assert.Equal(DrgSource.ChoGrouper, result.Source);
        Assert.Equal(version, result.GrouperVersion);

        // The fields a remittance and an appeal are answered from.
        Assert.Matches("^[0-9]{3}$", result.Drg);
        Assert.True(result.Mdc > 0);
        Assert.Matches("^[0-9]{3}$", result.FamilyKey);
        Assert.Equal(DrgPartition.Medical, result.Partition);  // heart failure, no procedure

        // Severity is deliberately unset through this adapter, because CMS's
        // formula table does not carry the family's severity marker.
        Assert.Null(result.Severity);

        // Acute kidney failure is a complication and was present on admission,
        // so it counts and it is the code an appeal would be about.
        Assert.NotEqual(ComplicationLevel.None, result.ComplicationLevel);
        Assert.Equal("N179", result.DrivingComplicationCode);
    }

    /// <summary>
    /// The one claim-data answer this adapter must not turn into a refusal.
    /// </summary>
    [SkippableFact]
    public async Task A_real_release_reports_an_unknown_secondary_rather_than_refusing_the_stay()
    {
        var release = RealRelease(out _);
        var grouper = new FormulaMsDrgGrouper(new ReleaseWindowProvider(
            [new MsDrgReleaseWindow(release, new DateOnly(2024, 10, 1), null)]));

        var result = await grouper.GroupAsync(new MsDrgGroupingRequest
        {
            PrincipalDiagnosis = new GroupingDiagnosis("I5021", "Y"),
            SecondaryDiagnoses = [new GroupingDiagnosis("ZZZZZ", "Y")],
            DischargeDate = new DateOnly(2025, 3, 4),
            DischargeStatus = "01",
            PoaReporting = HospitalPoaReporting.NonExempt
        });

        // CMS groups a stay carrying an unknown secondary as though the code
        // were absent, and the parity run is what settled that.
        Assert.Equal(GroupingStatus.Grouped, result.Status);

        // The caller still has to be told, because rejecting the claim back to
        // the biller is its policy to make and not the grouper's.
        Assert.Contains(result.Notes, note => note.Contains("ZZZZZ"));
    }

    /// <summary>
    /// A hospital-acquired condition that stops counting is reported as an
    /// excluded complication with the reason, which is the field an appeal
    /// against the HAC policy is answered from.
    /// </summary>
    [SkippableFact]
    public async Task A_condition_the_hospital_caused_is_reported_with_the_reason_it_did_not_count()
    {
        var release = RealRelease(out _);
        var grouper = new FormulaMsDrgGrouper(new ReleaseWindowProvider(
            [new MsDrgReleaseWindow(release, new DateOnly(2024, 10, 1), null)]));

        var result = await grouper.GroupAsync(new MsDrgGroupingRequest
        {
            PrincipalDiagnosis = new GroupingDiagnosis("I5021", "Y"),

            // Acute kidney failure, reported as NOT present on admission.
            SecondaryDiagnoses = [new GroupingDiagnosis("N179", "N")],
            DischargeDate = new DateOnly(2025, 3, 4),
            DischargeStatus = "01",
            PoaReporting = HospitalPoaReporting.NonExempt
        });

        Assert.Equal(GroupingStatus.Grouped, result.Status);

        // Whether this particular code is on a HAC list is the release's
        // business, so the assertion is about the shape of the answer: every
        // excluded complication names a real level and a reason a human can
        // act on.
        Assert.All(result.ExcludedComplications, excluded =>
        {
            Assert.NotEqual(ComplicationLevel.None, excluded.Level);
            Assert.False(string.IsNullOrWhiteSpace(excluded.Reason));
        });
    }

    private static MsDrgRelease RealRelease(out string version)
    {
        var tables = Environment.GetEnvironmentVariable("CMS_MSDRG_TABLES");
        Skip.If(
            string.IsNullOrWhiteSpace(tables),
            "Set CMS_MSDRG_TABLES to a CMS MS-DRG release's table directory to run this.");

        version = Environment.GetEnvironmentVariable("CMS_MSDRG_VERSION") ?? "V440";
        return CmsJsonTableImporter.ImportRelease(tables!, version);
    }

    private static MsDrgRelease Release(string version) => new(
        version,
        new InMemoryCodeAttributeSource(version, [], []),
        new InMemoryClusterSource([]),
        new InMemoryFormulaSource(version, []),
        new InMemoryExclusionSource(version, []),
        InMemoryHacSource.None);
}
