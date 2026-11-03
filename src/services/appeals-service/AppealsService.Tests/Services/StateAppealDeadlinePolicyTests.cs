using AppealsService.Models;
using AppealsService.Services;
using AppealsService.Services.RulePacks;

namespace AppealsService.Tests.Services;

/// <summary>
/// Federal deadlines tightened by the State rule pack. California Medi-Cal
/// grievances are the case that motivated it: 30 days, 72 hours when
/// expedited, no extension (28 CCR 1300.68; DHCS APL 21-011), where the
/// federal floor allows 90 days plus 14.
/// </summary>
public class StateAppealDeadlinePolicyTests
{
    private static readonly RulePackCatalog Catalog = RulePackCatalog.FromEmbeddedResources();
    private static readonly DateTime Received = new(2026, 11, 2, 15, 0, 0, DateTimeKind.Utc);

    private static RulePack Pack(string state, LineOfBusiness lob = LineOfBusiness.Medicaid) =>
        Catalog.Find(state, lob, DateOnly.FromDateTime(Received))
        ?? throw new InvalidOperationException($"No {lob} pack for {state}.");

    private static AppealDeadlines Resolve(
        RulePack? pack, AppealType type, bool urgent = false,
        LineOfBusiness lob = LineOfBusiness.Medicaid, AppealLevel level = AppealLevel.FirstLevel) =>
        StateAppealDeadlinePolicy.Resolve(pack, Received, lob, type, level, urgent, UsFederalHolidayCalendar.Instance);

    // ── California ──────────────────────────────────────────────────────

    [Fact]
    public void California_grievance_is_30_days_with_no_extension()
    {
        var d = Resolve(Pack("CA"), AppealType.Grievance);

        d.DefaultTarget.Should().Be(Received.AddDays(30));
        d.EnforceableMaximum.Should().Be(Received.AddDays(30));
        d.Extension.IsPermitted.Should().BeFalse();
        d.Extension.RegulatoryBasis.Should().Contain("APL 21-011").And.Contain("CA rule pack");
        d.MaxExtendedTarget.Should().Be(Received.AddDays(30));
        d.RulePack.Should().StartWith("ca-medicaid:");
    }

    [Fact]
    public void California_expedited_grievance_is_72_hours_enforced_with_no_extension()
    {
        var d = Resolve(Pack("CA"), AppealType.Grievance, urgent: true);

        d.DefaultTarget.Should().Be(Received.AddHours(72));
        d.EnforceableMaximum.Should().Be(Received.AddHours(72),
            "the federal rule has no expedited grievance clock, but California's 72 hours is binding");
        d.Extension.IsPermitted.Should().BeFalse();
    }

    [Theory]
    [InlineData("CA")]
    [InlineData("CO")] // member appeals 10 working days: must not reach provider claim disputes
    [InlineData("IL")]
    public void Appeals_stay_on_the_federal_clocks_until_the_case_track_exists(string state)
    {
        foreach (var urgent in new[] { false, true })
        {
            var d = Resolve(Pack(state), AppealType.Reconsideration, urgent);
            var federal = Resolve(null, AppealType.Reconsideration, urgent);

            d.Should().Be(federal, $"{state} urgent={urgent}");
            d.RulePack.Should().BeNull();
        }
    }

    // ── Texas ───────────────────────────────────────────────────────────

    [Fact]
    public void Texas_grievance_keeps_90_days_and_the_14_day_extension()
    {
        var d = Resolve(Pack("TX"), AppealType.Grievance);

        d.DefaultTarget.Should().Be(Received.AddDays(90));
        d.EnforceableMaximum.Should().Be(Received.AddDays(90));
        d.Extension.MaxExtension.Should().Be(TimeSpan.FromDays(14));
        d.RulePack.Should().StartWith("tx-medicaid:");
    }

    [Fact]
    public void Texas_expedited_grievance_keeps_the_federal_72_hour_default_and_90_day_ceiling()
    {
        // The Texas pack sets no expedited grievance clock; the earlier of
        // the federal default and the pack's 90 days is the federal 72 hours.
        var d = Resolve(Pack("TX"), AppealType.Grievance, urgent: true);
        var federal = Resolve(null, AppealType.Grievance, urgent: true);

        d.DefaultTarget.Should().Be(federal.DefaultTarget).And.Be(Received.AddHours(72));
        d.EnforceableMaximum.Should().Be(federal.EnforceableMaximum).And.Be(Received.AddDays(90));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Texas_appeal_matches_the_federal_clocks(bool urgent)
    {
        var d = Resolve(Pack("TX"), AppealType.Reconsideration, urgent);
        var federal = Resolve(null, AppealType.Reconsideration, urgent);

        d.DefaultTarget.Should().Be(federal.DefaultTarget);
        d.EnforceableMaximum.Should().Be(federal.EnforceableMaximum);
        d.Extension.MaxExtension.Should().Be(federal.Extension.MaxExtension);
    }

    // ── Other States follow their packs ─────────────────────────────────

    [Fact]
    public void Arizona_grievance_runs_on_business_days()
    {
        // az.json: 10 business days. Monday 2026-11-02 + 10 business days,
        // skipping Veterans Day (Wed 11-11) → Tuesday 2026-11-17.
        var d = Resolve(Pack("AZ"), AppealType.Grievance);

        d.DefaultTarget.Should().Be(new DateTime(2026, 11, 17, 15, 0, 0, DateTimeKind.Utc));
        d.EnforceableMaximum.Should().Be(d.DefaultTarget);
    }

    [Fact]
    public void A_pack_silent_on_grievance_extensions_leaves_the_federal_extension()
    {
        Pack("AZ").MemberGrievance.Extension.Should().BeNull("this test relies on az.json stating no grievance extension");

        var d = Resolve(Pack("AZ"), AppealType.Grievance);

        d.Extension.Should().Be(AppealResponseDeadlinePolicy.GetExtensionRule(
            LineOfBusiness.Medicaid, AppealType.Grievance, AppealLevel.FirstLevel, isUrgent: false));
    }

    // ── Where packs do not apply ────────────────────────────────────────

    [Fact]
    public void No_pack_means_federal_rules()
    {
        var d = Resolve(null, AppealType.Grievance);

        d.DefaultTarget.Should().Be(Received.AddDays(90));
        d.EnforceableMaximum.Should().Be(Received.AddDays(90));
        d.Extension.MaxExtension.Should().Be(TimeSpan.FromDays(14));
        d.RulePack.Should().BeNull();
    }

    [Fact]
    public void A_pack_for_another_line_of_business_is_ignored()
    {
        var d = Resolve(Pack("CA"), AppealType.Grievance, lob: LineOfBusiness.Commercial);

        d.RulePack.Should().BeNull();
        d.EnforceableMaximum.Should().BeNull("commercial grievances have no federal ceiling");
    }

    [Fact]
    public void State_Fair_Hearing_is_not_governed_by_the_plan_level_pack()
    {
        var d = Resolve(Pack("CA"), AppealType.ExternalReview, level: AppealLevel.ExternalReview);

        d.RulePack.Should().BeNull();
        d.DefaultTarget.Should().Be(Received.AddDays(90));
        d.Extension.IsPermitted.Should().BeFalse();
    }

    [Fact]
    public void The_national_Medicare_Advantage_pack_does_not_turn_the_24_hour_default_into_a_ceiling()
    {
        var pack = Pack("CA", LineOfBusiness.Medicare);
        pack.IsNational.Should().BeTrue();

        var d = Resolve(pack, AppealType.Grievance, urgent: true, lob: LineOfBusiness.Medicare);

        d.RulePack.Should().BeNull();
        d.DefaultTarget.Should().Be(Received.AddHours(24));
        d.EnforceableMaximum.Should().Be(Received.AddDays(30));
    }

    [Fact]
    public void Every_State_pack_only_tightens_the_federal_clocks()
    {
        foreach (var pack in Catalog.All.Where(p => !p.IsNational))
        foreach (var type in new[] { AppealType.Reconsideration, AppealType.Grievance })
        foreach (var urgent in new[] { false, true })
        {
            var state = Resolve(pack, type, urgent, pack.LineOfBusiness);
            var federal = Resolve(null, type, urgent, pack.LineOfBusiness);
            var label = $"{pack.Id} {type} urgent={urgent}";

            state.DefaultTarget.Should().BeOnOrBefore(federal.DefaultTarget, label);
            if (federal.EnforceableMaximum is { } max)
                state.EnforceableMaximum.Should().BeOnOrBefore(max, label);
            state.Extension.MaxExtension.Should().BeLessThanOrEqualTo(federal.Extension.MaxExtension, label);
            state.DefaultTarget.Should().BeOnOrBefore(state.EnforceableMaximum!.Value, label);
        }
    }
}
