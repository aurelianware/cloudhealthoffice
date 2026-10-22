using CloudHealthOffice.ClinicalCriteria.Configuration;
using CloudHealthOffice.ClinicalCriteria.Domain;
using CloudHealthOffice.ClinicalCriteria.Providers;
using CloudHealthOffice.ClinicalCriteria.Services;
using CloudHealthOffice.PriorAuthRuleEngine.Abstractions;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CloudHealthOffice.ClinicalCriteria.Tests;

public class LocalRulesCriteriaProviderTests
{
    private static LocalRulesCriteriaProvider Build(PaRuleDecision decision)
    {
        var engine = Substitute.For<IPriorAuthRuleEngine>();
        engine.EvaluateAsync(Arg.Any<PaRuleContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(decision));
        return new LocalRulesCriteriaProvider(engine);
    }

    private static PaRuleDecision Decision(PaDecisionOutcome outcome) => new()
    {
        Outcome            = outcome,
        FiringRuleId       = "TX-PA-001",
        FiringRuleName     = "Chiropractic visit limit",
        ResolvedRuleSetKey = "platform/TX/Medicaid/STAR",
        DenialCode         = outcome == PaDecisionOutcome.Deny ? "A1" : null,
        DenialReason       = outcome == PaDecisionOutcome.Deny ? "Service is not a covered benefit." : null,
    };

    [Fact]
    public void It_is_always_configured_because_it_needs_no_licence()
    {
        Build(Decision(PaDecisionOutcome.Pend)).IsConfigured.Should().BeTrue();
    }

    [Fact]
    public async Task An_approving_rule_means_no_clinical_review_is_needed()
    {
        var evaluation = await Build(Decision(PaDecisionOutcome.Approve))
            .EvaluateAsync(TestRequests.Inpatient());

        evaluation.Recommendation.Should().Be(CriteriaRecommendation.MeetsCriteria);
        evaluation.RoutesToReview.Should().BeFalse();
        evaluation.Guideline!.GuidelineId.Should().Be("TX-PA-001");
        evaluation.Guideline.Version.Should().Be("platform/TX/Medicaid/STAR");
    }

    [Fact]
    public async Task A_denying_rule_is_a_coverage_answer_not_a_clinical_one()
    {
        var evaluation = await Build(Decision(PaDecisionOutcome.Deny))
            .EvaluateAsync(TestRequests.Inpatient());

        evaluation.Recommendation.Should().Be(
            CriteriaRecommendation.NotCovered,
            "coverage rules are not competent to make a medical-necessity determination");
        evaluation.Recommendation.Should().NotBe(CriteriaRecommendation.DoesNotMeetCriteria);
        evaluation.Rationale.Should().Contain(r => r.Contains("not a covered benefit"));
    }

    [Fact]
    public async Task A_pending_rule_asks_for_clinical_review()
    {
        var evaluation = await Build(Decision(PaDecisionOutcome.Pend))
            .EvaluateAsync(TestRequests.Inpatient());

        evaluation.Recommendation.Should().Be(CriteriaRecommendation.Indeterminate);
        evaluation.FailureReason.Should().Be("no-local-rule-conclusive");
        evaluation.OutstandingQuestions.Should().ContainSingle()
            .Which.Id.Should().Be("clinical-review");
    }

    [Fact]
    public async Task The_rule_engine_sees_the_requests_routing_and_clinical_identifiers()
    {
        var engine = Substitute.For<IPriorAuthRuleEngine>();
        engine.EvaluateAsync(Arg.Any<PaRuleContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Decision(PaDecisionOutcome.Pend)));

        await new LocalRulesCriteriaProvider(engine).EvaluateAsync(
            TestRequests.Inpatient(tenantId: "tenant-b", stateCode: "CA", lob: PaLineOfBusiness.Exchange));

        var context = (PaRuleContext)engine.ReceivedCalls().Single().GetArguments()[0]!;
        context.TenantId.Should().Be("tenant-b");
        context.StateCode.Should().Be("CA");
        context.Lob.Should().Be(PaLineOfBusiness.Exchange);
        context.ProcedureCodes.Should().ContainSingle().Which.Should().Be("99222");
        context.RequestedUnits.Should().Be(3);
    }
}

public class LicensedCriteriaProviderTests
{
    [Fact]
    public void A_licensed_provider_without_an_endpoint_or_credential_is_not_configured()
    {
        new InterQualCriteriaProvider(new LicensedCriteriaProviderOptions { Enabled = true })
            .IsConfigured.Should().BeFalse();

        new McgCriteriaProvider(new LicensedCriteriaProviderOptions
        {
            Enabled = true,
            BaseUrl = "https://example.invalid",
        }).IsConfigured.Should().BeFalse("a named credential is also required");
    }

    [Fact]
    public void A_fully_configured_licensed_provider_reports_itself_usable()
    {
        new InterQualCriteriaProvider(new LicensedCriteriaProviderOptions
        {
            Enabled = true,
            BaseUrl = "https://example.invalid",
            CredentialSecretName = "interqual-api-key",
        }).IsConfigured.Should().BeTrue();
    }

    [Fact]
    public async Task Configuring_a_licensed_provider_with_no_connector_fails_loudly()
    {
        var provider = new InterQualCriteriaProvider(new LicensedCriteriaProviderOptions
        {
            Enabled = true,
            BaseUrl = "https://example.invalid",
            CredentialSecretName = "interqual-api-key",
        });

        var thrown = await Assert.ThrowsAsync<ClinicalCriteriaConnectorMissingException>(
            () => provider.EvaluateAsync(TestRequests.Inpatient()));

        thrown.ProviderId.Should().Be("interqual");
    }

    [Fact]
    public async Task A_missing_connector_pends_the_request_rather_than_deciding_it()
    {
        var provider = new McgCriteriaProvider(new LicensedCriteriaProviderOptions
        {
            Enabled = true,
            BaseUrl = "https://example.invalid",
            CredentialSecretName = "mcg-api-key",
        });

        var service = new ClinicalCriteriaService(
            [provider],
            Options.Create(new ClinicalCriteriaOptions { DefaultProvider = "mcg" }),
            NullLogger<ClinicalCriteriaService>.Instance);

        var evaluation = await service.EvaluateAsync(TestRequests.Inpatient());

        evaluation.Recommendation.Should().Be(CriteriaRecommendation.Indeterminate);
        evaluation.FailureReason.Should().Be("provider-error:ClinicalCriteriaConnectorMissingException");
        evaluation.RoutesToReview.Should().BeTrue();
    }
}
