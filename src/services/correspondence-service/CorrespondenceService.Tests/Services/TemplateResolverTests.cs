using CorrespondenceService.Models;
using CorrespondenceService.Services;
using CorrespondenceService.Tests.Support;

namespace CorrespondenceService.Tests.Services;

public class TemplateResolverTests
{
    private static TemplateRequest Req(string? lob = "Medicaid", string? state = "TX", string? plan = null, DateOnly? on = null) =>
        new(Builders.LetterTypeCode, "en", TemplateFormat.Standard, on ?? Builders.Today, lob, state, null, plan);

    [Fact]
    public void Most_specific_scope_wins()
    {
        var general = Builders.Template("TPL-GEN");
        var texas = Builders.Template("TPL-TX", scope: new TemplateScope { State = "TX" });
        var texasMedicaid = Builders.Template("TPL-TX-MCD", scope: new TemplateScope { State = "TX", LineOfBusiness = "Medicaid" });

        TemplateResolver.Resolve([general, texas, texasMedicaid], Req()).Template!.TemplateId.Should().Be("TPL-TX-MCD");
        TemplateResolver.Resolve([general, texas, texasMedicaid], Req(lob: "Commercial")).Template!.TemplateId.Should().Be("TPL-TX");
        TemplateResolver.Resolve([general, texas, texasMedicaid], Req(state: "OK")).Template!.TemplateId.Should().Be("TPL-GEN");
    }

    [Fact]
    public void Plan_outranks_state_and_line_of_business()
    {
        var plan = Builders.Template("TPL-PLAN", scope: new TemplateScope { PlanId = "P1" });
        var stateLob = Builders.Template("TPL-SL", scope: new TemplateScope { State = "TX", LineOfBusiness = "Medicaid" });
        TemplateResolver.Resolve([plan, stateLob], Req(plan: "P1")).Template!.TemplateId.Should().Be("TPL-PLAN");
    }

    [Fact]
    public void Picks_the_version_effective_on_the_letter_date()
    {
        var v1 = Builders.Template(version: 1, from: new DateOnly(2026, 1, 1), to: new DateOnly(2026, 7, 1));
        var v2 = Builders.Template(version: 2, from: new DateOnly(2026, 7, 1));

        TemplateResolver.Resolve([v1, v2], Req(on: new DateOnly(2026, 3, 1))).Template!.Version.Should().Be(1);
        TemplateResolver.Resolve([v1, v2], Req(on: new DateOnly(2026, 7, 1))).Template!.Version.Should().Be(2);
    }

    [Fact]
    public void Latest_effective_version_of_one_template_does_not_count_as_a_tie()
    {
        var v1 = Builders.Template(version: 1, from: new DateOnly(2026, 1, 1));
        var v2 = Builders.Template(version: 2, from: new DateOnly(2026, 6, 1));
        TemplateResolver.Resolve([v1, v2], Req()).Template!.Version.Should().Be(2);
    }

    [Fact]
    public void Two_equally_specific_templates_fail_loudly()
    {
        var a = Builders.Template("TPL-A", scope: new TemplateScope { State = "TX" });
        var b = Builders.Template("TPL-B", scope: new TemplateScope { State = "TX" });
        var result = TemplateResolver.Resolve([a, b], Req());
        result.Template.Should().BeNull();
        result.Error.Should().Contain("same scope");
    }

    [Fact]
    public void Nothing_effective_fails_with_the_reason()
    {
        var future = Builders.Template(from: new DateOnly(2027, 1, 1));
        var draft = Builders.Template("TPL-D", status: TemplateStatus.Draft);
        var otherState = Builders.Template("TPL-OK", scope: new TemplateScope { State = "OK" });
        var result = TemplateResolver.Resolve([future, draft, otherState], Req());
        result.Template.Should().BeNull();
        result.Error.Should().Contain("No approved APPEAL_ACK template").And.Contain("2026-10-21").And.Contain("state TX");
    }
}
