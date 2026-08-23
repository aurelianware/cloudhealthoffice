using System.Net;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Gateways.Stedi;
using CloudHealthOffice.Infrastructure.Gateways.Stedi.Mapping;
using CloudHealthOffice.Infrastructure.ReferenceData.Payers;
using CloudHealthOffice.Infrastructure.Tests.Gateways;
using CloudHealthOffice.Infrastructure.Tests.ReferenceData.Payers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways.Stedi;

public class StediClaimSubmissionTests
{
    private const string SuccessJson =
        "{\"status\":\"SUCCESS\",\"controlNumber\":\"000000001\"," +
        "\"claimReference\":{\"correlationId\":\"01CLAIMCORR\",\"rhclaimNumber\":\"01CLAIMCORR\",\"patientControlNumber\":\"CLM-P-1001\"}," +
        "\"meta\":{\"traceId\":\"trace-claim-1\"}}";

    private static StediGatewayOptions ValidOptions() => new()
    {
        ApiKey = "test-key",
        BaseUrl = "https://healthcare.test",
        Environment = "sandbox",
        EligibilityPath = "/eligibility/v3",
        ProfessionalClaimPath = "/professionalclaims/v3/submission",
        InstitutionalClaimPath = "/institutionalclaims/v1/submission",
        DentalClaimPath = "/dental-claims/submission",
        MaxRetries = 1
    };

    private static StediHealthcareGateway NewGateway(
        StubHttpMessageHandler handler,
        IClaimTransmissionStore? store = null,
        StediGatewayOptions? options = null)
    {
        options ??= ValidOptions();
        var opts = Options.Create(options);
        var factory = new StubHttpClientFactory(handler);
        var eligibility = new StediEligibilityApiClient(
            factory, opts, NullLogger<StediEligibilityApiClient>.Instance, delay: (_, _) => Task.CompletedTask);
        var claims = new StediClaimApiClient(
            factory, opts, NullLogger<StediClaimApiClient>.Instance, delay: (_, _) => Task.CompletedTask);
        return new StediHealthcareGateway(
            eligibility, PayerTestHarness.CreateResolver(opts), opts,
            NullLogger<StediHealthcareGateway>.Instance, timeProvider: null, claims, store);
    }

    [Fact]
    public async Task ProfessionalClaim_Success_IsTransmissionAcceptedNotPaid()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, SuccessJson);
        var gateway = NewGateway(handler);

        var response = await gateway.SubmitClaimAsync(GatewayClaimFixtures.Professional());

        response.IsSuccess.Should().BeTrue();
        response.Result!.AcceptedForProcessing.Should().BeTrue();
        response.Result.TransmissionStatus.Should().Be(GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway);
        response.Result.SubmissionId.Should().Be("01CLAIMCORR");
        response.Metadata.TransactionType.Should().Be(HealthcareTransactionType.ProfessionalClaim837P);
        response.Metadata.Status.Should().Be(GatewayTransactionStatus.Completed);
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.None);
        handler.CallCount.Should().Be(1);
        handler.Requests[0].RequestUri!.ToString().Should().Contain("professionalclaims");
        handler.Requests[0].Headers.Contains("Idempotency-Key").Should().BeTrue();
    }

    [Fact]
    public void Mapper_Professional_UsesDocumentedJsonFields()
    {
        var dto = StediClaimMapper.ToStediRequest(GatewayClaimFixtures.Professional(), "60054", "T");
        var json = JsonSerializer.Serialize(dto, StediHttpSender.JsonOptions);

        json.Should().Contain("\"tradingPartnerServiceId\":\"60054\"");
        json.Should().Contain("\"usageIndicator\":\"T\"");
        json.Should().Contain("\"npi\":\"1999999984\"");
        json.Should().Contain("\"procedureCode\":\"90837\"");
        json.Should().Contain("\"claimChargeAmount\":\"109.20\"");
        json.Should().Contain("professionalService");
        json.Should().NotContain("stedi", because: "canonical mapping should not invent extra vendor wrappers");
    }

    [Fact]
    public void Mapper_Dental_IncludesToothFields()
    {
        var dto = StediClaimMapper.ToStediRequest(GatewayClaimFixtures.Dental(), "60054", "T");
        var dental = dto.ClaimInformation.ServiceLines[0].DentalService;
        dental.Should().NotBeNull();
        dental!.ToothCode.Should().Be("14");
        dental.ProcedureCode.Should().Be("D0120");
        dto.ClaimInformation.ServiceLines[0].ProfessionalService.Should().BeNull();
    }

    [Fact]
    public void Mapper_Institutional_UsesRevenueAndTypeOfBillPath()
    {
        var dto = StediClaimMapper.ToStediRequest(GatewayClaimFixtures.Institutional(), "60054", "T");
        dto.ClaimInformation.ServiceLines[0].InstitutionalService!.ServiceLineRevenueCode.Should().Be("0124");
        dto.ClaimInformation.PrincipalDiagnosis!.PrincipalDiagnosisCode.Should().Be("R45851");
    }

    [Fact]
    public async Task IdempotentReplay_DoesNotCallStediAgain()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, SuccessJson);
        var store = new InMemoryClaimTransmissionStore();
        var gateway = NewGateway(handler, store);

        var first = await gateway.SubmitClaimAsync(GatewayClaimFixtures.Professional());
        var second = await gateway.SubmitClaimAsync(GatewayClaimFixtures.Professional());

        handler.CallCount.Should().Be(1);
        second.Result!.ReplayOfExistingTransmission.Should().BeTrue();
        second.Result.TransmissionId.Should().Be(first.Result!.TransmissionId);
    }

    [Fact]
    public async Task NewClaimVersion_MayResubmit()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.OK, SuccessJson)
            .EnqueueJson(HttpStatusCode.OK, SuccessJson);
        var gateway = NewGateway(handler);

        await gateway.SubmitClaimAsync(GatewayClaimFixtures.Professional(version: 1));
        var second = await gateway.SubmitClaimAsync(GatewayClaimFixtures.Professional(version: 2));

        handler.CallCount.Should().Be(2);
        second.Result!.ReplayOfExistingTransmission.Should().BeFalse();
    }

    [Fact]
    public async Task Http400_IsValidation_NotRetried()
    {
        var handler = new StubHttpMessageHandler().EnqueueStatus(HttpStatusCode.BadRequest);
        var gateway = NewGateway(handler);

        var response = await gateway.SubmitClaimAsync(GatewayClaimFixtures.Professional());

        response.IsSuccess.Should().BeFalse();
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.Validation);
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Http429_IsRetried()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueStatus(HttpStatusCode.TooManyRequests, r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1)))
            .EnqueueJson(HttpStatusCode.OK, SuccessJson);
        var gateway = NewGateway(handler);

        var response = await gateway.SubmitClaimAsync(GatewayClaimFixtures.Professional());

        response.IsSuccess.Should().BeTrue();
        handler.CallCount.Should().Be(2);
        response.Metadata.RetryCount.Should().Be(1);
    }

    [Fact]
    public async Task Http401_IsAuthentication()
    {
        var handler = new StubHttpMessageHandler().EnqueueStatus(HttpStatusCode.Unauthorized);
        var response = await NewGateway(handler).SubmitClaimAsync(GatewayClaimFixtures.Professional());
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.Authentication);
    }

    [Fact]
    public async Task UnknownPayer_DoesNotCallStedi()
    {
        var handler = new StubHttpMessageHandler();
        var response = await NewGateway(handler).SubmitClaimAsync(
            GatewayClaimFixtures.Professional(payerId: "NO-SUCH-PAYER"));

        response.IsSuccess.Should().BeFalse();
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.PayerNotFound);
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task EnrollmentRequiredPayer_IsBlocked()
    {
        var handler = new StubHttpMessageHandler();
        var response = await NewGateway(handler).SubmitClaimAsync(
            GatewayClaimFixtures.Professional(payerId: SyntheticPayerSeed.EnrollmentId));

        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.EnrollmentRequired);
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task UnsupportedPayer_IsBlocked()
    {
        var handler = new StubHttpMessageHandler();
        var response = await NewGateway(handler).SubmitClaimAsync(
            GatewayClaimFixtures.Professional(payerId: SyntheticPayerSeed.UnsupportedId));

        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.NotSupported);
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task MissingExternalIdentifier_IsBlocked()
    {
        var handler = new StubHttpMessageHandler();
        var request = GatewayClaimFixtures.Professional(payerId: SyntheticPayerSeed.MissingExternalId);
        var response = await NewGateway(handler).SubmitClaimAsync(request);

        handler.CallCount.Should().Be(0);
        response.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task PhiIsNotLogged()
    {
        var logger = new CapturingLogger<StediHealthcareGateway>();
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, SuccessJson);
        var options = ValidOptions();
        var opts = Options.Create(options);
        var factory = new StubHttpClientFactory(handler);
        var gateway = new StediHealthcareGateway(
            new StediEligibilityApiClient(factory, opts, NullLogger<StediEligibilityApiClient>.Instance, delay: (_, _) => Task.CompletedTask),
            PayerTestHarness.CreateResolver(opts),
            opts,
            logger,
            timeProvider: null,
            new StediClaimApiClient(factory, opts, NullLogger<StediClaimApiClient>.Instance, delay: (_, _) => Task.CompletedTask),
            new InMemoryClaimTransmissionStore());

        var request = GatewayClaimFixtures.Professional();
        request.Subscriber!.LastName = "Zzytestphisurname";
        request.Subscriber.MemberId = "PHI-MEMBER-ZX9Q";
        request.Diagnoses[0].Code = "E119";

        await gateway.SubmitClaimAsync(request);

        var logs = string.Join("\n", logger.Messages);
        logs.Should().NotContain("Zzytestphisurname");
        logs.Should().NotContain("PHI-MEMBER-ZX9Q");
        logs.Should().NotContain("E119");
        logs.Should().NotContain("90837");
        logs.Should().NotContain("test-key");
        logs.Should().Contain("ProfessionalClaim837P");
        logs.Should().Contain("tenant-alpha");
    }
}
