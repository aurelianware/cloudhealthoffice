using System.Text.Json.Nodes;
using DaVinciInterop.Tests.Harness;
using FluentAssertions;

namespace DaVinciInterop.Tests.Unit;

/// <summary>
/// The harness answers the kits' waits by test id, and builds the CRD hook
/// requests that provoke each coverage-information answer. A wait it does not
/// recognise times out as "waiting" with nothing to say why (run 3), so the
/// ids are pinned here as the kits actually write them.
/// </summary>
[Trait("Category", "DaVinciInteropUnit")]
public sealed class KitHarnessRequestTests
{
    [Theory]
    [InlineData("smart_stu2_2-smart_full_ehr_launch_stu2-smart_ehr_launch_stu2-smart_app_redirect_stu2")]
    [InlineData("smart_stu2_2-smart_full_standalone_launch_stu2-smart_standalone_launch_stu2-smart_app_redirect_stu2")]
    public void The_smart_redirect_wait_is_answered_with_its_stu2_suffix(string testId)
        => InfernoSmartApp.IsRedirectWait(testId).Should().BeTrue();

    [Fact]
    public void The_smart_ehr_launch_wait_is_answered()
        => InfernoSmartApp.IsLaunchWait("smart_stu2_2-smart_full_ehr_launch_stu2-smart_ehr_launch_stu2-smart_app_launch")
            .Should().BeTrue();

    [Fact]
    public void The_kits_post_authorization_page_becomes_a_form_post_to_the_authorization_endpoint()
    {
        const string page = "http://inferno-smart:4567/custom/smart_stu2_2/post_auth_redirect"
            + "?auth_url=http%3A%2F%2Fcho-smart-auth%3A8080%2Fconnect%2Fauthorize&response_type=code&client_id=app&scope=openid+launch%2Fpatient&state=s1";

        var form = InfernoSmartApp.PostRedirectForm(page, "<title>Inferno POST Authorization Redirect</title>");

        form.Should().NotBeNull();
        form!.Value.Action.Should().Be("http://cho-smart-auth:8080/connect/authorize");
        form.Value.Fields.Should().Contain(new KeyValuePair<string, string>("scope", "openid launch/patient"))
            .And.Contain(new KeyValuePair<string, string>("state", "s1"))
            .And.NotContain(f => f.Key == "auth_url");
        InfernoSmartApp.PostRedirectForm(page, "<title>Consent</title>").Should().BeNull();
    }

    [Fact]
    public void The_pas_pended_decision_wait_is_answered_with_its_use_case_suffix()
    {
        PasPendedDecision.IsDecisionWait(
            "davinci_pas_server_suite_v221-pas_server_v221_pended_use_case-pas_server_v221_subscription_notification_wait_pended")
            .Should().BeTrue();
        PasPendedDecision.IsHandshakeWait(
            "davinci_pas_server_suite_v221-pas_server_v221_pended_use_case-pas_server_v221_subscription_notification_wait_pended")
            .Should().BeFalse();
    }

    private static JsonObject KitPasRequest() => JsonNode.Parse("""
        {"resourceType":"Bundle","identifier":{"system":"urn:example","value":"b1"},"entry":[
          {"resource":{"resourceType":"Claim","identifier":[{"system":"http://davinci-pas.org/PATIENT_EVENT_TRACE_NUMBER","value":"111099"}],
           "item":[{"sequence":1,"productOrService":{"coding":[{"system":"https://codesystem.x12.org/005010/1365","code":"3"}]}}]}}]}
        """)!.AsObject();

    [Fact]
    public void The_pas_must_support_submit_gains_a_pend_for_a_service_that_needs_documentation()
    {
        var kit = KitPasRequest();

        var added = PasServerRequests.MustSupportAdditions("must_support_pa_submit_request_payload", [kit]);

        var claim = added.Should().ContainSingle().Subject["entry"]![0]!["resource"]!;
        claim["identifier"]![0]!["value"]!.GetValue<string>().Should().Be(PasServerRequests.DocumentationPendIdentifier);
        claim["item"]![0]!["productOrService"]!["coding"]![0]!["code"]!.GetValue<string>()
            .Should().Be(PasServerRequests.PendedServiceType);
        kit["entry"]![0]!["resource"]!["identifier"]![0]!["value"]!.GetValue<string>()
            .Should().Be("111099", "the kit's own request is left as it is");
    }

    [Fact]
    public void The_pas_must_support_inquiry_gains_one_about_that_pend_and_nothing_else_does()
    {
        var added = PasServerRequests.MustSupportAdditions("must_support_pa_inquire_request_payload", [KitPasRequest()]);

        var claim = added.Should().ContainSingle().Subject["entry"]![0]!["resource"]!;
        claim["identifier"]![0]!["value"]!.GetValue<string>().Should().Be(PasServerRequests.DocumentationPendIdentifier);
        claim["item"]![0]!["productOrService"]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("3");

        PasServerRequests.MustSupportAdditions("approval_pa_submit_request_payload", [KitPasRequest()]).Should().BeEmpty();
    }

    [Fact]
    public void The_order_sign_requests_lead_with_the_one_hook_response_request()
    {
        var fixture = ChoSeedFixture.Load();
        var bodies = CrdServerRequests.OrderSign(fixture, "cov-1");

        bodies.Should().HaveCount(3);
        Orders(bodies[0]!).Should().Equal(Orders(CrdServerRequests.AnyHook(fixture, "cov-1")));
    }

    [Fact]
    public void The_follow_on_therapy_is_based_on_the_knee_order_in_the_same_request()
    {
        var body = CrdServerRequests.ApprovedWithFollowOn(ChoSeedFixture.Load(), "cov-1");

        var orders = body["context"]!["draftOrders"]!["entry"]!.AsArray().Select(e => e!["resource"]!).ToList();
        orders.Select(o => o["id"]!.GetValue<string>())
            .Should().Equal(CrdServerRequests.KneeOrderId, CrdServerRequests.TherapyOrderId);
        orders[1]["basedOn"]![0]!["reference"]!.GetValue<string>()
            .Should().Be($"ServiceRequest/{CrdServerRequests.KneeOrderId}");
    }

    [Fact]
    public void The_later_order_is_planned_after_the_coverage_it_names_ends()
    {
        var body = CrdServerRequests.AfterCoverageEnds(ChoSeedFixture.Load());

        var occurrence = body["context"]!["draftOrders"]!["entry"]![0]!["resource"]!["occurrenceDateTime"]!.GetValue<string>();
        var end = body["prefetch"]!["coverage"]!["entry"]![0]!["resource"]!["period"]!["end"]!.GetValue<string>();
        string.CompareOrdinal(occurrence, end).Should().BePositive();
    }

    [Fact]
    public void The_seed_approves_the_knee_procedure_for_the_member_the_requests_name()
    {
        var seed = JsonNode.Parse(File.ReadAllText(InteropPaths.ChoSeedFixture))!;
        var approval = seed["authorizations"]!["items"]!.AsArray()
            .Single(a => a!["authorizationNumber"]!.GetValue<string>() == CrdServerRequests.ApprovedAuthorizationNumber)!;

        approval["memberId"]!.GetValue<string>().Should().Be(ChoSeedFixture.Load().Member.MemberId);
        approval["requestedServices"]![0]!["procedureCode"]!.GetValue<string>().Should().Be(CrdServerRequests.KneeOrderCode);
    }

    [Fact]
    public void Every_order_the_requests_send_is_one_resource_in_the_mock_ehr()
    {
        var fixture = ChoSeedFixture.Load();
        var bundle = CrdServerRequests.MockEhrBundle(fixture, "cov-1", CrdSourceData.LapsedMember, "lapsed-1");

        var ids = bundle["entry"]!.AsArray().Select(e => $"{e!["resource"]!["resourceType"]}/{e["resource"]!["id"]}").ToList();
        ids.Should().OnlyHaveUniqueItems();
        ids.Should().Contain([
            $"ServiceRequest/{CrdServerRequests.KneeOrderId}",
            $"ServiceRequest/{CrdServerRequests.TherapyOrderId}",
            $"ServiceRequest/{CrdServerRequests.LaterKneeOrderId}",
            $"Coverage/{CrdServerRequests.EndingCoverageId}",
            $"Patient/{CrdServerRequests.EndingCoverageMemberId}",
        ]);
    }

    [Fact]
    public void The_introspection_header_form_encodes_the_credentials_before_base64()
    {
        var header = InfernoSmartApp.BasicAuthorizationHeader("fhir-api", "a+b/c=");

        header.Should().StartWith("Authorization: Basic ");
        var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(header["Authorization: Basic ".Length..]));
        decoded.Should().Be("fhir-api:a%2Bb%2Fc%3D");
    }

    [Fact]
    public void The_resource_server_is_an_introspection_only_backend_named_for_the_token_audience()
    {
        var body = InfernoSmartApp.ResourceServerRegistration();

        body["clientId"]!.GetValue<string>().Should().Be("fhir-api");
        body["kind"]!.GetValue<string>().Should().Be("backend");
        body["introspection"]!.GetValue<bool>().Should().BeTrue();
        body["scopes"]!.AsArray().Should().BeEmpty("a resource server is issued no tokens");
    }

    private static List<string> Orders(JsonNode body)
        => body["context"]!["draftOrders"]!["entry"]!.AsArray()
            .Select(e => e!["resource"]!.ToJsonString()).ToList();
}
