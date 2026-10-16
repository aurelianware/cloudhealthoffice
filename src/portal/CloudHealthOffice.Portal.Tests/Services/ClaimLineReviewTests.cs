using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using CloudHealthOffice.Portal.Pages;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

/// <summary>
/// Each claim line's Pay / Deny / Pend outcome and its reasons, derived from
/// what claims-service returns: the line's adjudication result and 835
/// adjustments, and the claim's pend details (edits name the lines they hit).
/// </summary>
public class ClaimLineReviewTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private static ClaimServiceLine Line(string json) => JsonSerializer.Deserialize<ClaimServiceLine>(json, Json)!;

    private static ClaimDetails Claim(string status, ClaimPendDetails? pend = null, string? denialReason = null) =>
        new() { ClaimId = "c1", ClaimNumber = "CLM-1", Status = status, PendDetails = pend, DenialReason = denialReason };

    [Fact]
    public void PaidLine_IsPay_WithItsAdjustmentsInPlainLanguage()
    {
        var line = Line("""
            {"lineNumber":1,"chargeAmount":272,"adjudicationResult":{"allowedAmount":125,"paidAmount":95,"patientResponsibility":30,
             "adjustmentReasons":[{"groupCode":"CO","reasonCode":"45","amount":147},{"groupCode":"PR","reasonCode":"3","amount":30}]}}
            """);

        var review = ClaimLineReview.Evaluate(Claim("Approved"), line);

        review.Outcome.Should().Be(LineOutcome.Pay);
        review.Label.Should().Be("Pay");
        review.Reasons.Select(r => r.Code).Should().Equal("CO-45", "PR-3");
        review.Reasons[0].Text.Should().Contain("contracted");
        review.Reasons.Should().OnlyContain(r => !r.IsDenial, "a contractual write-down and a copay are not denials");
        review.Summary.Should().Be("Member copay");
    }

    [Fact]
    public void LineWithNothingAllowed_AndADenialReason_IsDeny_WithThatReasonAsSummary()
    {
        var line = Line("""
            {"lineNumber":3,"chargeAmount":4112,"adjudicationResult":{"allowedAmount":0,"paidAmount":0,"patientResponsibility":0,
             "adjustmentReasons":[{"groupCode":"CO","reasonCode":"97","amount":4112,"remarkCode":"M80"}]}}
            """);

        var review = ClaimLineReview.Evaluate(Claim("Approved"), line);

        review.Outcome.Should().Be(LineOutcome.Deny);
        review.Reasons.Single().IsDenial.Should().BeTrue();
        review.Summary.Should().Be("Included in the payment for another service on this claim (remark M80)");
    }

    [Fact]
    public void DeductibleOnlyLine_IsPay_AsMemberCostShare_NotDeny()
    {
        var line = Line("""
            {"lineNumber":1,"chargeAmount":200,"adjudicationResult":{"allowedAmount":150,"paidAmount":0,"patientResponsibility":150,
             "adjustmentReasons":[{"groupCode":"PR","reasonCode":"1","amount":150}]}}
            """);

        var review = ClaimLineReview.Evaluate(Claim("Approved"), line);

        review.Outcome.Should().Be(LineOutcome.Pay);
        review.Label.Should().Be("Pay – member cost share");
        review.Summary.Should().Contain("deductible");
    }

    [Fact]
    public void PendedClaim_LinesAnEditHits_ArePend_WithTheEditAndTheOtherLineOfThePair()
    {
        var pend = new ClaimPendDetails
        {
            PendCode = "NCCI",
            EditFailures =
            {
                new ClaimPendEditFailure { EditType = "MUE", RuleId = "MUE-66984", Column1Code = "66984", AffectedLineNumbers = { 2, 3 } },
            },
        };
        var claim = Claim("Pended", pend);

        var line2 = ClaimLineReview.Evaluate(claim, Line("""{"lineNumber":2,"chargeAmount":4129}"""));
        var line1 = ClaimLineReview.Evaluate(claim, Line("""{"lineNumber":1,"chargeAmount":272}"""));

        line2.Outcome.Should().Be(LineOutcome.Pend);
        line2.Edits.Single().OtherLines.Should().Equal(3);
        line2.Summary.Should().Contain("66984").And.Contain("limit");
        line1.Outcome.Should().Be(LineOutcome.NotAdjudicated);
        line1.Label.Should().Be("On hold", "the claim is pended for another line; this one is waiting with it");
    }

    [Fact]
    public void DuplicateFinding_OnALine_IsShownAsItsEdit()
    {
        var pend = new ClaimPendDetails
        {
            PendCode = "DUPLICATE",
            DuplicateFindings = { new ClaimPendDuplicateFinding { DuplicateType = "Exact", RuleId = "DUP-1", LineNumber = 1, MatchedClaimNumber = "CLM-9", MatchedLineNumber = 2 } },
        };

        var review = ClaimLineReview.Evaluate(Claim("Pended", pend), Line("""{"lineNumber":1,"chargeAmount":100}"""));

        review.Outcome.Should().Be(LineOutcome.Pend);
        review.Edits.Single().Text.Should().Be("Possible duplicate of claim CLM-9, line 2");
    }

    [Fact]
    public void DeniedClaim_WithUnadjudicatedLines_DeniesEachLineWithTheClaimReason()
    {
        var review = ClaimLineReview.Evaluate(Claim("Denied", denialReason: "Member not eligible on date of service"),
            Line("""{"lineNumber":1,"chargeAmount":100}"""));

        review.Outcome.Should().Be(LineOutcome.Deny);
        review.Summary.Should().Be("Member not eligible on date of service");
    }

    [Theory]
    [InlineData("CO", "97", true)]
    [InlineData("CO", "45", false)]
    [InlineData("PR", "1", false)]
    [InlineData("PR", "204", false)]
    [InlineData("OA", "18", true)]
    public void IsDenial_SeparatesDenialsFromReductionsAndCostShare(string group, string code, bool denial) =>
        CarcText.IsDenial(group, code).Should().Be(denial);

    [Fact]
    public void UnknownCode_FallsBackToTheCode() =>
        CarcText.Describe("CO", "999").Should().Be("Adjustment reason 999");
}

/// <summary>Claim detail shows each line's outcome and links to the member, provider and authorization.</summary>
public class ClaimDetailsLineOutcomeTests : TestContext
{
    private readonly Mock<IClaimsService> _claims = new();

    public ClaimDetailsLineOutcomeTests()
    {
        Services.AddMudServices();
        Services.AddSingleton(_claims.Object);
        Services.AddSingleton(Mock.Of<IWorkQueueService>());
        Services.AddSingleton(Mock.Of<IUserContextService>());
        JSInterop.Mode = JSRuntimeMode.Loose;
        _claims.Setup(c => c.GetDuplicateMatchesAsync(It.IsAny<string>())).ReturnsAsync(new List<ClaimDuplicateMatch>());
        _claims.Setup(c => c.GetAdjudicationDataAsync(It.IsAny<string>())).ReturnsAsync((AdjudicationTransparencyData?)null);
    }

    [Fact]
    public void Lines_ShowPayAndDeny_WithTheReason_AndTheClaimLinksToMemberProviderAndAuth()
    {
        var lines = JsonSerializer.Deserialize<List<ClaimServiceLine>>("""
            [
              {"lineNumber":1,"procedureCode":"99214","chargeAmount":272,"adjudicationResult":{"allowedAmount":125,"paidAmount":95,"patientResponsibility":30,
                "adjustmentReasons":[{"groupCode":"CO","reasonCode":"45","amount":147},{"groupCode":"PR","reasonCode":"3","amount":30}]}},
              {"lineNumber":2,"procedureCode":"66984","chargeAmount":4129,"adjudicationResult":{"allowedAmount":0,"paidAmount":0,"patientResponsibility":0,
                "adjustmentReasons":[{"groupCode":"CO","reasonCode":"151","amount":4129}]}}
            ]
            """, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        _claims.Setup(c => c.GetClaimByIdAsync(It.IsAny<string>())).ReturnsAsync(new ClaimDetails
        {
            ClaimId = "A", ClaimNumber = "CLM-A", Status = "Approved", MemberId = "MBR001",
            BillingProviderName = "Lakeside Orthopedics", BillingProviderNPI = "1234567893",
            PriorAuthorizationNumber = "PA-77", ServiceLines = lines,
        });

        var cut = RenderComponent<ClaimDetailsNew>(p => p.Add(x => x.ClaimId, "A").Add(x => x.Tab, "lines"));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("CLM-A"));

        cut.Find("[data-testid=line-1-outcome]").TextContent.Trim().Should().Be("Pay");
        cut.Find("[data-testid=line-2-outcome]").TextContent.Trim().Should().Be("Deny");
        cut.Find("[data-testid=line-2-summary]").TextContent.Should().Contain("Units or frequency exceed");

        // Member and provider are on the Summary tab.
        cut.SetParametersAndRender(p => p.Add(x => x.Tab, "summary"));
        cut.Find("[data-testid=open-member]").TextContent.Should().Be("MBR001");
        cut.Find("[data-testid=open-billing-provider]").TextContent.Should().Be("Lakeside Orthopedics");
        cut.Find("[data-testid=open-authorization]").TextContent.Should().Contain("PA-77");
    }
}

/// <summary>An authorization opens by its record id or by its number.</summary>
public class AuthorizationLookupTests
{
    [Fact]
    public async Task GetAuthorizationById_FallsBackToTheNumber_WhenTheIdIsNotFound()
    {
        var urls = new List<string>();
        var handler = new StubHandler(req =>
        {
            urls.Add(req.RequestUri!.ToString());
            return req.RequestUri!.AbsolutePath.Contains("/number/")
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"authorizationNumber":"PA-77","status":"Approved"}""", Encoding.UTF8, "application/json"),
                }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Services:AuthorizationService"] = "http://auth/api",
        }).Build();
        var sut = new AuthorizationService(new HttpClient(handler), config, NullLogger<AuthorizationService>.Instance);

        var auth = await sut.GetAuthorizationByIdAsync("PA-77");

        urls.Should().Equal("http://auth/api/authorizations/PA-77", "http://auth/api/authorizations/number/PA-77");
        auth!.AuthorizationNumber.Should().Be("PA-77");
        auth.StatusText.Should().Be("Approved");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
