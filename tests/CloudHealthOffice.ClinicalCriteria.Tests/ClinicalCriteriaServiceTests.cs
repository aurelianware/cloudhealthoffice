using System.Diagnostics;
using CloudHealthOffice.ClinicalCriteria.Abstractions;
using CloudHealthOffice.ClinicalCriteria.Configuration;
using CloudHealthOffice.ClinicalCriteria.Domain;
using CloudHealthOffice.ClinicalCriteria.Models;
using CloudHealthOffice.ClinicalCriteria.Services;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.ClinicalCriteria.Tests;

public class ClinicalCriteriaServiceTests
{
    private static ClinicalCriteriaService Build(
        ClinicalCriteriaOptions options, params IClinicalCriteriaProvider[] providers) =>
        new(providers, Options.Create(options), NullLogger<ClinicalCriteriaService>.Instance);

    // ── Fail-open ─────────────────────────────────────────────────

    [Fact]
    public async Task A_provider_that_is_routed_to_but_not_registered_pends()
    {
        var service = Build(new ClinicalCriteriaOptions { DefaultProvider = "interqual" });

        var evaluation = await service.EvaluateAsync(TestRequests.Inpatient());

        evaluation.Recommendation.Should().Be(CriteriaRecommendation.Indeterminate);
        evaluation.FailureReason.Should().Be("provider-not-registered");
        evaluation.RoutesToReview.Should().BeTrue();
    }

    [Fact]
    public async Task An_unconfigured_provider_is_not_called()
    {
        var provider = new StubProvider("interqual") { IsConfiguredValue = false };
        var service = Build(new ClinicalCriteriaOptions { DefaultProvider = "interqual" }, provider);

        var evaluation = await service.EvaluateAsync(TestRequests.Inpatient());

        evaluation.FailureReason.Should().Be("not-configured");
        provider.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_provider_that_throws_pends_rather_than_denying()
    {
        var provider = new StubProvider("interqual")
        {
            Behaviour = _ => throw new HttpRequestException("vendor down"),
        };
        var service = Build(new ClinicalCriteriaOptions { DefaultProvider = "interqual" }, provider);

        var evaluation = await service.EvaluateAsync(TestRequests.Inpatient());

        evaluation.Recommendation.Should().Be(CriteriaRecommendation.Indeterminate);
        evaluation.FailureReason.Should().Be("provider-error:HttpRequestException");
    }

    [Fact]
    public async Task A_provider_slower_than_the_timeout_pends()
    {
        var provider = new StubProvider("interqual")
        {
            BehaviourAsync = async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                throw new UnreachableException();
            },
        };
        var service = Build(
            new ClinicalCriteriaOptions
            {
                DefaultProvider = "interqual",
                EvaluationTimeout = TimeSpan.FromMilliseconds(50),
            },
            provider);

        var evaluation = await service.EvaluateAsync(TestRequests.Inpatient());

        evaluation.Recommendation.Should().Be(CriteriaRecommendation.Indeterminate);
        evaluation.FailureReason.Should().Be("timeout");
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_swallowed_as_a_timeout()
    {
        var provider = new StubProvider("interqual")
        {
            BehaviourAsync = async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                throw new UnreachableException();
            },
        };
        var service = Build(new ClinicalCriteriaOptions { DefaultProvider = "interqual" }, provider);

        using var caller = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.EvaluateAsync(TestRequests.Inpatient(), caller.Token));
    }

    [Fact]
    public async Task A_provider_returning_nothing_pends()
    {
        var provider = new StubProvider("interqual") { Behaviour = _ => null! };
        var service = Build(new ClinicalCriteriaOptions { DefaultProvider = "interqual" }, provider);

        var evaluation = await service.EvaluateAsync(TestRequests.Inpatient());

        evaluation.FailureReason.Should().Be("provider-returned-null");
    }

    // ── Normalization ─────────────────────────────────────────────

    [Fact]
    public async Task An_adverse_finding_always_requires_physician_review()
    {
        var provider = new StubProvider("interqual")
        {
            Behaviour = _ => new CriteriaEvaluation
            {
                ProviderId = "something-else",
                Recommendation = CriteriaRecommendation.DoesNotMeetCriteria,
                RequiresPhysicianReview = false,
            },
        };
        var service = Build(new ClinicalCriteriaOptions { DefaultProvider = "interqual" }, provider);

        var evaluation = await service.EvaluateAsync(TestRequests.Inpatient());

        evaluation.RequiresPhysicianReview.Should().BeTrue();
        evaluation.RoutesToReview.Should().BeTrue();
        evaluation.ProviderId.Should().Be("interqual", "the evaluation names the provider that produced it");
    }

    [Fact]
    public async Task A_conclusive_evaluation_carries_no_failure_reason()
    {
        var provider = new StubProvider("interqual")
        {
            Behaviour = _ => new CriteriaEvaluation
            {
                ProviderId = "interqual",
                Recommendation = CriteriaRecommendation.MeetsCriteria,
                FailureReason = "left over from somewhere",
            },
        };
        var service = Build(new ClinicalCriteriaOptions { DefaultProvider = "interqual" }, provider);

        var evaluation = await service.EvaluateAsync(TestRequests.Inpatient());

        evaluation.FailureReason.Should().BeNull();
        evaluation.RoutesToReview.Should().BeFalse();
    }

    [Fact]
    public async Task Every_evaluation_is_stamped_with_a_time()
    {
        var provider = new StubProvider("cho-local")
        {
            Behaviour = _ => new CriteriaEvaluation
            {
                ProviderId = "cho-local",
                Recommendation = CriteriaRecommendation.MeetsCriteria,
            },
        };
        var service = Build(new ClinicalCriteriaOptions(), provider);

        var evaluation = await service.EvaluateAsync(TestRequests.Inpatient());

        evaluation.EvaluatedAtUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    // ── Routing ───────────────────────────────────────────────────

    [Fact]
    public async Task With_no_matching_rule_the_default_provider_answers()
    {
        var local = new StubProvider("cho-local");
        var interQual = new StubProvider("interqual");
        var options = new ClinicalCriteriaOptions
        {
            Routing = [new CriteriaRoutingRule { Lob = PaLineOfBusiness.Medicare, Provider = "interqual" }],
        };

        await Build(options, local, interQual).EvaluateAsync(
            TestRequests.Inpatient(lob: PaLineOfBusiness.Medicaid));

        local.Calls.Should().Be(1);
        interQual.Calls.Should().Be(0);
    }

    [Fact]
    public async Task The_most_specific_matching_rule_wins()
    {
        var interQual = new StubProvider("interqual");
        var mcg = new StubProvider("mcg");
        var options = new ClinicalCriteriaOptions
        {
            Routing =
            [
                new CriteriaRoutingRule { Lob = PaLineOfBusiness.Medicare, Provider = "interqual" },
                new CriteriaRoutingRule
                {
                    Lob = PaLineOfBusiness.Medicare,
                    ReviewType = CriteriaReviewType.Concurrent,
                    TenantId = "tenant-a",
                    Provider = "mcg",
                },
            ],
        };

        await Build(options, interQual, mcg).EvaluateAsync(TestRequests.Inpatient(
            lob: PaLineOfBusiness.Medicare, reviewType: CriteriaReviewType.Concurrent));

        mcg.Calls.Should().Be(1);
        interQual.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_rule_matches_only_when_every_field_it_sets_matches()
    {
        var interQual = new StubProvider("interqual");
        var local = new StubProvider("cho-local");
        var options = new ClinicalCriteriaOptions
        {
            Routing =
            [
                new CriteriaRoutingRule
                {
                    Lob = PaLineOfBusiness.Medicare,
                    ReviewType = CriteriaReviewType.Concurrent,
                    Provider = "interqual",
                },
            ],
        };

        // Right LOB, wrong review type.
        await Build(options, interQual, local).EvaluateAsync(TestRequests.Inpatient(
            lob: PaLineOfBusiness.Medicare, reviewType: CriteriaReviewType.Prospective));

        interQual.Calls.Should().Be(0);
        local.Calls.Should().Be(1);
    }

    [Fact]
    public void Provider_status_reports_what_this_deployment_can_actually_do()
    {
        var service = Build(
            new ClinicalCriteriaOptions { DefaultProvider = "cho-local" },
            new StubProvider("cho-local"),
            new StubProvider("interqual") { IsConfiguredValue = false });

        var status = service.GetProviderStatus();

        status.Should().HaveCount(2);
        status.Single(s => s.ProviderId == "cho-local").IsDefault.Should().BeTrue();
        status.Single(s => s.ProviderId == "cho-local").IsConfigured.Should().BeTrue();
        status.Single(s => s.ProviderId == "interqual").IsConfigured.Should().BeFalse();
    }

    private sealed class StubProvider(string id) : IClinicalCriteriaProvider
    {
        public string ProviderId => id;
        public string DisplayName => id;
        public bool IsConfiguredValue { get; set; } = true;
        public bool IsConfigured => IsConfiguredValue;
        public int Calls { get; private set; }

        /// <summary>Set to return something specific — including null, which the service must survive.</summary>
        public Func<CriteriaRequest, CriteriaEvaluation?>? Behaviour { get; set; }
        public Func<CancellationToken, Task<CriteriaEvaluation>>? BehaviourAsync { get; set; }

        public Task<CriteriaEvaluation> EvaluateAsync(CriteriaRequest request, CancellationToken ct = default)
        {
            Calls++;
            if (BehaviourAsync is not null) return BehaviourAsync(ct);
            if (Behaviour is not null) return Task.FromResult(Behaviour(request)!);
            return Task.FromResult(new CriteriaEvaluation
            {
                ProviderId = id,
                Recommendation = CriteriaRecommendation.MeetsCriteria,
            });
        }
    }
}
