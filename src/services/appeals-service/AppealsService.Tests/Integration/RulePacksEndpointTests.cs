using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace AppealsService.Tests.Integration;

public class RulePacksEndpointTests : IClassFixture<AppealsWebApplicationFactory>
{
    private readonly AppealsWebApplicationFactory _factory;

    public RulePacksEndpointTests(AppealsWebApplicationFactory factory) => _factory = factory;

    private HttpClient NewClient(string tenant = "tenant-a")
    {
        var client = _factory.CreateDefaultClient(new CloudHealthOffice.Infrastructure.Security.ChoDevelopmentTokenHandler());
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task List_returns_every_pack_including_the_national_one()
    {
        var response = await NewClient().GetAsync("/api/appeals/rule-packs");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var states = (await Json(response)).EnumerateArray().Select(p => p.GetProperty("state").GetString()).ToList();
        // The ten Medicaid States, plus Medicare Advantage, which is federal
        // and so reports "US" rather than a State.
        states.Should().HaveCount(11).And.Contain(new[] { "TX", "CA", "OH", "US" });
    }

    [Fact]
    public async Task The_national_pack_answers_for_any_State()
    {
        // A Medicare Advantage member in Wyoming has the same clocks as one
        // in Texas, so the lookup must not need a pack per State.
        foreach (var state in new[] { "wy", "TX" })
        {
            var response = await NewClient().GetAsync($"/api/appeals/rule-packs/{state}?lineOfBusiness=Medicare");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await Json(response)).GetProperty("id").GetString().Should().Be("medicare-advantage");
        }
    }

    [Fact]
    public async Task Get_returns_the_pack_for_a_State()
    {
        var response = await NewClient().GetAsync("/api/appeals/rule-packs/tx?asOf=2026-11-01");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var pack = await Json(response);
        pack.GetProperty("id").GetString().Should().Be("tx-medicaid");
        pack.GetProperty("memberAppeal").GetProperty("resolution").GetProperty("amount").GetInt32().Should().Be(30);
    }

    [Fact]
    public async Task Unknown_State_returns_404()
    {
        // Medicaid has no national pack to fall back to.
        var response = await NewClient().GetAsync("/api/appeals/rule-packs/wy");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Deadlines_are_computed_with_citations()
    {
        var response = await NewClient().GetAsync(
            "/api/appeals/rule-packs/co/deadlines?receivedAt=2026-11-04T12:00:00Z&decidedAt=2026-11-10T12:00:00Z");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var deadlines = (await Json(response)).GetProperty("deadlines").EnumerateArray().ToList();
        var resolution = deadlines.Single(d => d.GetProperty("name").GetString() == "resolution");
        resolution.GetProperty("dueAt").GetDateTime().ToUniversalTime().Should().Be(new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc));
        resolution.GetProperty("citation").GetString().Should().Contain("8.209");
        deadlines.Should().Contain(d => d.GetProperty("name").GetString() == "fairHearingRequest");
    }

    [Fact]
    public async Task Deadlines_without_receivedAt_return_400()
    {
        var response = await NewClient().GetAsync("/api/appeals/rule-packs/tx/deadlines");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Recoupment_is_effective_dated()
    {
        var before = await Json(await NewClient().GetAsync("/api/appeals/rule-packs/ok/recoupment?asOf=2026-12-31"));
        var after = await Json(await NewClient().GetAsync("/api/appeals/rule-packs/ok/recoupment?asOf=2027-01-01"));

        before.GetProperty("hold").GetString().Should().BeEquivalentTo("unknown");
        after.GetProperty("hold").GetString().Should().BeEquivalentTo("required");
    }

    [Fact]
    public async Task Requires_a_token()
    {
        var response = await _factory.CreateClient().GetAsync("/api/appeals/rule-packs");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
