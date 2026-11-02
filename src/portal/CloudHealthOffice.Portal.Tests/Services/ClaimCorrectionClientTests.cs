using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Portal.Tests.Services;

/// <summary>
/// The portal's calls for claim correction: load the draft and submit the
/// corrected claim to the adjustments API at the claims-service root.
/// </summary>
public class ClaimCorrectionClientTests
{
    private static ClaimsService Service(FakeHandler handler) => new(
        new HttpClient(handler),
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Services:ClaimsService"] = "http://claims/api" })
            .Build(),
        Mock.Of<ILogger<ClaimsService>>());

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Draft_is_read_from_the_v1_route()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, """{"memberId":"MEM-1"}"""));

        var draft = await Service(handler).GetCorrectionDraftAsync("claim 1");

        handler.CapturedUrls.Should().ContainSingle().Which.Should().Be("http://claims/api/v1/claims/claim%201/correction-draft");
        draft!["memberId"]!.GetValue<string>().Should().Be("MEM-1");
    }

    [Fact]
    public async Task Missing_claim_has_no_draft()
    {
        var draft = await Service(new FakeHandler(HttpStatusCode.NotFound)).GetCorrectionDraftAsync("x");
        draft.Should().BeNull();
    }

    [Fact]
    public async Task Submit_posts_reason_and_corrected_claim_with_the_idempotency_key()
    {
        string? body = null;
        string? key = null;
        var handler = new FakeHandler(request =>
        {
            body = request.Content!.ReadAsStringAsync().Result;
            key = request.Headers.GetValues("Idempotency-Key").Single();
            return Json(HttpStatusCode.Created, """{"id":"adj-1","newClaimId":"claim-2","status":0}""");
        });

        var result = await Service(handler).SubmitCorrectionAsync(
            "claim-1", "Units were 3, should be 1", null, new JsonObject { ["memberId"] = "MEM-1" }, "key-1");

        handler.CapturedRequests.Single().Method.Should().Be(HttpMethod.Post);
        handler.CapturedUrls.Single().Should().Be("http://claims/api/v1/claims/claim-1/adjustments");
        key.Should().Be("key-1");
        var sent = JsonNode.Parse(body!)!;
        sent["adjustmentReason"]!.GetValue<string>().Should().Be("Units were 3, should be 1");
        sent["correctedClaim"]!["memberId"]!.GetValue<string>().Should().Be("MEM-1");
        result.Success.Should().BeTrue();
        result.AdjustmentId.Should().Be("adj-1");
        result.NewClaimId.Should().Be("claim-2");
    }

    [Fact]
    public async Task A_refusal_comes_back_in_the_services_words()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.BadRequest,
            """{"error":"Corrected claim failed validation","errors":[{"field":"ClaimLines[0].ProcedureCode","code":"required","message":"Procedure code is required"}]}"""));

        var result = await Service(handler).SubmitCorrectionAsync("claim-1", "r", null, new JsonObject(), "k");

        result.Success.Should().BeFalse();
        result.Errors.Should().Equal(
            "Corrected claim failed validation",
            "ClaimLines[0].ProcedureCode: Procedure code is required");
    }

    [Fact]
    public async Task An_unadjustable_claim_says_why()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.UnprocessableEntity,
            """{"error":"Adjustment-of-adjustment is not supported in Phase 1; predecessor must be a first-generation claim","predecessorClaimId":"c"}"""));

        var result = await Service(handler).SubmitCorrectionAsync("c", "r", null, new JsonObject(), "k");

        result.Errors.Should().ContainSingle().Which.Should().StartWith("Adjustment-of-adjustment");
    }

    [Fact]
    public async Task Forbidden_is_a_permission_message()
    {
        var result = await Service(new FakeHandler(HttpStatusCode.Forbidden))
            .SubmitCorrectionAsync("c", "r", null, new JsonObject(), "k");

        result.Errors.Should().Equal("You don't have permission to correct claims.");
    }

    [Fact]
    public async Task New_claim_posts_to_v1_intake_and_returns_its_id()
    {
        string? body = null;
        var handler = new FakeHandler(request =>
        {
            body = request.Content!.ReadAsStringAsync().Result;
            return Json(HttpStatusCode.Created, """{"id":"claim-9","claimNumber":"MAN-1","status":1}""");
        });

        var result = await Service(handler).SubmitNewClaimAsync(new JsonObject { ["memberId"] = "MEM-1" });

        handler.CapturedUrls.Single().Should().Be("http://claims/api/v1/claims");
        JsonNode.Parse(body!)!["memberId"]!.GetValue<string>().Should().Be("MEM-1");
        result.Success.Should().BeTrue();
        result.NewClaimId.Should().Be("claim-9");
    }

    [Fact]
    public async Task New_claim_validation_errors_come_back_in_the_services_words()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.BadRequest,
            """{"errors":[{"field":"ServiceDateFrom","code":"Required","message":"ServiceDateFrom is required"}]}"""));

        var result = await Service(handler).SubmitNewClaimAsync(new JsonObject());

        result.Errors.Should().Equal("ServiceDateFrom: ServiceDateFrom is required");
    }
}
