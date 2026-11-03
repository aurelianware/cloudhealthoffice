using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Portal.Tests.Services;

/// <summary>
/// The guided plan onboarding client: template reads, Draft from template,
/// the tenant's Drafts, a full-fidelity Draft read, save and publish.
/// </summary>
public class PlanTemplateServiceTests
{
    private readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Services:BenefitPlanService"] = "http://bp/api" })
        .Build();

    private PlanTemplateService Create(FakeHandler handler) =>
        new(new HttpClient(handler), _configuration, Mock.Of<ILogger<PlanTemplateService>>());

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Lists_templates_with_state_and_program_filters()
    {
        var handler = new FakeHandler(HttpStatusCode.OK,
            """[{"templateId":"tx-chip-186","state":"TX","program":"CHIP","planName":"Texas CHIP","population":"Kids","verifyCount":3,"notModeledCount":2}]""");

        var list = await Create(handler).ListTemplatesAsync("TX", "CHIP");

        handler.CapturedUrls.Single().Should().Be("http://bp/api/v1/plans/templates?state=TX&program=CHIP");
        list.Single().VerifyCount.Should().Be(3);
    }

    [Fact]
    public async Task Reads_a_template_from_its_detail_envelope()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """
            {"template":{"templateId":"tx-chip-186","state":"TX","program":"CHIP","planName":"Texas CHIP",
              "benefits":[{"serviceCategory":"Office Visit","covered":true,"copay":20,"source":"hhsc","status":"sourced"},
                          {"serviceCategory":"Inpatient","covered":true,"copay":75,"status":"verify"}],
              "sources":{"hhsc":{"title":"HHSC D-1800","url":"https://example.test"}}},
             "preview":{"planId":"preview-tx-chip-186"}}
            """);

        var t = await Create(handler).GetTemplateAsync("tx-chip-186");

        t!.Benefits.Should().HaveCount(2);
        t.BenefitFor("office visit")!.IsVerify.Should().BeFalse();
        t.SourceFor("hhsc")!.Title.Should().Be("HHSC D-1800");
        t.BenefitFor("Inpatient")!.IsVerify.Should().BeTrue();
    }

    [Fact]
    public async Task A_missing_template_is_null()
    {
        var t = await Create(new FakeHandler(HttpStatusCode.NotFound)).GetTemplateAsync("nope");
        t.Should().BeNull();
    }

    [Fact]
    public async Task Creating_a_draft_posts_to_the_template_and_returns_its_version()
    {
        var handler = new FakeHandler(HttpStatusCode.Created, """{"id":"row-1","planId":"TX-CHIP-2027","versionId":"v-1"}""");

        var result = await Create(handler).CreateDraftFromTemplateAsync("tx-chip-186",
            new CreateDraftFromTemplateRequest { PlanId = "TX-CHIP-2027", Payer = "Acme Health", EffectiveDate = new DateTime(2027, 1, 1) });

        result.Should().Be(PlanWriteResult.Ok("TX-CHIP-2027", "v-1"));
        var req = handler.CapturedRequests.Single();
        req.Method.Should().Be(HttpMethod.Post);
        req.RequestUri!.AbsoluteUri.Should().Be("http://bp/api/v1/plans/templates/tx-chip-186/drafts");
        var body = JsonNode.Parse(await req.Content!.ReadAsStringAsync())!;
        body["planId"]!.ToString().Should().Be("TX-CHIP-2027");
        body["payer"]!.ToString().Should().Be("Acme Health");
    }

    [Fact]
    public async Task A_taken_plan_id_comes_back_as_the_services_message()
    {
        var handler = new FakeHandler(HttpStatusCode.Conflict, """{"message":"Plan 'TX-CHIP-2027' already exists; amend it instead.","versionId":"v-0"}""");

        var result = await Create(handler).CreateDraftFromTemplateAsync("tx-chip-186", new CreateDraftFromTemplateRequest { PlanId = "TX-CHIP-2027", Payer = "Acme" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("already exists");
    }

    [Fact]
    public async Task Forbidden_names_the_permission()
    {
        var result = await Create(new FakeHandler(HttpStatusCode.Forbidden)).PublishAsync("P", "v");
        result.Error.Should().Contain("settings:manage");
    }

    [Fact]
    public async Task Drafts_are_the_tenants_Draft_versions_newest_first()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """
            [{"planId":"A","versionId":"a1","versionState":"Published","createdAt":"2026-10-01T00:00:00Z"},
             {"planId":"B","versionId":"b1","versionState":"Draft","createdAt":"2026-10-02T00:00:00Z"},
             {"planId":"C","versionId":"c1","versionState":"Draft","createdAt":"2026-11-01T00:00:00Z"}]
            """);

        var drafts = await Create(handler).ListDraftsAsync();

        handler.CapturedUrls.Single().Should().Be("http://bp/api/v1/plans?activeOnly=false");
        drafts.Select(d => d.PlanId).Should().Equal("C", "B");
    }

    [Fact]
    public async Task A_draft_is_read_from_the_stored_version_list_with_every_field()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """
            {"items":[{"id":"r2","planId":"P","versionId":"v2","versionState":"Draft","accumulatorTargets":[{"x":1}]},
                      {"id":"r1","planId":"P","versionId":"v1","versionState":"Published"}],
             "continuationToken":null}
            """);

        var draft = await Create(handler).GetVersionAsync("P", "v2");

        handler.CapturedUrls.Single().Should().Be("http://bp/api/v1/plans/P/versions?pageSize=200");
        draft!["id"]!.ToString().Should().Be("r2");
        draft["accumulatorTargets"].Should().NotBeNull("fields the portal does not show must survive a save");
    }

    [Fact]
    public async Task Saving_puts_the_whole_draft_to_its_row_id()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """{"planId":"P","versionId":"v2"}""");
        var draft = new JsonObject { ["id"] = "r2", ["planId"] = "P", ["planYearDefinition"] = new JsonObject { ["type"] = "Calendar" } };

        var result = await Create(handler).SaveDraftAsync(draft);

        result.Success.Should().BeTrue();
        var req = handler.CapturedRequests.Single();
        req.Method.Should().Be(HttpMethod.Put);
        req.RequestUri!.AbsoluteUri.Should().Be("http://bp/api/v1/plans/r2");
        (await req.Content!.ReadAsStringAsync()).Should().Contain("planYearDefinition");
    }

    [Fact]
    public async Task Publishing_posts_to_the_versions_publish_route()
    {
        var handler = new FakeHandler(_ => JsonResponse(HttpStatusCode.OK, """{"planId":"P","versionId":"v2","versionState":"Published"}"""));

        var result = await Create(handler).PublishAsync("P", "v2");

        result.Success.Should().BeTrue();
        handler.CapturedUrls.Single().Should().Be("http://bp/api/v1/plans/P/versions/v2/publish");
    }

    [Fact]
    public async Task An_unreachable_service_is_service_unavailable()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("down"));
        await Assert.ThrowsAsync<ServiceUnavailableException>(() => Create(handler).ListTemplatesAsync());
    }
}
