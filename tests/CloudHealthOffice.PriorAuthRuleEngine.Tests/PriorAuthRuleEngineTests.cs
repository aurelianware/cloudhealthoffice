using CloudHealthOffice.PriorAuthRuleEngine.Abstractions;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using CloudHealthOffice.PriorAuthRuleEngine.Persistence;
using CloudHealthOffice.PriorAuthRuleEngine.Rules.Platform;
using CloudHealthOffice.PriorAuthRuleEngine.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CloudHealthOffice.PriorAuthRuleEngine.Tests;

// ═════════════════════════════════════════════════════════════════
//  Shared helpers
// ═════════════════════════════════════════════════════════════════

file static class Helpers
{
    public static PaRuleContext MakeContext(
        IReadOnlyList<string>? procedures = null,
        IReadOnlyList<string>? diagnoses = null,
        ProviderApprovalHistory? providerHistory = null,
        MemberAuthHistory? memberHistory = null,
        DateOnly? memberDob = null,
        int requestedUnits = 1) => new()
    {
        TenantId = "txmco01",
        StateCode = "TX",
        Lob = PaLineOfBusiness.Medicaid,
        Program = "STAR",
        RequestingProviderNpi = "1234567890",
        ServicingProviderNpi = "1234567890",
        MemberId = "MBR-001",
        ServiceDate = DateOnly.FromDateTime(DateTime.Today),
        ProcedureCodes = procedures ?? ["99213"],
        DiagnosisCodes = diagnoses ?? [],
        EstimatedCost = 200m,
        ProviderHistory = providerHistory,
        MemberHistory = memberHistory,
        MemberDateOfBirth = memberDob,
        RequestedUnits = requestedUnits
    };

    public static PaRuleDocument MakeGoldCardConfig(
        IReadOnlyList<string>? procedureCodes = null,
        decimal? approvalThreshold = null,
        int? minDecisions = null) => new()
    {
        RuleId = "TX-STAR-REG-001",
        RuleName = "Gold Card Exemption",
        StateCode = "TX",
        Lob = PaLineOfBusiness.Medicaid,
        Category = RuleCategory.RegulatoryExemption,
        Scope = RuleScope.Platform,
        Priority = 1,
        RuleType = "TxGoldCardExemption",
        ProcedureCodes = procedureCodes ?? [],
        GoldCardApprovalRateThreshold = approvalThreshold,
        GoldCardMinimumDecisions = minDecisions
    };

    public static PaRuleDocument MakeQuantityLimitConfig(
        int? visitLimit = null,
        int? unitLimit = null) => new()
    {
        RuleId = "TX-STAR-QTY-001",
        RuleName = "Visit Limit",
        StateCode = "TX",
        Lob = PaLineOfBusiness.Medicaid,
        Category = RuleCategory.QuantityLimit,
        Scope = RuleScope.Platform,
        Priority = 20,
        RuleType = "QuantityLimit",
        ProcedureCodes = ["99213"],
        VisitLimit = visitLimit,
        UnitLimit = unitLimit
    };

    public static PaRuleDocument MakeDiagnosisConfig(
        IReadOnlyList<string> requiredDx) => new()
    {
        RuleId = "TX-STAR-DX-001",
        RuleName = "Diagnosis Required",
        StateCode = "TX",
        Lob = PaLineOfBusiness.Medicaid,
        Category = RuleCategory.DiagnosisRequired,
        Scope = RuleScope.Platform,
        Priority = 30,
        RuleType = "DiagnosisRequired",
        ProcedureCodes = ["99213"],
        RequiredDiagnosisCodes = requiredDx
    };

    public static PaRuleDocument MakeAgeLimitConfig(int? maxAge = null) => new()
    {
        RuleId = "TX-STARKIDS-AGE-001",
        RuleName = "EPSDT Under-21",
        StateCode = "TX",
        Lob = PaLineOfBusiness.Medicaid,
        Category = RuleCategory.MemberAge,
        Scope = RuleScope.Platform,
        Priority = 50,
        RuleType = "MemberAgeLimit",
        MaxMemberAgeYears = maxAge
    };

    public static ProviderApprovalHistory MakeProviderHistory(
        int total, int approved) => new()
    {
        Npi = "1234567890",
        LookbackDays = 180,
        TotalDecisions = total,
        ApprovedDecisions = approved
    };

    public static MemberAuthHistory MakeMemberHistory(
        int visits, int units = 0) => new()
    {
        MemberId = "MBR-001",
        BenefitPeriod = "2026",
        ProcedureCodes = ["99213"],
        AuthorisedVisits = visits,
        AuthorisedUnits = units,
        AuthorisedAmount = 0m
    };
}

// ═════════════════════════════════════════════════════════════════
//  TxGoldCardExemptionRule (tests 1-5)
// ═════════════════════════════════════════════════════════════════

public class TxGoldCardExemptionRuleTests
{
    private readonly TxGoldCardExemptionRule _rule = new();

    [Fact]
    public async Task Evaluate_NoProviderHistory_ReturnsNull()
    {
        var result = await _rule.EvaluateAsync(
            Helpers.MakeGoldCardConfig(),
            Helpers.MakeContext(providerHistory: null));

        result.Should().BeNull();
    }

    [Fact]
    public async Task Evaluate_InsufficientDecisions_BelowMinimum_ReturnsNull()
    {
        var result = await _rule.EvaluateAsync(
            Helpers.MakeGoldCardConfig(minDecisions: 20),
            Helpers.MakeContext(providerHistory: Helpers.MakeProviderHistory(total: 15, approved: 14)));

        result.Should().BeNull();
    }

    [Fact]
    public async Task Evaluate_ApprovalRateBelowThreshold_ReturnsNull()
    {
        // 85% < 90% threshold
        var result = await _rule.EvaluateAsync(
            Helpers.MakeGoldCardConfig(approvalThreshold: 0.90m),
            Helpers.MakeContext(providerHistory: Helpers.MakeProviderHistory(total: 100, approved: 85)));

        result.Should().BeNull();
    }

    [Fact]
    public async Task Evaluate_QualifyingHistory_ReturnsApprove()
    {
        // 92% ≥ 90%, 25 ≥ 20
        var result = await _rule.EvaluateAsync(
            Helpers.MakeGoldCardConfig(approvalThreshold: 0.90m, minDecisions: 20),
            Helpers.MakeContext(providerHistory: Helpers.MakeProviderHistory(total: 25, approved: 23)));

        result.Should().NotBeNull();
        result!.Outcome.Should().Be(PaDecisionOutcome.Approve);
    }

    [Fact]
    public async Task Evaluate_ProcedureNotInScope_ReturnsNull()
    {
        var config = Helpers.MakeGoldCardConfig(procedureCodes: ["99213"]);
        var context = Helpers.MakeContext(
            procedures: ["27447"],
            providerHistory: Helpers.MakeProviderHistory(total: 25, approved: 23));

        var result = await _rule.EvaluateAsync(config, context);

        result.Should().BeNull();
    }
}

// ═════════════════════════════════════════════════════════════════
//  QuantityLimitRule (tests 6-9)
// ═════════════════════════════════════════════════════════════════

public class QuantityLimitRuleTests
{
    private readonly QuantityLimitRule _rule = new();

    [Fact]
    public async Task Evaluate_WithinVisitLimit_ReturnsApprove()
    {
        var result = await _rule.EvaluateAsync(
            Helpers.MakeQuantityLimitConfig(visitLimit: 20),
            Helpers.MakeContext(memberHistory: Helpers.MakeMemberHistory(visits: 18)));

        result.Should().NotBeNull();
        result!.Outcome.Should().Be(PaDecisionOutcome.Approve);
    }

    [Fact]
    public async Task Evaluate_AtVisitLimit_ReturnsPend()
    {
        // 20 + 1 (current request) = 21 > 20 limit
        var result = await _rule.EvaluateAsync(
            Helpers.MakeQuantityLimitConfig(visitLimit: 20),
            Helpers.MakeContext(memberHistory: Helpers.MakeMemberHistory(visits: 20)));

        result.Should().NotBeNull();
        result!.Outcome.Should().Be(PaDecisionOutcome.Pend);
    }

    [Fact]
    public async Task Evaluate_NoMemberHistory_AndLimitConfigured_ReturnsPend()
    {
        var result = await _rule.EvaluateAsync(
            Helpers.MakeQuantityLimitConfig(visitLimit: 20),
            Helpers.MakeContext(memberHistory: null));

        result.Should().NotBeNull();
        result!.Outcome.Should().Be(PaDecisionOutcome.Pend);
    }

    [Fact]
    public async Task Evaluate_NeitherLimitConfigured_ReturnsNull()
    {
        var result = await _rule.EvaluateAsync(
            Helpers.MakeQuantityLimitConfig(visitLimit: null, unitLimit: null),
            Helpers.MakeContext(memberHistory: Helpers.MakeMemberHistory(visits: 5)));

        result.Should().BeNull();
    }
}

// ═════════════════════════════════════════════════════════════════
//  DiagnosisRequiredRule (tests 10-11)
// ═════════════════════════════════════════════════════════════════

public class DiagnosisRequiredRuleTests
{
    private readonly DiagnosisRequiredRule _rule = new();

    [Fact]
    public async Task Evaluate_RequiredDxPresent_ReturnsPend()
    {
        var result = await _rule.EvaluateAsync(
            Helpers.MakeDiagnosisConfig(["G47.33"]),
            Helpers.MakeContext(diagnoses: ["G47.33", "J06.9"]));

        result.Should().NotBeNull();
        result!.Outcome.Should().Be(PaDecisionOutcome.Pend);
    }

    [Fact]
    public async Task Evaluate_RequiredDxAbsent_ReturnsApprove()
    {
        var result = await _rule.EvaluateAsync(
            Helpers.MakeDiagnosisConfig(["G47.33"]),
            Helpers.MakeContext(diagnoses: ["J06.9"]));

        result.Should().NotBeNull();
        result!.Outcome.Should().Be(PaDecisionOutcome.Approve);
    }
}

// ═════════════════════════════════════════════════════════════════
//  MemberAgeLimitRule (tests 12-15)
// ═════════════════════════════════════════════════════════════════

public class MemberAgeLimitRuleTests
{
    private readonly MemberAgeLimitRule _rule = new();
    private readonly DateOnly _today = DateOnly.FromDateTime(DateTime.Today);

    [Fact]
    public async Task Evaluate_MemberUnder21_MaxAge21Configured_ReturnsApprove()
    {
        var dob = _today.AddYears(-18);
        var result = await _rule.EvaluateAsync(
            Helpers.MakeAgeLimitConfig(maxAge: 21),
            Helpers.MakeContext(memberDob: dob));

        result.Should().NotBeNull();
        result!.Outcome.Should().Be(PaDecisionOutcome.Approve);
    }

    [Fact]
    public async Task Evaluate_MemberExactly21_MaxAge21_ReturnsApprove()
    {
        var dob = _today.AddYears(-21);
        var result = await _rule.EvaluateAsync(
            Helpers.MakeAgeLimitConfig(maxAge: 21),
            Helpers.MakeContext(memberDob: dob));

        result.Should().NotBeNull();
        result!.Outcome.Should().Be(PaDecisionOutcome.Approve);
    }

    [Fact]
    public async Task Evaluate_MemberOver21_MaxAge21_ReturnsNull()
    {
        var dob = _today.AddYears(-22);
        var result = await _rule.EvaluateAsync(
            Helpers.MakeAgeLimitConfig(maxAge: 21),
            Helpers.MakeContext(memberDob: dob));

        result.Should().BeNull();
    }

    [Fact]
    public async Task Evaluate_NoDobInContext_ReturnsNull()
    {
        var result = await _rule.EvaluateAsync(
            Helpers.MakeAgeLimitConfig(maxAge: 21),
            Helpers.MakeContext(memberDob: null));

        result.Should().BeNull();
    }
}

// ═════════════════════════════════════════════════════════════════
//  PriorAuthRuleEngineService (tests 16-20)
// ═════════════════════════════════════════════════════════════════

public class PriorAuthRuleEngineServiceTests
{
    private static PaRuleContext DefaultContext => Helpers.MakeContext();

    private static PriorAuthRuleEngineService BuildEngine(
        IPaRuleRepository repo,
        IEnumerable<IPaRule>? rules = null,
        PriorAuthRuleEngineOptions? opts = null)
    {
        return new PriorAuthRuleEngineService(
            repo,
            rules ?? [],
            Options.Create(opts ?? new PriorAuthRuleEngineOptions()),
            Substitute.For<ILogger<PriorAuthRuleEngineService>>());
    }

    [Fact]
    public async Task EvaluateAsync_NoRulesFound_ReturnsPend_NoRulesConfigured()
    {
        var repo = Substitute.For<IPaRuleRepository>();
        repo.GetRulesAsync(Arg.Any<RuleSetKey>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PaRuleDocument>());

        var engine = BuildEngine(repo);

        var result = await engine.EvaluateAsync(DefaultContext);

        result.Outcome.Should().Be(PaDecisionOutcome.Pend);
        result.FiringRuleId.Should().Be("NoRulesConfigured");
    }

    [Fact]
    public async Task EvaluateAsync_FirstRuleApproves_ShortCircuits_DoesNotEvaluateRemaining()
    {
        var doc1 = Helpers.MakeGoldCardConfig();        // Priority = 1
        var doc2 = Helpers.MakeQuantityLimitConfig(visitLimit: 20); // Priority = 20

        var repo = Substitute.For<IPaRuleRepository>();
        repo.GetRulesAsync(Arg.Any<RuleSetKey>(), Arg.Any<CancellationToken>())
            .Returns(new[] { doc1, doc2 });

        var rule1 = Substitute.For<IPaRule>();
        rule1.RuleType.Returns("TxGoldCardExemption");
        rule1.EvaluateAsync(doc1, Arg.Any<PaRuleContext>(), Arg.Any<CancellationToken>())
            .Returns(new PaRuleDecision
            {
                Outcome = PaDecisionOutcome.Approve,
                FiringRuleId = doc1.RuleId,
                FiringRuleName = doc1.RuleName,
                ResolvedRuleSetKey = "platform/TX/Medicaid/STAR"
            });

        var rule2 = Substitute.For<IPaRule>();
        rule2.RuleType.Returns("QuantityLimit");

        var engine = BuildEngine(repo, [rule1, rule2]);

        var result = await engine.EvaluateAsync(DefaultContext);

        result.Outcome.Should().Be(PaDecisionOutcome.Approve);
        await rule2.DidNotReceive()
            .EvaluateAsync(Arg.Any<PaRuleDocument>(), Arg.Any<PaRuleContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateAsync_RegulatoryExemptionRunsBeforeClinical()
    {
        // Gold card (RegulatoryExemption, Priority=1) should fire before
        // ProcedureRequiresAuth (ClinicalCriteria, Priority=10)
        var goldCardDoc = Helpers.MakeGoldCardConfig();
        var clinicalDoc = new PaRuleDocument
        {
            RuleId = "TX-STAR-PA-001", RuleName = "Inpatient PA",
            StateCode = "TX", Lob = PaLineOfBusiness.Medicaid,
            Category = RuleCategory.ClinicalCriteria,
            Scope = RuleScope.Platform, Priority = 10,
            RuleType = "ProcedureRequiresAuth"
        };

        var repo = Substitute.For<IPaRuleRepository>();
        repo.GetRulesAsync(Arg.Any<RuleSetKey>(), Arg.Any<CancellationToken>())
            .Returns(new[] { clinicalDoc, goldCardDoc }); // deliberately out of order

        var goldCardRule = Substitute.For<IPaRule>();
        goldCardRule.RuleType.Returns("TxGoldCardExemption");
        goldCardRule.EvaluateAsync(goldCardDoc, Arg.Any<PaRuleContext>(), Arg.Any<CancellationToken>())
            .Returns(new PaRuleDecision
            {
                Outcome = PaDecisionOutcome.Approve,
                FiringRuleId = goldCardDoc.RuleId,
                FiringRuleName = goldCardDoc.RuleName,
                ResolvedRuleSetKey = "platform/TX/Medicaid/STAR"
            });

        var clinicalRule = Substitute.For<IPaRule>();
        clinicalRule.RuleType.Returns("ProcedureRequiresAuth");

        var engine = BuildEngine(repo, [goldCardRule, clinicalRule]);

        var result = await engine.EvaluateAsync(DefaultContext);

        result.Outcome.Should().Be(PaDecisionOutcome.Approve);
        result.FiringRuleId.Should().Be("TX-STAR-REG-001");
        await clinicalRule.DidNotReceive()
            .EvaluateAsync(Arg.Any<PaRuleDocument>(), Arg.Any<PaRuleContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateAsync_RuleThrows_PendOnRuleError_True_ReturnsPend()
    {
        var doc = Helpers.MakeGoldCardConfig();

        var repo = Substitute.For<IPaRuleRepository>();
        repo.GetRulesAsync(Arg.Any<RuleSetKey>(), Arg.Any<CancellationToken>())
            .Returns(new[] { doc });

        var rule = Substitute.For<IPaRule>();
        rule.RuleType.Returns("TxGoldCardExemption");
        rule.EvaluateAsync(doc, Arg.Any<PaRuleContext>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Simulated failure"));

        var engine = BuildEngine(repo, [rule], new PriorAuthRuleEngineOptions { PendOnRuleError = true });

        var result = await engine.EvaluateAsync(DefaultContext);

        result.Outcome.Should().Be(PaDecisionOutcome.Pend);
        result.FiringRuleId.Should().Contain("RuleError");
    }

    [Fact]
    public async Task EvaluateAsync_TenantRuleBeforePlatformRule_SamePriority()
    {
        var tenantDoc = new PaRuleDocument
        {
            RuleId = "TXMCO01-STAR-PA-001", RuleName = "Tenant PA Rule",
            StateCode = "TX", Lob = PaLineOfBusiness.Medicaid,
            Category = RuleCategory.ClinicalCriteria,
            Scope = RuleScope.Tenant, TenantId = "txmco01",
            Priority = 10, RuleType = "ProcedureRequiresAuth"
        };
        var platformDoc = new PaRuleDocument
        {
            RuleId = "TX-STAR-PA-001", RuleName = "Platform PA Rule",
            StateCode = "TX", Lob = PaLineOfBusiness.Medicaid,
            Category = RuleCategory.ClinicalCriteria,
            Scope = RuleScope.Platform,
            Priority = 10, RuleType = "ProcedureRequiresAuth"
        };

        var repo = Substitute.For<IPaRuleRepository>();
        // Return platform first to prove the engine re-sorts
        repo.GetRulesAsync(Arg.Any<RuleSetKey>(), Arg.Any<CancellationToken>())
            .Returns(new[] { platformDoc, tenantDoc });

        var rule = Substitute.For<IPaRule>();
        rule.RuleType.Returns("ProcedureRequiresAuth");
        // Return Pend for tenant doc, null for platform doc
        rule.EvaluateAsync(tenantDoc, Arg.Any<PaRuleContext>(), Arg.Any<CancellationToken>())
            .Returns(new PaRuleDecision
            {
                Outcome = PaDecisionOutcome.Pend,
                FiringRuleId = tenantDoc.RuleId,
                FiringRuleName = tenantDoc.RuleName,
                ResolvedRuleSetKey = "txmco01/TX/Medicaid/any"
            });
        rule.EvaluateAsync(platformDoc, Arg.Any<PaRuleContext>(), Arg.Any<CancellationToken>())
            .Returns((PaRuleDecision?)null);

        var engine = BuildEngine(repo, [rule]);

        var result = await engine.EvaluateAsync(DefaultContext);

        result.Outcome.Should().Be(PaDecisionOutcome.Pend);
        result.FiringRuleId.Should().Be("TXMCO01-STAR-PA-001", "tenant rule should fire before platform rule at same priority");
    }
}
