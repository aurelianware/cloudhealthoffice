using System.Net;
using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Gateways.Stedi;
using CloudHealthOffice.Infrastructure.Tests.ReferenceData.Payers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways.Stedi;

/// <summary>
/// End-to-end tests for <see cref="StediHealthcareGateway"/> over a stubbed HTTP
/// transport: normalized success, configuration/validation failures, payer
/// rejection, HTTP error normalization, capability discovery, and metadata.
/// </summary>
public class StediHealthcareGatewayTests
{
    private const string ActiveJson =
        "{\"meta\":{\"traceId\":\"trace-xyz\"},\"planStatus\":[{\"statusCode\":\"1\"}]," +
        "\"planInformation\":{\"planNumber\":\"P1\",\"groupDescription\":\"Gold\"}," +
        "\"benefitsInformation\":[{\"code\":\"1\",\"name\":\"Health Benefit Plan Coverage\"}]}";

    private const string RejectionJson =
        "{\"errors\":[{\"code\":\"72\",\"description\":\"Invalid/Missing Subscriber ID\"}]}";

    private static StediGatewayOptions ValidOptions() => new()
    {
        ApiKey = "test-key",
        BaseUrl = "https://healthcare.test",
        Environment = "sandbox",
        EligibilityPath = "/eligibility/v3",
        MaxRetries = 1
    };

    private static StediHealthcareGateway NewGateway(
        StubHttpMessageHandler handler, StediGatewayOptions? options = null)
    {
        options ??= ValidOptions();
        var opts = Options.Create(options);
        var apiClient = new StediEligibilityApiClient(
            new StubHttpClientFactory(handler), opts,
            NullLogger<StediEligibilityApiClient>.Instance,
            delay: (_, _) => Task.CompletedTask);
        return new StediHealthcareGateway(
            apiClient, PayerTestHarness.CreateResolver(opts), opts,
            NullLogger<StediHealthcareGateway>.Instance);
    }

    private static GatewayEligibilityRequest Request(string? payerId = "60054") => new()
    {
        TenantId = "tenant-alpha",
        SubscriberId = "MBR-1",
        ProviderNpi = "1234567890",
        ServiceTypeCode = "30",
        ServiceDate = new DateOnly(2026, 6, 1),
        PayerId = payerId,
        CorrelationId = "corr-1"
    };

    [Fact]
    public async Task Eligibility_Success_IsNormalizedWithMetadata()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, ActiveJson);
        var gateway = NewGateway(handler);

        var response = await gateway.CheckEligibilityAsync(Request());

        response.IsSuccess.Should().BeTrue();
        response.Result!.IsEligible.Should().BeTrue();
        response.Result.CoverageStatus.Should().Be(GatewayCoverageStatus.Active);
        response.Result.PlanId.Should().Be("P1");

        response.Metadata.GatewayName.Should().Be("Stedi");
        response.Metadata.TransactionType.Should().Be(HealthcareTransactionType.Eligibility270271);
        response.Metadata.Status.Should().Be(GatewayTransactionStatus.Completed);
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.None);
        response.Metadata.ExternalTransactionId.Should().Be("trace-xyz");
        response.Metadata.TenantId.Should().Be("tenant-alpha");
        response.Metadata.CorrelationId.Should().Be("corr-1");
    }

    [Fact]
    public void Capabilities_OnlyEligibilityIsSupported()
    {
        IHealthcareTransactionGateway gateway = NewGateway(new StubHttpMessageHandler());

        gateway.Supports(GatewayCapability.Eligibility).Should().BeTrue();
        gateway.Supports(GatewayCapability.ClaimSubmission).Should().BeFalse();
        gateway.Supports(GatewayCapability.ClaimStatus).Should().BeFalse();
        gateway.Supports(GatewayCapability.ClaimAcknowledgment).Should().BeFalse();
        gateway.Supports(GatewayCapability.ClaimAttachment).Should().BeFalse();
        gateway.Supports(GatewayCapability.Remittance).Should().BeFalse();
    }

    [Fact]
    public async Task InvalidConfiguration_FailsWithConfigurationCategory_NoHttpCall()
    {
        var handler = new StubHttpMessageHandler(); // no responses queued
        var badOptions = ValidOptions();
        badOptions.ApiKey = null; // missing key
        var gateway = NewGateway(handler, badOptions);

        var response = await gateway.CheckEligibilityAsync(Request());

        response.IsSuccess.Should().BeFalse();
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.Configuration);
        response.Metadata.Status.Should().Be(GatewayTransactionStatus.Failed);
        response.ErrorMessage.Should().Contain("ApiKey");
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task MissingTenantOrSubscriber_FailsValidation()
    {
        var gateway = NewGateway(new StubHttpMessageHandler());
        var request = Request();
        request.TenantId = "";

        var response = await gateway.CheckEligibilityAsync(request);

        response.IsSuccess.Should().BeFalse();
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.Validation);
    }

    [Fact]
    public async Task MissingPayer_FailsPayerNotFound()
    {
        var gateway = NewGateway(new StubHttpMessageHandler());

        var response = await gateway.CheckEligibilityAsync(Request(payerId: null));

        response.IsSuccess.Should().BeFalse();
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.PayerNotFound);
    }

    [Fact]
    public async Task UnknownPayer_FailsPayerNotFound_NoHttpCall()
    {
        var handler = new StubHttpMessageHandler();
        var gateway = NewGateway(handler);

        var response = await gateway.CheckEligibilityAsync(Request(payerId: "NOT-A-PAYER"));

        response.IsSuccess.Should().BeFalse();
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.PayerNotFound);
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task UnsupportedEligibilityPayer_FailsNotSupported()
    {
        var handler = new StubHttpMessageHandler();
        var gateway = NewGateway(handler);

        var response = await gateway.CheckEligibilityAsync(Request(payerId: "SYNTH-UNSUPPORTED"));

        response.IsSuccess.Should().BeFalse();
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.NotSupported);
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task EnrollmentRequiredPayer_FailsEnrollmentRequired()
    {
        var handler = new StubHttpMessageHandler();
        var gateway = NewGateway(handler);

        var response = await gateway.CheckEligibilityAsync(Request(payerId: "SYNTH-ENROLL"));

        response.IsSuccess.Should().BeFalse();
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.EnrollmentRequired);
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task MissingStediIdentifier_FailsExplicitly()
    {
        var handler = new StubHttpMessageHandler();
        var gateway = NewGateway(handler);

        var response = await gateway.CheckEligibilityAsync(Request(payerId: "SYNTH-NO-EXTERNAL"));

        response.IsSuccess.Should().BeFalse();
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.ExternalIdentifierMissing);
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task AmbiguousPayer_FailsExplicitly()
    {
        var handler = new StubHttpMessageHandler();
        var gateway = NewGateway(handler);

        var response = await gateway.CheckEligibilityAsync(Request(payerId: "SYNTH-DUP"));

        response.IsSuccess.Should().BeFalse();
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.AmbiguousPayer);
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task PayerRejection_IsSurfacedAsRejected()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, RejectionJson);
        var gateway = NewGateway(handler);

        var response = await gateway.CheckEligibilityAsync(Request());

        response.IsSuccess.Should().BeTrue();
        response.Result!.CoverageStatus.Should().Be(GatewayCoverageStatus.Unknown);
        response.Result.RejectionReason.Should().Contain("Invalid/Missing Subscriber ID");
        response.Metadata.Status.Should().Be(GatewayTransactionStatus.Rejected);
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.PayerRejected);
    }

    [Fact]
    public async Task Aaa73_IsPayerRejected_NotTransportFailure()
    {
        const string aaa73 =
            "{\"errors\":[{\"code\":\"73\",\"description\":\"Invalid/Missing Subscriber/Insured Name\"," +
            "\"followupAction\":\"Please Correct and Resubmit\"}]}";
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, aaa73);
        var gateway = NewGateway(handler);

        var response = await gateway.CheckEligibilityAsync(Request());

        response.IsSuccess.Should().BeTrue();
        response.Metadata.Status.Should().Be(GatewayTransactionStatus.Rejected);
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.PayerRejected);
        response.Result!.IsEligible.Should().BeFalse();
        response.Result.CoverageStatus.Should().Be(GatewayCoverageStatus.Unknown);
        response.Result.RejectionReason.Should().Contain("Invalid/Missing Subscriber/Insured Name");
    }

    [Fact]
    public async Task DependentInquiry_SendsDependentsArray_AndNormalizesActiveCoverage()
    {
        const string activeDependentJson =
            "{\"meta\":{\"traceId\":\"trace-dep\",\"applicationMode\":\"test\"}," +
            "\"planStatus\":[{\"statusCode\":\"1\",\"status\":\"Active Coverage\",\"planDetails\":\"CHOICE PLUS\"}]," +
            "\"planInformation\":{\"planNumber\":\"P1\",\"groupNumber\":\"186084\"}," +
            "\"benefitsInformation\":[{\"code\":\"1\",\"name\":\"Health Benefit Plan Coverage\",\"serviceTypeCodes\":[\"30\"]}]," +
            "\"subscriber\":{\"memberId\":\"UHC202649\",\"firstName\":\"John\",\"lastName\":\"Doe\"}," +
            "\"dependents\":[{\"firstName\":\"Jane\",\"lastName\":\"Doe\",\"dateOfBirth\":\"19521121\",\"relationToSubscriber\":\"Spouse\"}]}";
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, activeDependentJson);
        var gateway = NewGateway(handler);

        var request = Request("60054");
        request.SubscriberId = "UHC202649";
        request.SubscriberFirstName = "John";
        request.SubscriberLastName = "Doe";
        request.Patient = new GatewayEligibilityPerson
        {
            FirstName = "Jane",
            LastName = "Doe",
            DateOfBirth = new DateOnly(1952, 11, 21)
        };

        var response = await gateway.CheckEligibilityAsync(request);

        response.IsSuccess.Should().BeTrue();
        response.Metadata.Status.Should().Be(GatewayTransactionStatus.Completed);
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.None);
        response.Result!.CoverageStatus.Should().Be(GatewayCoverageStatus.Active);
        response.Result.Patient!.FirstName.Should().Be("Jane");
        handler.RequestBodies[0].Should().Contain("\"dependents\"");
        handler.RequestBodies[0].Should().Contain("Jane");
        handler.RequestBodies[0].Should().Contain("19521121");
        handler.RequestBodies[0].Should().Contain("UHC202649");
    }

    [Fact]
    public async Task HttpAuthFailure_IsNormalizedToAuthenticationCategory()
    {
        var handler = new StubHttpMessageHandler().EnqueueStatus(HttpStatusCode.Unauthorized);
        var gateway = NewGateway(handler);

        var response = await gateway.CheckEligibilityAsync(Request());

        response.IsSuccess.Should().BeFalse();
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.Authentication);
        response.Metadata.Status.Should().Be(GatewayTransactionStatus.Failed);
    }

    [Fact]
    public async Task RetryCount_FlowsIntoMetadata()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueStatus(HttpStatusCode.InternalServerError)
            .EnqueueJson(HttpStatusCode.OK, ActiveJson);
        var gateway = NewGateway(handler);

        var response = await gateway.CheckEligibilityAsync(Request());

        response.IsSuccess.Should().BeTrue();
        response.Metadata.RetryCount.Should().Be(1);
    }
}
