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
        Services.AddSingleton(new HttpClient(new CaseApi(() => _case)));
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Services:AppealsService"] = "http://appeals/api"
        }).Build());
        Services.AddSingleton(new Mock<IDocumentCatalogService>().Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<MemberServiceCases> Open()
    {
        Services.AddSingleton(GatingUsers.With("appeals:read", "appeals:write").Object);
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

    private sealed class CaseApi(Func<object> body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body()) });
    }
}
