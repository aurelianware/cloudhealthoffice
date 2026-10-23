using CloudHealthOffice.PriorAuthRuleEngine.Abstractions;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using CloudHealthOffice.PriorAuthRuleEngine.Persistence;
using CloudHealthOffice.PriorAuthRuleEngine.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CloudHealthOffice.PriorAuthRuleEngine.Tests;

/// <summary>
/// What a decision records about the rule set that made it
/// (docs/architecture/plan-configuration.md §12.6).
///
/// <para>
/// It used to be <c>tenant/TX/Medicaid/STAR</c>: a string that looks like an
/// identifier without being one, built out of four values, pointing at no
/// record. A rule set written against a program now names that program's id,
/// which can be looked up — and a rule set that is not one program's still
/// says what it is, so the fallback from a program's rules to the state's
/// stays visible in the audit trail rather than being dressed up as the
/// program's own.
/// </para>
/// </summary>
public class ResolvedRuleSetKeyTests
{
    private const string ProgramId = "txmco01|DEFAULT|STAR";

    private static PaRuleContext Context(string? programId = ProgramId, string? program = "STAR") => new()
    {
        TenantId = "txmco01",
        StateCode = "TX",
        Lob = PaLineOfBusiness.Medicaid,
        Program = program,
        ProgramId = programId,
        RequestingProviderNpi = "1234567890",
        ServicingProviderNpi = "1234567890",
        MemberId = "MBR-001",
        ServiceDate = DateOnly.FromDateTime(DateTime.Today),
        ProcedureCodes = ["99213"],
        DiagnosisCodes = [],
        EstimatedCost = 200m,
    };

    private static PaRuleDocument Doc(string? program = "STAR", string? tenantId = null) => new()
    {
        RuleId = "TX-STAR-PA-001",
        RuleName = "Inpatient PA",
        StateCode = "TX",
        Lob = PaLineOfBusiness.Medicaid,
        Program = program,
        TenantId = tenantId,
        Category = RuleCategory.ClinicalCriteria,
        Scope = tenantId is null ? RuleScope.Platform : RuleScope.Tenant,
        Priority = 10,
        RuleType = "ProcedureRequiresAuth",
    };

    /// <summary>A rule that always denies, naming its own document's key as rules do.</summary>
    private static IPaRule Denying(PaRuleDocument doc)
    {
        var rule = Substitute.For<IPaRule>();
        rule.RuleType.Returns("ProcedureRequiresAuth");
        rule.EvaluateAsync(Arg.Any<PaRuleDocument>(), Arg.Any<PaRuleContext>(), Arg.Any<CancellationToken>())
            .Returns(new PaRuleDecision
            {
                Outcome = PaDecisionOutcome.Deny,
                FiringRuleId = doc.RuleId,
                FiringRuleName = doc.RuleName,
                ResolvedRuleSetKey = "whatever the rule built for itself",
            });
        return rule;
    }

    private static PriorAuthRuleEngineService Engine(IPaRuleRepository repo, IEnumerable<IPaRule>? rules = null) =>
        new(repo, rules ?? [], Options.Create(new PriorAuthRuleEngineOptions()),
            Substitute.For<ILogger<PriorAuthRuleEngineService>>());

    [Fact]
    public async Task A_programs_rule_set_is_named_by_the_programs_id()
    {
        var doc = Doc();
        var repo = Substitute.For<IPaRuleRepository>();
        repo.GetRulesAsync(Arg.Any<RuleSetKey>(), Arg.Any<CancellationToken>()).Returns(new[] { doc });

        var decision = await Engine(repo, [Denying(doc)]).EvaluateAsync(Context());

        decision.Outcome.Should().Be(PaDecisionOutcome.Deny);
        // Not the key the rule built from its own document: the rule knows its
        // document, the engine knows which rule set it loaded.
        decision.ResolvedRuleSetKey.Should().Be(ProgramId);
    }

    [Fact]
    public async Task Falling_back_to_the_states_rules_is_not_recorded_as_the_programs_own()
    {
        var doc = Doc(program: null);
        var repo = Substitute.For<IPaRuleRepository>();
        // Nothing written against the program; the state-and-line-of-business
        // rule set answers instead.
        repo.GetRulesAsync(Arg.Is<RuleSetKey>(k => k.Program != null), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PaRuleDocument>());
        repo.GetRulesAsync(Arg.Is<RuleSetKey>(k => k.Program == null), Arg.Any<CancellationToken>())
            .Returns(new[] { doc });

        var decision = await Engine(repo, [Denying(doc)]).EvaluateAsync(Context());

        // The tenant's own state-wide rules, which is the third candidate: the
        // program's rules, tenant and platform, were both empty.
        decision.ResolvedRuleSetKey.Should().Be("txmco01/TX/Medicaid/any",
            "a member on a program decided by the state's rules is exactly what an auditor needs to see");
    }

    [Fact]
    public async Task A_member_with_no_program_is_still_told_what_decided()
    {
        var doc = Doc(program: null);
        var repo = Substitute.For<IPaRuleRepository>();
        repo.GetRulesAsync(Arg.Any<RuleSetKey>(), Arg.Any<CancellationToken>()).Returns(new[] { doc });

        var decision = await Engine(repo, [Denying(doc)]).EvaluateAsync(Context(programId: null, program: null));

        decision.ResolvedRuleSetKey.Should().Be("txmco01/TX/Medicaid/any");
    }

    [Fact]
    public async Task A_pend_for_want_of_a_rule_set_names_what_was_looked_for_not_the_program()
    {
        var repo = Substitute.For<IPaRuleRepository>();
        repo.GetRulesAsync(Arg.Any<RuleSetKey>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<PaRuleDocument>());

        var decision = await Engine(repo).EvaluateAsync(Context());

        decision.FiringRuleId.Should().Be("NoRulesConfigured");
        // The program id here would read as "this program's rules pended it",
        // and none of that program's rules were loaded.
        decision.ResolvedRuleSetKey.Should().Be("txmco01/TX/Medicaid/STAR");
    }

    [Fact]
    public async Task A_pend_because_no_rule_matched_names_the_rule_set_that_was_loaded()
    {
        var doc = Doc();
        var repo = Substitute.For<IPaRuleRepository>();
        repo.GetRulesAsync(Arg.Any<RuleSetKey>(), Arg.Any<CancellationToken>()).Returns(new[] { doc });

        var silent = Substitute.For<IPaRule>();
        silent.RuleType.Returns("ProcedureRequiresAuth");
        silent.EvaluateAsync(Arg.Any<PaRuleDocument>(), Arg.Any<PaRuleContext>(), Arg.Any<CancellationToken>())
            .Returns((PaRuleDecision?)null);

        var decision = await Engine(repo, [silent]).EvaluateAsync(Context());

        decision.FiringRuleId.Should().Be("NoRuleMatch");
        decision.ResolvedRuleSetKey.Should().Be(ProgramId);
    }

    [Fact]
    public void The_program_id_names_the_rule_set_without_becoming_part_of_its_lookup()
    {
        var key = new RuleSetKey { StateCode = "TX", Lob = PaLineOfBusiness.Medicaid, Program = "STAR", TenantId = "txmco01", ProgramId = ProgramId };

        key.AuditKey.Should().Be(ProgramId);
        // The four fields are what rules are stored and cached under, and the
        // engine's own candidate de-duplication runs on this string. If the
        // program id reached it, a tenant's rule set and the platform's would
        // look like one and the platform fallback would never be tried.
        key.ToString().Should().Be("txmco01/TX/Medicaid/STAR");
        (key with { TenantId = null }).ToString().Should().Be("platform/TX/Medicaid/STAR");
    }

    [Fact]
    public void A_rule_set_that_belongs_to_no_program_is_named_by_what_it_covers()
    {
        var key = new RuleSetKey { StateCode = "TX", Lob = PaLineOfBusiness.Medicaid };

        key.AuditKey.Should().Be("platform/TX/Medicaid/any").And.Be(key.ToString());
    }
}
