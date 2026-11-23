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
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Stages;

/// <summary>
/// A line that needs prior authorization but carries no number is matched to
/// the plan's approved authorizations by member, date, service and provider
/// (shadow runs by default; live adjudication when the plan turns it on), and
/// every checked line records its evidence: why it needed an authorization, the
/// rule and medical policy version, the authorization and how it was found.
/// </summary>
public sealed class BenefitCalculationStagePriorAuthMatchTests
{
    private const string Tenant = "tenant-1";

    private static readonly AccumulatorCommit Prepared = new()
    {
        CommitId = "commit-test", ClaimId = "claim", MemberId = "MEM-1", SubscriberId = "MEM-1",
        BenefitPlanId = Guid.Empty, PlanYear = "2026",
    };

    private readonly IBenefitCalculationEngine _engine = Substitute.For<IBenefitCalculationEngine>();
    private readonly IPriorAuthRuleScopeClient _scopes = Substitute.For<IPriorAuthRuleScopeClient>();
    private readonly IAuthorizationMatchClient _matcher = Substitute.For<IAuthorizationMatchClient>();
    private string? _criteria;

    public BenefitCalculationStagePriorAuthMatchTests()
    {
        _engine.CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BenefitResolutionResult { PreparedAccumulatorCommit = Prepared, Success = true, Totals = new ClaimTotals() });
        _scopes.GetScopeAsync(Tenant, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new PriorAuthRuleScope
        {
            RuleSet = "program-star",
            RuleSetVersion = "a1b2c3d4e5f6",
            Entries =
            [
                new PriorAuthRuleScopeEntry
                {
                    RuleId = "import:imaging", Name = "Advanced imaging", Codes = ["70551"],
                    Condition = PriorAuthRuleCondition.Always,
                    PolicySource = "Plan medical policy MP-RAD-014", PolicyVersion = "v4",
                    PolicyEffectiveDate = new DateTime(2026, 1, 1),
                },
            ],
        });
    }

    private void MatchReturns(AuthorizationMatchLookup lookup) =>
        _matcher.FindCoveringAsync(Tenant, "MEM-1", Arg.Any<DateTime>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(lookup);

    [Fact]
    public async Task Shadow_run_matches_an_approved_authorization_when_the_claim_has_no_number()
    {
        MatchReturns(AuthorizationMatchLookup.From([
            new AuthorizationMatchCandidate("AUTH-9", 1, CriteriaReference: "InterQual 2026 Imaging",
                MatchedOn: ["member", "service date", "procedure", "provider"]),
        ]));
        var ctx = Professional(shadow: true, authNumber: null, "70551");

        var result = await Stage().ExecuteAsync(ctx, CancellationToken.None);

        PriorAuthorizationPassed(ctx, result);
        var evidence = Assert.Single(ctx.PriorAuthorizationEvidence);
        Assert.Equal("AUTH-9", evidence.AuthorizationNumber);
        Assert.Equal(PriorAuthorizationNumberSource.Matched, evidence.NumberSource);
        Assert.Equal(PriorAuthorizationLineOutcome.Valid, evidence.Outcome);
        Assert.Equal("InterQual 2026 Imaging", evidence.CriteriaReference);
        Assert.Contains("procedure", evidence.MatchedOn);
        await _matcher.Received(1).FindCoveringAsync(Tenant, "MEM-1", new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc),
            "70551", "1234567890", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task No_covering_authorization_still_denies_197_and_says_none_matched()
    {
        MatchReturns(AuthorizationMatchLookup.From([]));
        var ctx = Professional(shadow: true, authNumber: null, "70551");

        var result = await Stage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Deny, result.Outcome);
        Assert.Equal(BenefitCalculationStage.PriorAuthorizationRequiredCode, ctx.AdjudicationResult.DenialReasonCode);
        var evidence = Assert.Single(ctx.PriorAuthorizationEvidence);
        Assert.Equal(PriorAuthorizationLineOutcome.Missing, evidence.Outcome);
        Assert.Contains("no approved authorization covers", evidence.Reason);
    }

    [Fact]
    public async Task A_match_that_cannot_be_answered_pends_rather_than_denies()
    {
        MatchReturns(AuthorizationMatchLookup.Unavailable("authorization match returned 503"));
        var ctx = Professional(shadow: true, authNumber: null, "70551");

        var result = await Stage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(BenefitCalculationStage.PriorAuthorizationUnavailablePendCode, ctx.PendDetails!.PendCode);
        Assert.Null(ctx.AdjudicationResult.DenialReasonCode);
    }

    [Fact]
    public async Task Live_adjudication_does_not_match_unless_the_plan_turns_it_on()
    {
        MatchReturns(AuthorizationMatchLookup.From([new AuthorizationMatchCandidate("AUTH-9")]));

        var off = Professional(shadow: false, authNumber: null, "70551");
        Assert.Equal(ClaimAdjudicationOutcome.Deny, (await Stage().ExecuteAsync(off, CancellationToken.None)).Outcome);
        await _matcher.DidNotReceiveWithAnyArgs().FindCoveringAsync(default!, default!, default, default, default, default);

        var on = Professional(shadow: false, authNumber: null, "70551");
        Assert.Equal(ClaimAdjudicationOutcome.Pass,
            (await Stage(new PriorAuthAdjudicationOptions { UseRuleSet = true, MatchWithoutNumber = true })
                .ExecuteAsync(on, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Shadow_matching_can_be_turned_off()
    {
        MatchReturns(AuthorizationMatchLookup.From([new AuthorizationMatchCandidate("AUTH-9")]));
        var ctx = Professional(shadow: true, authNumber: null, "70551");

        var result = await Stage(new PriorAuthAdjudicationOptions { UseRuleSet = true, MatchWithoutNumberInShadow = false })
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Deny, result.Outcome);
        Assert.Equal(PriorAuthorizationNumberSource.None, Assert.Single(ctx.PriorAuthorizationEvidence).NumberSource);
    }

    [Fact]
    public async Task A_number_on_the_claim_is_checked_as_before_and_its_criteria_recorded()
    {
        _criteria = "Plan medical policy MP-RAD-014 v4";
        var ctx = Professional(shadow: true, authNumber: "AUTH-1", "70551");

        var result = await Stage().ExecuteAsync(ctx, CancellationToken.None);

        PriorAuthorizationPassed(ctx, result);
        await _matcher.DidNotReceiveWithAnyArgs().FindCoveringAsync(default!, default!, default, default, default, default);
        var evidence = Assert.Single(ctx.PriorAuthorizationEvidence);
        Assert.Equal(PriorAuthorizationNumberSource.Claim, evidence.NumberSource);
        Assert.Equal("AUTH-1", evidence.AuthorizationNumber);
        Assert.Equal("Plan medical policy MP-RAD-014 v4", evidence.CriteriaReference);
    }

    [Fact]
    public async Task Evidence_names_the_rule_rule_set_version_and_medical_policy_version()
    {
        MatchReturns(AuthorizationMatchLookup.From([]));
        var ctx = Professional(shadow: true, authNumber: null, "99213", "70551");

        await Stage().ExecuteAsync(ctx, CancellationToken.None);

        var evidence = Assert.Single(ctx.PriorAuthorizationEvidence);
        Assert.Equal(2, evidence.LineNumber);
        Assert.Equal(PriorAuthorizationRequiredBy.RuleSet, evidence.RequiredBy);
        Assert.Equal("import:imaging", evidence.RuleId);
        Assert.Equal("a1b2c3d4e5f6", evidence.RuleSetVersion);
        Assert.Equal("v4", evidence.PolicyVersion);
        Assert.Equal(
            "Plan medical policy MP-RAD-014 v4 (effective 2026-01-01); rule import:imaging; rule set a1b2c3d4e5f6",
            evidence.PolicyLabel());
    }

    /// <summary>
    /// A shadow run goes on to price against the core's accumulator history,
    /// which this fixture does not supply: what matters here is that the
    /// prior-authorization check neither denied nor pended the claim.
    /// </summary>
    private static void PriorAuthorizationPassed(ClaimAdjudicationContext ctx, ClaimAdjudicationStageResult result)
    {
        Assert.NotEqual(ClaimAdjudicationOutcome.Deny, result.Outcome);
        Assert.Null(ctx.AdjudicationResult.DenialReasonCode);
        Assert.NotEqual(BenefitCalculationStage.PriorAuthorizationUnavailablePendCode, ctx.PendDetails?.PendCode);
        Assert.NotEqual(BenefitCalculationStage.RuleSetExemptionReviewPendCode, ctx.PendDetails?.PendCode);
        Assert.DoesNotContain(BenefitCalculationStage.PriorAuthorizationUnavailableReason, result.Reason ?? string.Empty);
    }

    private BenefitCalculationStage Stage(PriorAuthAdjudicationOptions? options = null)
    {
        var handler = new Responder(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                authorizationNumber = "AUTH-1", isValid = true, status = "Approved",
                validationMessage = "Authorization valid", validatedMemberId = "MEM-1", memberMatches = true,
                criteriaReference = _criteria,
            }),
        });
        var client = new HttpAuthorizationValidationClient(new Factory(handler), NullLogger<HttpAuthorizationValidationClient>.Instance);
        return new BenefitCalculationStage(
            _engine,
            Substitute.For<IMemberResolver>(),
            client,
            NullLogger<BenefitCalculationStage>.Instance,
            Microsoft.Extensions.Options.Options.Create(options ?? new PriorAuthAdjudicationOptions { UseRuleSet = true }),
            _scopes,
            authorizationMatcher: _matcher);
    }

    private static ClaimAdjudicationContext Professional(bool shadow, string? authNumber, params string[] procedureCodes)
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
            IsShadow = shadow,
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

/// <summary>The HTTP shape of authorization-service's match answer.</summary>
public sealed class HttpAuthorizationMatchClientTests
{
    private static HttpAuthorizationValidationClient Client(HttpStatusCode status, string body, List<string>? urls = null) =>
        new(new Factory(new Responder(req =>
        {
            urls?.Add(req.RequestUri!.PathAndQuery);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        })), NullLogger<HttpAuthorizationValidationClient>.Instance);

    [Fact]
    public async Task Reads_candidates_and_sends_member_date_procedure_and_provider()
    {
        const string body = """
        {"candidates":[{"authorizationNumber":"AUTH-9","lineNumber":1,"status":"Approved",
          "approvedServiceDateFrom":"2026-04-01T00:00:00","approvedServiceDateTo":"2026-04-30T00:00:00",
          "criteriaReference":"InterQual 2026 Imaging","matchedOn":["member","service date","procedure"]}]}
        """;
        var urls = new List<string>();

        var lookup = await Client(HttpStatusCode.OK, body, urls)
            .FindCoveringAsync("t", "MEM-1", new DateTime(2026, 4, 15), "70551", "1234567890");

        Assert.Equal(AuthorizationMatchStatus.Found, lookup.Status);
        Assert.Equal("AUTH-9", lookup.Candidates.Single().AuthorizationNumber);
        Assert.Equal("InterQual 2026 Imaging", lookup.Candidates.Single().CriteriaReference);
        Assert.Equal(
            "/api/authorizations/match?memberId=MEM-1&serviceDate=2026-04-15&procedureCode=70551&providerNpi=1234567890",
            urls.Single());
    }

    [Fact]
    public async Task An_empty_list_is_none_and_errors_are_unavailable()
    {
        Assert.Equal(AuthorizationMatchStatus.None,
            (await Client(HttpStatusCode.OK, """{"candidates":[]}""").FindCoveringAsync("t", "M", DateTime.Today, null, null)).Status);
        Assert.Equal(AuthorizationMatchStatus.Unavailable,
            (await Client(HttpStatusCode.NotFound, "").FindCoveringAsync("t", "M", DateTime.Today, null, null)).Status);
        Assert.Equal(AuthorizationMatchStatus.Unavailable,
            (await Client(HttpStatusCode.OK, "{}").FindCoveringAsync("t", "M", DateTime.Today, null, null)).Status);
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
