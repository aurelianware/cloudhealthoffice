using System.Net;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Gateways.Stedi;
using CloudHealthOffice.Infrastructure.Gateways.Stedi.Mapping;
using CloudHealthOffice.Infrastructure.Tests.Gateways;
using CloudHealthOffice.Infrastructure.Tests.ReferenceData.Payers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways.Stedi;

public class StediRemittanceTests
{
    internal const string PaidJson =
        """
        {"meta":{"transactionId":"era-txn-1"},"transactions":[{"controlNumber":"000000001",
        "payer":{"name":"Synthetic Payer","payerId":"60054"},
        "payee":{"npi":"1999999984","name":"Therapy Associates"},
        "paymentAndRemitReassociationDetails":{"traceTypeCode":"1","checkOrEFTTraceNumber":"EFT-TRACE-1"},
        "financialInformation":{"transactionHandlingCode":"I","totalActualProviderPaymentAmount":"320.00",
        "creditOrDebitFlagCode":"C","paymentMethodCode":"ACH","checkIssueOrEFTEffectiveDate":"20260120"},
        "detailInfo":[{"assignedNumber":"1","paymentInfo":[{"claimPaymentInfo":{
        "patientControlNumber":"CLM-P-1001","claimStatusCode":"1","totalClaimChargeAmount":"500.00",
        "claimPaymentAmount":"320.00","patientResponsibilityAmount":"80.00","payerClaimControlNumber":"PAYER-CCN-9"},
        "claimAdjustments":[{"claimAdjustmentGroupCode":"CO","adjustmentReasonCode1":"45","adjustmentAmount1":"100.00"},
        {"claimAdjustmentGroupCode":"PR","adjustmentReasonCode1":"1","adjustmentAmount1":"50.00",
        "adjustmentReasonCode2":"2","adjustmentAmount2":"30.00"}],
        "serviceLines":[{"serviceIdQualifier":"HC","adjudicatedProcedureCode":"90837","lineItemControlNumber":"1",
        "lineItemChargeAmount":"500.00","lineItemProviderPaymentAmount":"320.00",
        "serviceAdjustments":[{"adjustmentGroupCode":"CO","adjustmentReasonCode1":"45","adjustmentAmount1":"100.00"}]}]}]}]}]}
        """;

    internal const string DentalJson =
        """
        {"meta":{"transactionId":"era-dental-1"},"transactions":[{"financialInformation":{
        "totalActualProviderPaymentAmount":"90.00","paymentMethodCode":"CHK","checkIssueOrEFTEffectiveDate":"20260315"},
        "paymentAndRemitReassociationDetails":{"checkOrEFTTraceNumber":"CHK-9"},
        "detailInfo":[{"paymentInfo":[{"claimPaymentInfo":{"patientControlNumber":"CLM-D-3001","claimStatusCode":"1",
        "totalClaimChargeAmount":"150.00","claimPaymentAmount":"90.00","patientResponsibilityAmount":"20.00",
        "payerClaimControlNumber":"DENTAL-CCN"},
        "serviceLines":[{"serviceIdQualifier":"AD","procedureCode":"D0120","lineItemControlNumber":"1",
        "lineItemChargeAmount":"150.00","lineItemProviderPaymentAmount":"90.00","toothCode":"14"}]}]}]}]}
        """;

    private static StediGatewayOptions ValidOptions() => new()
    {
        ApiKey = "test-key",
        BaseUrl = "https://healthcare.test",
        Environment = "sandbox",
        EligibilityPath = "/eligibility/v3",
        RemittanceReportPath = "/2024-04-01/change/medicalnetwork/reports/v2/{transactionId}/835",
        MaxRetries = 1
    };

    private static StediHealthcareGateway NewGateway(StubHttpMessageHandler handler)
    {
        var opts = Options.Create(ValidOptions());
        var factory = new StubHttpClientFactory(handler);
        var eligibility = new StediEligibilityApiClient(
            factory, opts, NullLogger<StediEligibilityApiClient>.Instance, delay: (_, _) => Task.CompletedTask);
        var remittance = new StediRemittanceApiClient(
            factory, opts, NullLogger<StediRemittanceApiClient>.Instance, delay: (_, _) => Task.CompletedTask);
        return new StediHealthcareGateway(
            eligibility, PayerTestHarness.CreateResolver(opts), opts,
            NullLogger<StediHealthcareGateway>.Instance,
            remittanceClient: remittance);
    }

    [Fact]
    public async Task Retrieve_Success_IsNormalized()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, PaidJson);
        var gateway = NewGateway(handler);

        var response = await gateway.RetrieveRemittanceAsync(new RemittanceRetrievalRequest
        {
            ExternalRemittanceId = "era-txn-1",
            EventId = "evt-era"
        });

        response.IsSuccess.Should().BeTrue();
        response.Result!.PaymentAmount.Should().Be(320m);
        response.Result.PaymentMethodCode.Should().Be("ACH");
        response.Result.PaymentDate.Should().Be(new DateOnly(2026, 1, 20));
        response.Result.Claims.Should().ContainSingle();
        response.Result.Claims[0].PayerClaimControlNumber.Should().Be("PAYER-CCN-9");
        response.Result.Claims[0].PatientControlNumber.Should().Be("CLM-P-1001");
        response.Result.Claims[0].PaidAmount.Should().Be(320m);
        response.Result.Claims[0].Adjustments.Should().Contain(a => a.Kind == RemittanceAdjustmentKind.Contractual);
        response.Result.Claims[0].ServiceLines.Should().ContainSingle(l => l.ProcedureCode == "90837");
        response.Metadata.TransactionType.Should().Be(HealthcareTransactionType.Remittance835);
        handler.Requests[0].RequestUri!.ToString().Should().Contain("/835");
    }

    [Fact]
    public void Mapper_Dental_PreservesCdtAndTooth()
    {
        var dto = JsonSerializer.Deserialize<CloudHealthOffice.Infrastructure.Gateways.Stedi.Models.Stedi835ReportDto>(
            DentalJson, StediHttpSender.JsonOptions)!;
        var canonical = StediRemittanceMapper.ToCanonical(dto, DateTimeOffset.UtcNow, "evt");
        canonical.Claims[0].ServiceLines[0].ProcedureQualifier.Should().Be("AD");
        canonical.Claims[0].ServiceLines[0].ProcedureCode.Should().Be("D0120");
        canonical.Claims[0].ServiceLines[0].ToothNumber.Should().Be("14");
    }

    [Theory]
    [InlineData("PR", "1", RemittanceAdjustmentKind.Deductible)]
    [InlineData("PR", "2", RemittanceAdjustmentKind.Coinsurance)]
    [InlineData("PR", "3", RemittanceAdjustmentKind.Copay)]
    [InlineData("CO", "45", RemittanceAdjustmentKind.Contractual)]
    [InlineData("CO", "96", RemittanceAdjustmentKind.NonCovered)]
    public void Mapper_ClassifiesAdjustments(string group, string reason, RemittanceAdjustmentKind kind) =>
        StediRemittanceMapper.Classify(group, reason).Should().Be(kind);

    [Fact]
    public async Task Http400_IsValidation_NotRetried()
    {
        var handler = new StubHttpMessageHandler().EnqueueStatus(HttpStatusCode.BadRequest);
        var response = await NewGateway(handler).RetrieveRemittanceAsync(new RemittanceRetrievalRequest
        {
            ExternalRemittanceId = "era-txn-1"
        });
        response.IsSuccess.Should().BeFalse();
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.Validation);
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Http401_IsAuthentication()
    {
        var handler = new StubHttpMessageHandler().EnqueueStatus(HttpStatusCode.Unauthorized);
        var response = await NewGateway(handler).RetrieveRemittanceAsync(new RemittanceRetrievalRequest
        {
            ExternalRemittanceId = "era-txn-1"
        });
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.Authentication);
    }

    [Fact]
    public async Task Http403_IsAuthorization()
    {
        var handler = new StubHttpMessageHandler().EnqueueStatus(HttpStatusCode.Forbidden);
        var response = await NewGateway(handler).RetrieveRemittanceAsync(new RemittanceRetrievalRequest
        {
            ExternalRemittanceId = "era-txn-1"
        });
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.Authorization);
    }

    [Fact]
    public async Task Http429_IsRetried()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueStatus(HttpStatusCode.TooManyRequests)
            .EnqueueJson(HttpStatusCode.OK, PaidJson);
        var response = await NewGateway(handler).RetrieveRemittanceAsync(new RemittanceRetrievalRequest
        {
            ExternalRemittanceId = "era-txn-1"
        });
        response.IsSuccess.Should().BeTrue();
        response.Metadata.RetryCount.Should().Be(1);
    }

    [Fact]
    public async Task Http5xx_IsRetried()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueStatus(HttpStatusCode.InternalServerError)
            .EnqueueJson(HttpStatusCode.OK, PaidJson);
        var response = await NewGateway(handler).RetrieveRemittanceAsync(new RemittanceRetrievalRequest
        {
            ExternalRemittanceId = "era-txn-1"
        });
        response.IsSuccess.Should().BeTrue();
        response.Metadata.RetryCount.Should().Be(1);
    }

    [Fact]
    public async Task Timeout_IsTimeout()
    {
        var handler = new StubHttpMessageHandler().EnqueueThrow(new TaskCanceledException());
        var response = await NewGateway(handler).RetrieveRemittanceAsync(new RemittanceRetrievalRequest
        {
            ExternalRemittanceId = "era-txn-1"
        });
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.Timeout);
    }

    [Fact]
    public async Task NetworkError_IsConnectivity()
    {
        var handler = new StubHttpMessageHandler().EnqueueThrow(new HttpRequestException("boom"));
        var response = await NewGateway(handler).RetrieveRemittanceAsync(new RemittanceRetrievalRequest
        {
            ExternalRemittanceId = "era-txn-1"
        });
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.Connectivity);
    }

    [Fact]
    public async Task MalformedResponse_IsMalformed()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, "{not-json");
        var response = await NewGateway(handler).RetrieveRemittanceAsync(new RemittanceRetrievalRequest
        {
            ExternalRemittanceId = "era-txn-1"
        });
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.MalformedResponse);
    }

    [Fact]
    public async Task MissingId_IsValidation_NoHttp()
    {
        var handler = new StubHttpMessageHandler();
        var response = await NewGateway(handler).RetrieveRemittanceAsync(new RemittanceRetrievalRequest());
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.Validation);
        handler.CallCount.Should().Be(0);
    }
}
