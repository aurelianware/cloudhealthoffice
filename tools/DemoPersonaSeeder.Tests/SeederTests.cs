using System.Net;
using System.Text;
using DemoPersonaSeeder;
using FluentAssertions;
using Xunit;

namespace DemoPersonaSeeder.Tests;

public class SeederTests
{
    private static readonly DateTime AsOf = new(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc);

    private static SeederOptions Options() => new()
    {
        TenantId = "demo",
        ConfirmSyntheticTenant = "demo",
        ClaimWaitSeconds = 0,
        ClaimsUrl = "http://claims", BenefitPlanUrl = "http://plans", MemberUrl = "http://members",
        ProviderUrl = "http://providers", AuthorizationUrl = "http://auths", CoverageUrl = "http://coverage",
        AppealsUrl = "http://appeals", SponsorUrl = "http://sponsors", BillingUrl = "http://billing",
        ContractsUrl = "http://contracts",
    };

    [Theory]
    [InlineData(null)]
    [InlineData("other")]
    [InlineData("DEMO")]
    public void Refuses_to_run_unless_the_tenant_is_confirmed(string? confirm)
    {
        var o = Options();
        o.ConfirmSyntheticTenant = confirm;
        o.Validate().Should().Contain("--confirm-synthetic");
    }

    [Fact]
    public void Confirmed_tenant_passes_validation() => Options().Validate().Should().BeNull();

    [Fact]
    public async Task Empty_tenant_gets_everything_created_in_dependency_order()
    {
        var handler = new FakeServices(existing: false);
        var run = await new Seeder(new HttpClient(handler), Options(), PersonaCatalog.Build(AsOf)).RunAsync();

        run.HasFailures.Should().BeFalse(string.Join("; ", run.Steps.Where(s => s.Outcome == StepOutcome.Failed).Select(s => s.Detail)));

        int First(string pathStart) => handler.Posts.FindIndex(p => p.StartsWith(pathStart, StringComparison.Ordinal));
        First("http://providers/api/v1/networks").Should().BeLessThan(First("http://plans/api/v1/plans"));
        First("http://plans/api/v1/plans").Should().BeLessThan(First("http://providers/api/v1/providers"));
        First("http://providers/api/v1/providers").Should().BeLessThan(First("http://members/api/v1/members"));
        First("http://members/api/v1/members").Should().BeLessThan(First("http://coverage/api/v1/coverage"));
        First("http://auths/api/authorizations").Should().BeLessThan(First("http://claims/api/v1/claims"));
        First("http://claims/api/v1/claims").Should().BeLessThan(First("http://appeals/api/appeals"));

        handler.Posts.Count(p => p == "http://claims/api/v1/claims").Should().Be(9);
        handler.Bodies.Where(b => b.Url == "http://members/api/v1/members").Select(b => b.Body)
            .TakeWhile(b => b.Contains("\"isSubscriber\":true"))
            .Should().NotBeEmpty("subscribers are created before their dependents");
    }

    [Fact]
    public async Task Rerun_against_a_seeded_tenant_creates_nothing_new()
    {
        var handler = new FakeServices(existing: true);
        var run = await new Seeder(new HttpClient(handler), Options(), PersonaCatalog.Build(AsOf)).RunAsync();

        run.HasFailures.Should().BeFalse();
        handler.Posts.Should().OnlyContain(p => p.Contains("/identifiers") || p.Contains("billing-runs"),
            "identifiers and billing runs report duplicates with 409 instead of a lookup");
        run.Steps.Where(s => s.Area == "claim").Should().OnlyContain(s => s.Outcome == StepOutcome.Existing);
    }

    [Fact]
    public async Task A_failing_service_is_reported_and_the_run_continues()
    {
        var handler = new FakeServices(existing: false) { FailHost = "coverage" };
        var run = await new Seeder(new HttpClient(handler), Options(), PersonaCatalog.Build(AsOf)).RunAsync();

        run.HasFailures.Should().BeTrue();
        run.Steps.Where(s => s.Outcome == StepOutcome.Failed).Should().OnlyContain(s => s.Area == "coverage" || s.Area == "PCP");
        handler.Posts.Should().Contain("http://claims/api/v1/claims");
    }

    [Fact]
    public void Dry_run_script_lists_the_cast_and_says_nothing_was_written()
    {
        var data = PersonaCatalog.Build(AsOf);
        var script = DemoScript.Render(data, "demo", run: null);

        script.Should().Contain("dry run");
        foreach (var m in data.Members) script.Should().Contain(m.MemberId);
        foreach (var c in data.Claims) script.Should().Contain(c.ClaimNumber);
        script.Should().Contain("1EG4TE5MK73").And.Contain("A90011234");
    }

    /// <summary>Stands in for every service: 404 or 200 on lookups, 201 on creates, 200 on updates.</summary>
    private sealed class FakeServices(bool existing) : HttpMessageHandler
    {
        public List<string> Posts { get; } = new();
        public List<(string Url, string Body)> Bodies { get; } = new();
        public string? FailHost { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.GetLeftPart(UriPartial.Path);
            if (FailHost is not null && request.RequestUri.Host == FailHost)
                return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") };

            if (request.Method == HttpMethod.Get)
            {
                if (!existing) return new HttpResponseMessage(HttpStatusCode.NotFound);
                var body = url.Contains("/coverage/member/") ? "[{\"id\":\"c1\"}]" : "{\"id\":\"" + Guid.NewGuid() + "\",\"status\":5}";
                return Json(HttpStatusCode.OK, body);
            }

            if (request.Method == HttpMethod.Post)
            {
                Posts.Add(url);
                Bodies.Add((url, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
                if (existing && (url.Contains("/identifiers") || url.EndsWith("/billing-runs", StringComparison.Ordinal)))
                    return new HttpResponseMessage(HttpStatusCode.Conflict);
                return Json(HttpStatusCode.Created, "{\"id\":\"" + Guid.NewGuid() + "\",\"status\":1}");
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        private static HttpResponseMessage Json(HttpStatusCode code, string body)
            => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
