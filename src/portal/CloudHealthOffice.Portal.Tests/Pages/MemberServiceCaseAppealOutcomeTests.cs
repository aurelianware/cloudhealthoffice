using System.Net;
using System.Net.Http.Json;
using Bunit;
using CloudHealthOffice.Portal.Pages;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

public class MemberServiceCaseAppealOutcomeTests : TestContext
{
    private object _case = new { };

    public MemberServiceCaseAppealOutcomeTests()
    {
        Services.AddMudServices();
        Services.AddSingleton(new HttpClient(new CaseApi(() => _case, _requests)));
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Services:AppealsService"] = "http://appeals/api"
        }).Build());
        Services.AddSingleton(new Mock<IDocumentCatalogService>().Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private readonly List<HttpRequestMessage> _requests = [];

    private IRenderedComponent<MemberServiceCases> Open(params string[] permissions)
    {
        Services.AddSingleton(GatingUsers.With(permissions.Length > 0 ? permissions : ["appeals:read", "appeals:write"]).Object);
        return RenderComponent<MemberServiceCases>(p => p.Add(x => x.CaseId, "case-1"));
    }

    [Fact]
    public void Appeal_decision_needs_an_outcome_before_it_can_be_recorded()
    {
        _case = new { contactId = "contact-1", memberId = "synthetic-member", classification = "Appeal", status = "Received", revision = 1 };
        var cut = Open();
        cut.WaitForElement("[data-testid=appeal-outcome]");
        cut.Find("textarea").Change("Synthetic rationale");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Record decision")).HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Grievance_decision_has_no_outcome_selector()
    {
        _case = new { contactId = "contact-1", memberId = "synthetic-member", classification = "Grievance", status = "Received", revision = 1 };
        var cut = Open();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Record decision"));
        cut.FindAll("[data-testid=appeal-outcome]").Should().BeEmpty();
        cut.Find("textarea").Change("Synthetic rationale");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Record decision")).HasAttribute("disabled").Should().BeFalse();
    }

    [Theory]
    [InlineData("Reported", "since")]
    [InlineData("Pending", "retried automatically")]
    [InlineData("NeedsAttention", "did not accept")]
    public void Decided_appeal_shows_its_outcome_and_where_reporting_stands(string reporting, string text)
    {
        _case = new { contactId = "contact-1", memberId = "synthetic-member", classification = "Appeal",
            status = "DecisionRecorded", decisionOutcome = "Overturned", reporting,
            reportedToAuthorizationAt = reporting == "Reported" ? DateTime.UtcNow : (DateTime?)null,
            reportingRejectedReason = reporting == "NeedsAttention" ? "authorization-service refused the fact (400)." : null };
        var cut = Open();
        cut.WaitForElement("[data-testid=appeal-reporting]");
        cut.Find("[data-testid=appeal-outcome-recorded]").TextContent.Should().Contain("overturned (approved)");
        cut.Find("[data-testid=appeal-reporting]").TextContent.Should().Contain(text);
    }

    private object Refused() => new { contactId = "contact-1", memberId = "synthetic-member", classification = "Appeal",
        status = "DecisionRecorded", decisionOutcome = "Overturned", reporting = "NeedsAttention",
        reportingRejectedReason = "authorization-service refused the fact (400)." };

    [Fact]
    public void Reporting_admin_can_requeue_a_refused_outcome()
    {
        _case = Refused();
        var cut = Open("appeals:read", "appeals:admin");
        cut.WaitForElement("[data-testid=appeal-reporting-requeue]");
        _case = new { contactId = "contact-1", memberId = "synthetic-member", classification = "Appeal",
            status = "DecisionRecorded", decisionOutcome = "Overturned", reporting = "Reported", reportedToAuthorizationAt = DateTime.UtcNow };

        cut.Find("[data-testid=appeal-reporting-requeue]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=appeal-reporting]").TextContent.Should().Contain("since"));
        _requests.Should().Contain(r => r.Method == HttpMethod.Post
            && r.RequestUri!.AbsolutePath == "/api/service-cases/case-1/reporting/requeue");
    }

    [Fact]
    public void Reviewer_without_admin_sees_the_refusal_but_no_requeue()
    {
        _case = Refused();
        var cut = Open();
        cut.WaitForElement("[data-testid=appeal-reporting]");
        cut.FindAll("[data-testid=appeal-reporting-requeue]").Should().BeEmpty();
    }

    private sealed class CaseApi(Func<object> body, List<HttpRequestMessage> requests) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body()) });
        }
    }
}
