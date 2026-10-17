using Bunit;
using Bunit.TestDoubles;
using CloudHealthOffice.Portal.Infrastructure;
using CloudHealthOffice.Portal.Pages;
using CloudHealthOffice.Portal.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

/// <summary>
/// Claims Insights (portal-navigation.md §5.1): aggregates with every number
/// a link to the claims list filtered to exactly what it counts, and the
/// claims list applying those links' filters from the URL.
/// </summary>
public class ClaimsInsightsTests : TestContext
{
    private static readonly DateTime AsOf = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
    private readonly Mock<IClaimsService> _claims = new();
    private readonly Mock<IUserContextService> _user = new();

    public ClaimsInsightsTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_claims.Object);
        Services.AddSingleton(_user.Object);
        Services.AddSingleton(Mock.Of<IProviderService>());
        Services.AddSingleton(Mock.Of<IMemberService>());
        Services.AddSingleton(Mock.Of<ISearchCriteriaPolicy>(p => p.RequiresCriteriaAsync() == Task.FromResult(true)));
        _user.Setup(u => u.GetCurrentUserAsync()).ReturnsAsync(new UserContext { UserId = "u1", Roles = ["ClaimsSupervisor"] });
        _user.Setup(u => u.HasPermission(It.IsAny<string>())).Returns(true);
        _claims.Setup(c => c.SearchClaimsAsync(It.IsAny<ClaimSearchRequest>())).ReturnsAsync(new ClaimSearchResult());
    }

    private static ClaimInsightsView Sample() => new()
    {
        AsOf = AsOf, From = AsOf.AddDays(-30), To = AsOf,
        Inventory = new()
        {
            Total = 5, TotalCharge = 650m,
            ByStatus = [new() { Status = "Pended", Count = 3, Charge = 300m }, new() { Status = "Received", Count = 2, Charge = 350m }],
            Aging =
            [
                new() { Label = "0–15 days", MinDays = 0, MaxDays = 15, Count = 1, ReceivedAfter = AsOf.AddDays(-16) },
                new() { Label = "16–30 days", MinDays = 16, MaxDays = 30, Count = 2, ReceivedAfter = AsOf.AddDays(-31), ReceivedOnOrBefore = AsOf.AddDays(-16) },
                new() { Label = "31–45 days", MinDays = 31, MaxDays = 45, Count = 0, ReceivedAfter = AsOf.AddDays(-46), ReceivedOnOrBefore = AsOf.AddDays(-31) },
                new() { Label = "46–60 days", MinDays = 46, MaxDays = 60, Count = 1, ReceivedAfter = AsOf.AddDays(-61), ReceivedOnOrBefore = AsOf.AddDays(-46) },
                new() { Label = "61+ days", MinDays = 61, Count = 1, ReceivedOnOrBefore = AsOf.AddDays(-61) },
            ],
        },
        PendReasons = [new() { PendCode = "AUTH", Count = 2, OldestPendedAt = AsOf.AddDays(-45) }],
        Outcomes = new() { Adjudicated = 4, AutoAdjudicated = 3, AutoAdjudicationRate = 0.75m, ByStatus = [new() { Status = "Paid", Count = 4 }] },
        DenialReasons = [new() { Code = "27", Description = "Coverage terminated", Count = 2 }],
        Turnaround = new() { Count = 4, AverageDays = 5m, MedianDays = 4m, P90Days = 8m },
    };

    [Fact]
    public void Shows_the_headline_figures_each_linked_to_its_claims()
    {
        _claims.Setup(c => c.GetClaimInsightsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), null)).ReturnsAsync(Sample());

        var cut = RenderComponent<ClaimsInsights>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=stat-open]").TextContent.Should().Contain("5"));
        cut.Find("[data-testid=stat-open-link]").GetAttribute("href").Should().Be("/claims?open=true");
        cut.Find("[data-testid=stat-over30]").TextContent.Should().Contain("2").And.Contain("40%");
        cut.Find("[data-testid=stat-over30-link]").GetAttribute("href").Should().Be("/claims?open=true&receivedOnOrBefore=2026-09-16T12%3A00%3A00Z");
        cut.Find("[data-testid=stat-auto]").TextContent.Should().Contain("75.0").And.Contain("3 of 4");
        cut.Find("[data-testid=stat-turnaround]").TextContent.Should().Contain("4 days").And.Contain("90% within 8 days");
    }

    [Fact]
    public void Aging_pend_and_denial_rows_drill_down_with_the_same_criteria()
    {
        _claims.Setup(c => c.GetClaimInsightsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), null)).ReturnsAsync(Sample());

        var cut = RenderComponent<ClaimsInsights>();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=insights-aging-row]").Should().HaveCount(5));
        // A zero bucket has no link (there's nothing to open).
        cut.FindAll("[data-testid=insights-aging-link]").Select(a => a.GetAttribute("href")).Should().Equal(
            "/claims?open=true&receivedAfter=2026-10-01T12%3A00%3A00Z",
            "/claims?open=true&receivedAfter=2026-09-16T12%3A00%3A00Z&receivedOnOrBefore=2026-10-01T12%3A00%3A00Z",
            "/claims?open=true&receivedAfter=2026-08-17T12%3A00%3A00Z&receivedOnOrBefore=2026-09-01T12%3A00%3A00Z",
            "/claims?open=true&receivedOnOrBefore=2026-08-17T12%3A00%3A00Z");
        cut.Find("[data-testid=insights-pends-link]").GetAttribute("href").Should().Be("/claims?status=Pended&pendCode=AUTH");
        cut.Find("[data-testid=insights-pends-row]").TextContent.Should().Contain("Authorization check").And.Contain("oldest 45 days");
        cut.Find("[data-testid=insights-denials-link]").GetAttribute("href")
            .Should().Be("/claims?adjudicated=true&denialCode=27&adjudicatedFrom=2026-09-17T12%3A00%3A00Z&adjudicatedBefore=2026-10-17T12%3A00%3A00Z");
    }

    [Fact]
    public void The_period_and_line_of_business_come_from_the_url()
    {
        var nav = Services.GetRequiredService<FakeNavigationManager>();
        nav.NavigateTo("/claims/insights?days=90&lob=Medicaid");
        DateTime? from = null, to = null;
        _claims.Setup(c => c.GetClaimInsightsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), "Medicaid"))
            .Callback((DateTime f, DateTime t, string? _) => { from = f; to = t; })
            .ReturnsAsync(Sample());

        var cut = RenderComponent<ClaimsInsights>();

        cut.WaitForAssertion(() => from.Should().NotBeNull());
        (to!.Value - from!.Value).TotalDays.Should().BeApproximately(90, 0.01);
        cut.Find("[data-testid=insights-period-90]").ClassList.Should().Contain("mud-button-filled");
    }

    [Fact]
    public void An_outage_is_said_plainly()
    {
        _claims.Setup(c => c.GetClaimInsightsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<string?>()))
            .ThrowsAsync(new ServiceUnavailableException("Claims Service", new Exception()));

        var cut = RenderComponent<ClaimsInsights>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=insights-error]").TextContent.Should().Contain("Claims Service"));
    }

    [Fact]
    public void The_claims_list_applies_a_drill_down_link_and_searches_without_asking_for_criteria()
    {
        ClaimSearchRequest? sent = null;
        _claims.Setup(c => c.SearchClaimsAsync(It.IsAny<ClaimSearchRequest>()))
            .Callback((ClaimSearchRequest r) => sent = r)
            .ReturnsAsync(new ClaimSearchResult { TotalCount = 2, PageSize = 25 });

        Services.GetRequiredService<FakeNavigationManager>()
            .NavigateTo("/claims?open=true&receivedAfter=2026-09-16T12%3A00%3A00Z&receivedOnOrBefore=2026-10-01T12%3A00%3A00Z&lob=Medicaid");

        var cut = RenderComponent<CloudHealthOffice.Portal.Pages.Claims>();

        cut.WaitForAssertion(() => sent.Should().NotBeNull());
        sent!.Open.Should().BeTrue();
        sent.ReceivedAfter.Should().Be(new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc));
        sent.ReceivedOnOrBefore.Should().Be(new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc));
        sent.LineOfBusiness.Should().Be("Medicaid");
        cut.Find("[data-testid=claims-drilldown]").TextContent.Should().Contain("open claims").And.Contain("Medicaid");
        cut.Find("[data-testid=module-view-insights]").GetAttribute("href").Should().Be("/claims/insights");
    }
}
