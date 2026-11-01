using System.Net;
using System.Net.Http.Json;
using System.Text;
using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using ClaimsService.Services.Resolution;
using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Stages;

/// <summary>
/// Claims read the plan's PA rule set, the one CRD, PAS and the published
/// required-services list answer from (ClaimsAdjudication:PriorAuth:UseRuleSet).
/// </summary>
public sealed class BenefitCalculationStagePriorAuthRuleSetTests
{
    private const string Tenant = "tenant-1";

    private static readonly AccumulatorCommit Prepared = new()
    {
        CommitId = "commit-test", ClaimId = "claim", MemberId = "MEM-1", SubscriberId = "MEM-1",
        BenefitPlanId = Guid.Empty, PlanYear = "2026",
    };

    private readonly IBenefitCalculationEngine _engine = Substitute.For<IBenefitCalculationEngine>();
    private readonly IPriorAuthRuleScopeClient _scopes = Substitute.For<IPriorAuthRuleScopeClient>();
    private int _validations;

    public BenefitCalculationStagePriorAuthRuleSetTests()
    {
        _engine.CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BenefitResolutionResult { PreparedAccumulatorCommit = Prepared, Success = true, Totals = new ClaimTotals() });
    }

    private static PriorAuthRuleScope Scope(params PriorAuthRuleScopeEntry[] entries) => new()
    {
        RuleSet = "program-star", Entries = entries,
    };

    private static PriorAuthRuleScopeEntry Always(string code, params string[] pos) => new()
    {
        RuleId = "import:imaging", Codes = [code], Condition = PriorAuthRuleCondition.Always, PlacesOfService = pos,
    };

    private void ScopeIs(PriorAuthRuleScope? scope) =>
        _scopes.GetScopeAsync(Tenant, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(scope);

    [Fact]
    public async Task Off_by_default_the_rule_set_is_not_read()
    {
        ScopeIs(Scope(Always("70551")));
        var ctx = Professional(null, "70551");

        var result = await Stage(useRuleSet: false).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        await _scopes.DidNotReceiveWithAnyArgs().GetScopeAsync(default!, default!, default);
    }

    [Fact]
    public async Task A_rule_set_code_without_an_authorization_denies_197()
    {
        ScopeIs(Scope(Always("70551")));
        var ctx = Professional(null, "99213", "70551");

        var result = await Stage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Deny, result.Outcome);
        Assert.Equal(BenefitCalculationStage.PriorAuthorizationRequiredCode, ctx.AdjudicationResult.DenialReasonCode);
    }

    [Fact]
    public async Task A_valid_authorization_passes()
    {
        ScopeIs(Scope(Always("70551")));
        var ctx = Professional("AUTH-1", "70551");

        var result = await Stage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Equal(1, _validations);
    }

    [Fact]
    public async Task Codes_outside_the_rule_set_and_its_places_of_service_need_nothing()
    {
        ScopeIs(Scope(Always("70551", "22")));
        var ctx = Professional(null, "70551", "99213"); // POS 11

        var result = await Stage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Equal(0, _validations);
    }

    [Fact]
    public async Task A_plan_without_a_rule_set_keeps_the_existing_scope()
    {
        ScopeIs(null);
        var result = await Stage().ExecuteAsync(Professional(null, "70551"), CancellationToken.None);
        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
    }

    [Fact]
    public async Task An_unreadable_rule_set_pends_PAUNAVAIL()
    {
        _scopes.GetScopeAsync(Tenant, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new PriorAuthRuleScopeUnavailableException("benefit-plan-service returned 503"));
        var ctx = Professional(null, "70551");

        var result = await Stage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(BenefitCalculationStage.PriorAuthorizationUnavailablePendCode, ctx.PendDetails!.PendCode);
        Assert.Contains("PA rule set", ctx.PendDetails.PendReason);
        Assert.Null(ctx.AdjudicationResult.DenialReasonCode);
    }

    [Fact]
    public async Task With_exemptions_in_the_set_a_missing_authorization_goes_to_review_not_denial()
    {
        ScopeIs(new PriorAuthRuleScope
        {
            RuleSet = "program-star", Entries = [Always("70551")], Exemptions = ["TxGoldCardExemption"],
        });
        var ctx = Professional(null, "70551");

        var result = await Stage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(BenefitCalculationStage.RuleSetExemptionReviewPendCode, ctx.PendDetails!.PendCode);
        Assert.Contains("TxGoldCardExemption", ctx.PendDetails.PendReason);
        Assert.Null(ctx.AdjudicationResult.DenialReasonCode);
        Assert.False(ClaimAdjudicationOrchestrator.IsTransientPendCode(ctx.PendDetails.PendCode));
    }

    [Theory]
    [InlineData("G47.33", ClaimAdjudicationOutcome.Deny)]
    [InlineData("J45.909", ClaimAdjudicationOutcome.Pass)]
    public async Task Diagnosis_rules_apply_only_with_a_listed_diagnosis(string diagnosis, ClaimAdjudicationOutcome expected)
    {
        ScopeIs(Scope(new PriorAuthRuleScopeEntry
        {
            RuleId = "import:sleep", Codes = ["95810"], Condition = PriorAuthRuleCondition.WithDiagnosis, Diagnoses = ["G4733"],
        }));
        var ctx = Professional(null, "95810");
        ctx.Claim.DiagnosisCodes = [new AdapterDiagnosisCode { Code = diagnosis, PointerNumber = 1 }];

        var result = await Stage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
    }

    [Fact]
    public void Prefixes_and_all_services_match_and_quantity_limits_do_not()
    {
        var claim = Professional(null, "K0001", "97110").Claim;
        var scope = Scope(
            new PriorAuthRuleScopeEntry { RuleId = "dme", CodePrefixes = ["K"], Condition = PriorAuthRuleCondition.Always },
            new PriorAuthRuleScopeEntry { RuleId = "pt", Codes = ["97110"], Condition = PriorAuthRuleCondition.AboveQuantityLimit });

        Assert.Equal("dme", scope.Match(claim, claim.ClaimLines[0])?.RuleId);
        Assert.Null(scope.Match(claim, claim.ClaimLines[1]));
        Assert.NotNull(Scope(new PriorAuthRuleScopeEntry { RuleId = "all", AllServices = true }).Match(claim, claim.ClaimLines[1]));
    }

    private BenefitCalculationStage Stage(bool useRuleSet = true)
    {
        var handler = new Responder(_ =>
        {
            _validations++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    authorizationNumber = "AUTH-1", isValid = true, status = "Approved",
                    validationMessage = "Authorization valid", validatedMemberId = "MEM-1", memberMatches = true,
                }),
            };
        });
        var client = new HttpAuthorizationValidationClient(new Factory(handler), NullLogger<HttpAuthorizationValidationClient>.Instance);
        return new BenefitCalculationStage(
            _engine,
            Substitute.For<IMemberResolver>(),
            client,
            NullLogger<BenefitCalculationStage>.Instance,
            Microsoft.Extensions.Options.Options.Create(new PriorAuthAdjudicationOptions { UseRuleSet = useRuleSet }),
            _scopes);
    }

    private static ClaimAdjudicationContext Professional(string? authNumber, params string[] procedureCodes)
    {
        var serviceDate = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc);
        var claim = new AdapterClaim
        {
            TenantId = Tenant,
            Id = "claim-1",
            ClaimVersionId = "claim-1",
            ClaimNumber = "CLM-1",
            MemberId = "MEM-1",
            SubscriberId = "MEM-1",
            BenefitPlanId = Guid.NewGuid().ToString(),
            BillingProviderNPI = "1234567890",
            LineOfBusiness = LineOfBusiness.Medicaid,
            ClaimType = ClaimType.Professional,
            PlaceOfServiceCode = "11",
            PriorAuthorizationNumber = authNumber,
            ServiceDateFrom = serviceDate,
            ServiceDateTo = serviceDate,
            ClaimLines = procedureCodes.Select((code, i) => new AdapterClaimLine
            {
                LineNumber = i + 1, ProcedureCode = code, ChargeAmount = 100m, Units = 1,
                ServiceDateFrom = serviceDate, ServiceDateTo = serviceDate,
            }).ToList(),
        };
        return new ClaimAdjudicationContext
        {
            TenantId = Tenant,
            ClaimVersionId = claim.Id,
            Claim = claim,
            PricingResult = new PricingOutcome { AllowedAmounts = claim.ClaimLines.ToDictionary(l => l.LineNumber, _ => 72m) },
            ResolvedMember = new ResolvedMember { MemberId = "MEM-1", IsSubscriber = true },
        };
    }

    private sealed class Responder(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://svc.test") };
    }
}

/// <summary>The HTTP shape of benefit-plan-service's plan required-services answer.</summary>
public sealed class HttpPriorAuthRuleScopeClientTests
{
    private static HttpPriorAuthRuleScopeClient Client(HttpStatusCode status, string body, List<string>? urls = null) =>
        new(new Factory(new Responder(req =>
        {
            urls?.Add(req.RequestUri!.PathAndQuery);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        })), NullLogger<HttpPriorAuthRuleScopeClient>.Instance);

    [Fact]
    public async Task Reads_entries_and_exemption_kinds()
    {
        const string body = """
        {"planId":"p1","programId":"prog","resolved":true,"list":{"ruleSet":"prog","entries":[
          {"ruleId":"import:imaging","name":"Imaging","allServices":false,"codes":["70551"],"codePrefixes":[],
           "condition":"Always","placesOfService":["22"],"diagnoses":[]},
          {"ruleId":"import:sleep","codes":["95810"],"condition":1,"diagnoses":["G4733"]}],
          "exemptions":[{"ruleId":"TX-GOLD","name":"Gold card","kind":"TxGoldCardExemption"}]}}
        """;
        var urls = new List<string>();

        var scope = await Client(HttpStatusCode.OK, body, urls).GetScopeAsync("t", "p1");

        Assert.NotNull(scope);
        Assert.Equal("/api/v1/prior-auth-rules/plans/p1/required-services?includeDrugs=true", urls.Single());
        Assert.Equal(PriorAuthRuleCondition.WithDiagnosis, scope!.Entries[1].Condition);
        Assert.Equal(["22"], scope.Entries[0].PlacesOfService);
        Assert.Equal(["TxGoldCardExemption"], scope.Exemptions);
    }

    [Fact]
    public async Task Unresolved_and_not_found_are_answers_and_errors_are_not()
    {
        Assert.Null(await Client(HttpStatusCode.OK, """{"planId":"p1","resolved":false,"reason":"no program"}""").GetScopeAsync("t", "p1"));
        Assert.Null(await Client(HttpStatusCode.NotFound, "{}").GetScopeAsync("t", "p1"));
        await Assert.ThrowsAsync<PriorAuthRuleScopeUnavailableException>(() =>
            Client(HttpStatusCode.ServiceUnavailable, "{}").GetScopeAsync("t", "p1"));
        await Assert.ThrowsAsync<PriorAuthRuleScopeUnavailableException>(() =>
            Client(HttpStatusCode.OK, "not json").GetScopeAsync("t", "p1"));
    }

    private sealed class Responder(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://svc.test") };
    }
}
