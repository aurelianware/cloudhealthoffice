using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.ReferenceData.Payers;
using FluentAssertions;
using NSubstitute;

namespace CloudHealthOffice.ProviderEligibilityApi.Tests;

public sealed class ProviderEligibilityCheckTests : IDisposable
{
    private readonly ProviderEligibilityApiFactory _factory = new();
    private GatewayEligibilityRequest? _sent;

    public ProviderEligibilityCheckTests()
    {
        GatewayReturns(EligibilityTestData.ActiveCoverage());
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Active_coverage_returns_normalized_result()
    {
        var response = await Post(EligibilityTestData.SelfRequest());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(response);
        body.GetProperty("outcome").GetString().Should().Be("Completed");
        body.GetProperty("eligible").GetBoolean().Should().BeTrue();
        body.GetProperty("coverageStatus").GetString().Should().Be("Active");
        body.GetProperty("planName").GetString().Should().Be("Dental PPO");
        body.GetProperty("coverageStart").GetString().Should().Be("2026-01-01");
        body.GetProperty("benefits")[0].GetProperty("amount").GetDecimal().Should().Be(1250m);
        body.GetProperty("correlationId").GetString().Should().Be("booking-42");
    }

    [Fact]
    public async Task Self_request_is_mapped_with_tenant_from_the_authenticated_request()
    {
        await Post(EligibilityTestData.SelfRequest());

        _sent.Should().NotBeNull();
        _sent!.TenantId.Should().Be(ProviderEligibilityApiFactory.PracticeTenant);
        _sent.PayerId.Should().Be("87726");
        _sent.ProviderNpi.Should().Be("1999999984");
        _sent.ServiceTypeCode.Should().Be("35");
        _sent.ResolveSubscriberMemberId().Should().Be(EligibilityTestData.MemberId);
        _sent.ResolveSubscriberDateOfBirth().Should().Be(DateOnly.Parse(EligibilityTestData.SubscriberDob));
        _sent.IsDependentInquiry().Should().BeFalse();
    }

    [Fact]
    public async Task Tenant_in_the_body_is_ignored()
    {
        var json = JsonSerializer.Serialize(EligibilityTestData.SelfRequest())
            .Replace("{\"payerId\"", "{\"tenantId\":\"other-practice\",\"payerId\"");

        var response = await _factory.CreateAuthorizedClient().PostAsync(
            "/api/v1/eligibility/check", new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _sent!.TenantId.Should().Be(ProviderEligibilityApiFactory.PracticeTenant);
    }

    [Fact]
    public async Task Dependent_request_is_sent_as_a_dependent_inquiry()
    {
        await Post(EligibilityTestData.DependentRequest());

        _sent!.IsDependentInquiry().Should().BeTrue();
        _sent.Patient!.RelationshipToSubscriber.Should().Be("child");
        _sent.Patient.DateOfBirth.Should().Be(DateOnly.Parse(EligibilityTestData.DependentDob));
        _sent.ServiceTypeCode.Should().Be("30", "the gateway default applies when no service type is sent");
    }

    [Fact]
    public async Task Invalid_request_is_rejected_before_any_payer_call()
    {
        var response = await Post(new
        {
            payerId = "",
            provider = new { npi = "12345" },
            subscriber = new { memberId = EligibilityTestData.MemberId, firstName = EligibilityTestData.SubscriberFirst }
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var text = await response.Content.ReadAsStringAsync();
        text.Should().Contain("payerId").And.Contain("provider.npi")
            .And.Contain("subscriber.lastName").And.Contain("subscriber.dateOfBirth");
        text.Should().NotContain(EligibilityTestData.MemberId).And.NotContain(EligibilityTestData.SubscriberFirst);
        await _factory.Gateway.DidNotReceiveWithAnyArgs().CheckEligibilityAsync(default!, default);
    }

    [Fact]
    public async Task Patient_marked_as_self_is_rejected()
    {
        var response = await Post(new
        {
            payerId = "87726",
            provider = new { npi = "1999999984" },
            subscriber = new { memberId = "M1", firstName = "A", lastName = "B", dateOfBirth = "1980-01-01" },
            patient = new { firstName = "A", lastName = "B", dateOfBirth = "1980-01-01", relationshipToSubscriber = "self" }
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("patient.relationshipToSubscriber");
    }

    [Fact]
    public async Task Payer_rejection_is_an_answer_not_an_error()
    {
        GatewayReturns(EligibilityTestData.PayerRejection());

        var response = await Post(EligibilityTestData.SelfRequest());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(response);
        body.GetProperty("outcome").GetString().Should().Be("Rejected");
        body.GetProperty("errorCategory").GetString().Should().Be("PayerRejected");
        body.GetProperty("eligible").GetBoolean().Should().BeFalse();
        body.GetProperty("message").GetString().Should().Be("Invalid/Missing Subscriber/Insured ID");
    }

    [Theory]
    [InlineData(GatewayErrorCategory.PayerNotFound, HttpStatusCode.UnprocessableEntity)]
    [InlineData(GatewayErrorCategory.EnrollmentRequired, HttpStatusCode.UnprocessableEntity)]
    [InlineData(GatewayErrorCategory.Validation, HttpStatusCode.UnprocessableEntity)]
    [InlineData(GatewayErrorCategory.Timeout, HttpStatusCode.ServiceUnavailable)]
    [InlineData(GatewayErrorCategory.RateLimited, HttpStatusCode.ServiceUnavailable)]
    [InlineData(GatewayErrorCategory.Authentication, HttpStatusCode.BadGateway)]
    [InlineData(GatewayErrorCategory.Configuration, HttpStatusCode.BadGateway)]
    [InlineData(GatewayErrorCategory.Internal, HttpStatusCode.InternalServerError)]
    public async Task Gateway_failures_map_to_distinct_status_codes(GatewayErrorCategory category, HttpStatusCode expected)
    {
        GatewayReturns(EligibilityTestData.Failure(category));

        var response = await Post(EligibilityTestData.SelfRequest());

        response.StatusCode.Should().Be(expected);
        var body = await Json(response);
        body.GetProperty("outcome").GetString().Should().Be("Failed");
        body.GetProperty("errorCategory").GetString().Should().Be(category.ToString());
        body.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Response_does_not_echo_member_identity()
    {
        var response = await Post(EligibilityTestData.SelfRequest());

        var text = await response.Content.ReadAsStringAsync();
        text.Should().NotContain(EligibilityTestData.MemberId)
            .And.NotContain(EligibilityTestData.SubscriberFirst)
            .And.NotContain(EligibilityTestData.SubscriberLast)
            .And.NotContain(EligibilityTestData.SubscriberDob);
    }

    [Fact]
    public async Task Logs_do_not_contain_member_identity_or_payer_text()
    {
        await Post(EligibilityTestData.DependentRequest());
        GatewayReturns(EligibilityTestData.PayerRejection());
        await Post(EligibilityTestData.SelfRequest());

        var logs = string.Join('\n', _factory.LogMessages);
        logs.Should().Contain("Provider eligibility check");
        foreach (var phi in new[]
                 {
                     EligibilityTestData.MemberId, EligibilityTestData.SubscriberFirst,
                     EligibilityTestData.SubscriberLast, EligibilityTestData.SubscriberDob,
                     EligibilityTestData.DependentFirst, EligibilityTestData.DependentDob,
                     "Invalid/Missing Subscriber"
                 })
        {
            logs.Should().NotContain(phi);
        }
    }

    [Fact]
    public async Task Payer_search_returns_routable_payer_ids()
    {
        _factory.Payers.SearchAsync(Arg.Any<PayerSearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<PayerReference>
            {
                new()
                {
                    Id = "HPQRS",
                    Name = "UnitedHealthcare",
                    ExternalIdentifiers =
                    {
                        new PayerExternalIdentifier { System = "stedi", Type = "id", Value = "HPQRS" },
                        new PayerExternalIdentifier { System = "stedi", Type = "primaryPayerId", Value = "87726" },
                        new PayerExternalIdentifier { System = "stedi", Type = "tradingPartnerServiceId", Value = "87726" }
                    },
                    SupportedTransactions =
                    {
                        new PayerTransactionCapability
                        {
                            Transaction = HealthcareTransactionType.Eligibility270271,
                            Support = PayerTransactionSupport.Supported
                        }
                    }
                }
            });

        var response = await _factory.CreateAuthorizedClient().GetAsync("/api/v1/payers?q=united&maxResults=500");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(response);
        body[0].GetProperty("name").GetString().Should().Be("UnitedHealthcare");
        body[0].GetProperty("payerIds").EnumerateArray().Select(e => e.GetString()).Should().Equal("87726");
        body[0].GetProperty("eligibility").GetString().Should().Be("Supported");
        await _factory.Payers.Received(1).SearchAsync(
            Arg.Is<PayerSearchQuery>(q => q.Text == "united" && q.MaxResults == 25 && q.Active == true),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Payer_search_requires_a_meaningful_query()
    {
        var response = await _factory.CreateAuthorizedClient().GetAsync("/api/v1/payers?q=u");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private void GatewayReturns(GatewayResponse<GatewayEligibilityResponse> response)
    {
        _factory.Gateway.CheckEligibilityAsync(Arg.Any<GatewayEligibilityRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _sent = call.Arg<GatewayEligibilityRequest>();
                return response;
            });
    }

    private Task<HttpResponseMessage> Post(object request) =>
        _factory.CreateAuthorizedClient().PostAsJsonAsync("/api/v1/eligibility/check", request);

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
}
