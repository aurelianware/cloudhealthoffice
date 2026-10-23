using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AppealsService.Models;
using AppealsService.Services;
using AppealsService.Services.RulePacks;
using FluentAssertions;
using Xunit;

namespace AppealsService.Tests.Services.RulePacks;

public class RulePackTests
{
    private static readonly RulePackCatalog Catalog = RulePackCatalog.FromEmbeddedResources();
    private static readonly DateOnly AsOf = new(2026, 11, 1);

    private static RulePack Pack(string state) =>
        Catalog.Find(state, LineOfBusiness.Medicaid, AsOf) ?? throw new InvalidOperationException($"No pack for {state}.");

    private static DateTime Utc(int y, int m, int d, int h = 12) => new(y, m, d, h, 0, 0, DateTimeKind.Utc);

    private static RuleDeadline Deadline(RulePackEvaluation e, string name) => e.Deadlines.Single(d => d.Name == name);

    private static RulePackEvaluation Appeal(string state, DateTime received, bool expedited = false,
        DateTime? info = null, DateTime? decided = null, DateTime? notice = null, IHolidayCalendar? holidays = null) =>
        RulePackEvaluator.Evaluate(Pack(state), new RulePackEvaluationInput
        {
            CaseType = RulePackCaseType.MemberAppeal,
            Expedited = expedited,
            ReceivedAt = received,
            InformationReceivedAt = info,
            DecidedAt = decided,
            NoticeSentAt = notice
        }, holidays ?? UsFederalHolidayCalendar.Instance);

    // ── Catalog ─────────────────────────────────────────────────────────

    [Fact]
    public void Every_embedded_pack_loads_and_validates()
    {
        Catalog.All.Select(p => p.Jurisdiction).Should().BeEquivalentTo(
            new[] { "AZ", "CA", "CO", "FL", "IL", "MI", "NY", "OH", "OK", "TX", "US" });
        Catalog.All.Where(p => p.LineOfBusiness == LineOfBusiness.Medicaid).Should().HaveCount(10);
        Catalog.All.Should().ContainSingle(p => p.IsNational)
               .Which.LineOfBusiness.Should().Be(LineOfBusiness.Medicare);
        foreach (var pack in Catalog.All)
            RulePackValidator.Validate(pack).Should().BeEmpty(pack.Id);
    }

    [Fact]
    public void Find_is_case_insensitive_and_respects_effective_dates()
    {
        Catalog.Find("tx", LineOfBusiness.Medicaid, AsOf)!.Id.Should().Be("tx-medicaid");
        Catalog.Find("FL", LineOfBusiness.Medicaid, new DateOnly(2026, 6, 30)).Should().BeNull();
        Catalog.Find("WY", LineOfBusiness.Medicaid, AsOf).Should().BeNull();
    }

    [Fact]
    public void Two_versions_that_overlap_fail_the_catalog()
    {
        var a = Pack("TX");
        var b = Mutate("tx", n => { n["id"] = "tx-medicaid-2"; n["effectiveFrom"] = "2026-06-01"; });

        var act = () => new RulePackCatalog(new[] { a, b });

        act.Should().Throw<InvalidOperationException>().WithMessage("*overlaps*");
    }

    // ── Validator: nothing looser than the federal floor ────────────────

    [Fact]
    public void Validator_rejects_a_resolution_longer_than_30_days()
    {
        var pack = Mutate("tx", n => n["memberAppeal"]!["resolution"]!["amount"] = 31);
        RulePackValidator.Validate(pack).Should().Contain(e => e.Contains("resolution", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_rejects_working_days_that_can_run_past_the_ceiling()
    {
        // 22 working days exceeds 30 calendar days whenever a holiday falls inside.
        var pack = Mutate("co", n => n["memberAppeal"]!["resolution"]!["amount"] = 22);
        RulePackValidator.Validate(pack).Should().NotBeEmpty();
    }

    [Fact]
    public void Validator_rejects_a_fair_hearing_window_below_90_days()
    {
        var pack = Mutate("tx", n => n["memberAppeal"]!["fairHearingRequestWindow"]!["amount"] = 60);
        RulePackValidator.Validate(pack).Should().Contain(e => e.Contains("fairHearingRequestWindow"));
    }

    [Fact]
    public void Validator_rejects_a_clock_citing_an_unknown_source()
    {
        var pack = Mutate("tx", n => n["memberAppeal"]!["resolution"]!["source"] = "nowhere");
        RulePackValidator.Validate(pack).Should().Contain(e => e.Contains("nowhere"));
    }

    [Fact]
    public void Validator_rejects_an_extension_over_14_days()
    {
        var pack = Mutate("tx", n => n["memberAppeal"]!["extension"]!["maxDays"] = 15);
        RulePackValidator.Validate(pack).Should().NotBeEmpty();
    }

    [Fact]
    public void Parse_rejects_an_unknown_field()
    {
        var act = () => Mutate("tx", n => n["memberAppeal"]!["resolutionn"] = 1);
        act.Should().Throw<InvalidOperationException>();
    }

    // ── Evaluator ───────────────────────────────────────────────────────

    [Fact]
    public void Texas_standard_appeal_has_5_business_day_ack_and_30_day_resolution()
    {
        var e = Appeal("TX", Utc(2026, 11, 2));

        Deadline(e, "acknowledgment").DueAt.Should().Be(Utc(2026, 11, 9));
        var resolution = Deadline(e, "resolution");
        resolution.DueAt.Should().Be(Utc(2026, 12, 2));
        resolution.ClampedToFederalCeiling.Should().BeFalse();
        Deadline(e, "resolutionWithExtension").DueAt.Should().Be(Utc(2026, 12, 16));
        e.ToConfirm.Should().NotBeEmpty();
    }

    [Fact]
    public void Colorado_10_working_days_skip_a_federal_holiday()
    {
        // Wed 4 Nov 2026; Veterans Day is Wed 11 Nov.
        var e = Appeal("CO", Utc(2026, 11, 4));
        Deadline(e, "resolution").DueAt.Should().Be(Utc(2026, 11, 19));
        Deadline(e, "acknowledgment").DueAt.Should().Be(Utc(2026, 11, 6));
    }

    [Fact]
    public void Tenant_holidays_cannot_push_a_deadline_past_the_federal_ceiling()
    {
        var everyDayAHoliday = new CompositeHolidayCalendar(
            UsFederalHolidayCalendar.Instance,
            Enumerable.Range(0, 60).Select(i => new DateOnly(2026, 11, 5).AddDays(i)),
            Array.Empty<(int, int)>());

        var resolution = Deadline(Appeal("CO", Utc(2026, 11, 4), holidays: everyDayAHoliday), "resolution");

        resolution.ClampedToFederalCeiling.Should().BeTrue();
        resolution.DueAt.Should().Be(Utc(2026, 12, 4));
        resolution.Note.Should().Contain("federal ceiling");
    }

    [Fact]
    public void Illinois_expedited_runs_24_hours_from_information_but_never_past_72_hours_from_receipt()
    {
        var received = Utc(2026, 11, 2);

        Deadline(Appeal("IL", received, expedited: true, info: Utc(2026, 11, 3, 8)), "resolution")
            .DueAt.Should().Be(Utc(2026, 11, 4, 8));

        var late = Deadline(Appeal("IL", received, expedited: true, info: Utc(2026, 11, 4, 20)), "resolution");
        late.ClampedToFederalCeiling.Should().BeTrue();
        late.DueAt.Should().Be(Utc(2026, 11, 5));
    }

    [Fact]
    public void Illinois_has_no_State_appeal_acknowledgment_deadline()
    {
        var ack = Deadline(Appeal("IL", Utc(2026, 11, 2)), "acknowledgment");
        ack.DueAt.Should().BeNull();
        ack.Citation.Should().Be("42 CFR 438.406(b)(1)");
    }

    [Fact]
    public void Florida_expedited_is_48_hours()
    {
        Deadline(Appeal("FL", Utc(2026, 11, 2), expedited: true), "resolution").DueAt.Should().Be(Utc(2026, 11, 4));
    }

    [Fact]
    public void Ohio_resolves_in_15_days_and_extensions_need_State_approval()
    {
        var e = Appeal("OH", Utc(2026, 11, 2), notice: Utc(2026, 11, 2));
        Deadline(e, "resolution").DueAt.Should().Be(Utc(2026, 11, 17));
        Deadline(e, "resolutionWithExtension").Note.Should().Contain("State approval");
        Deadline(e, "continuationRequest").DueAt.Should().Be(Utc(2026, 11, 17));
    }

    [Fact]
    public void Arizona_fair_hearing_window_is_90_days_with_the_conflict_recorded()
    {
        var e = Appeal("AZ", Utc(2026, 11, 2), decided: Utc(2026, 11, 20));
        Deadline(e, "fairHearingRequest").DueAt.Should().Be(Utc(2027, 2, 18));
        Pack("AZ").Conflicts.Should().Contain(c => c.Item == "Fair hearing request window");
        Pack("AZ").MemberAppeal.OnMissed.Should().Be(MissedDeadlineOutcome.DeemedDenied);
    }

    [Fact]
    public void California_grievances_have_no_extension()
    {
        Pack("CA").MemberGrievance.Extension!.MaxDays.Should().Be(0);
        var e = RulePackEvaluator.Evaluate(Pack("CA"), new RulePackEvaluationInput
        {
            CaseType = RulePackCaseType.MemberGrievance,
            ReceivedAt = Utc(2026, 11, 2)
        });
        Deadline(e, "acknowledgment").DueAt.Should().Be(Utc(2026, 11, 7));
        Deadline(e, "resolution").DueAt.Should().Be(Utc(2026, 12, 2));
    }

    [Fact]
    public void Ohio_grievance_variants_are_each_evaluated()
    {
        var e = RulePackEvaluator.Evaluate(Pack("OH"), new RulePackEvaluationInput
        {
            CaseType = RulePackCaseType.MemberGrievance,
            ReceivedAt = Utc(2026, 11, 2)
        }, UsFederalHolidayCalendar.Instance);

        Deadline(e, "resolution:access to services").DueAt.Should().Be(Utc(2026, 11, 4));
        var claims = Deadline(e, "resolution:claims");
        claims.DueAt.Should().Be(Utc(2027, 1, 1));
        claims.ClampedToFederalCeiling.Should().BeFalse();
    }

    [Fact]
    public void Oklahoma_recoupment_hold_starts_on_1_January_2027()
    {
        var ok = Pack("OK");
        RulePackEvaluator.RecoupmentAsOf(ok, new DateOnly(2026, 12, 31))!.Hold.Should().Be(RecoupmentHold.Unknown);
        RulePackEvaluator.RecoupmentAsOf(ok, new DateOnly(2027, 1, 1))!.Hold.Should().Be(RecoupmentHold.Required);
    }

    [Fact]
    public void Local_times_are_rejected()
    {
        var act = () => Appeal("TX", new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Local));
        act.Should().Throw<ArgumentException>();
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static RulePack Mutate(string state, Action<JsonNode> change)
    {
        var resource = $"{RulePackCatalog.ResourcePrefix}medicaid.{state}.json";
        using var stream = typeof(RulePackCatalog).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing resource {resource}.");
        var node = JsonNode.Parse(stream, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })!;
        change(node);
        using var edited = new MemoryStream(Encoding.UTF8.GetBytes(node.ToJsonString()));
        return RulePackCatalog.Parse(edited, resource);
    }
}
