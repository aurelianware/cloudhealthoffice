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

public class StediClaimStatusTests
{
    private const string PaidJson =
        """{"meta":{"traceId":"trace-276-1","transactionId":"txn-276-1"},"claims":[{"claimStatus":{"statusCategoryCode":"F1","statusCategoryCodeValue":"Finalized/Payment","statusCode":"65","statusCodeValue":"Claim/line has been paid.","effectiveDate":"20260120","submittedAmount":"109.20","amountPaid":"109.20","tradingPartnerClaimNumber":"PAYER-CCN-9","patientAccountNumber":"CLM-P-1001"},"serviceDetails":[{"procedureCode":"90837","lineItemControlNumber":"1","submittedAmount":"109.20","amountPaid":"109.20","status":[{"statusCategoryCode":"F1","statusCode":"65","statusCodeValue":"Claim/line has been paid."}]}]}]}""";

    private static StediGatewayOptions ValidOptions() => new()
    {
        ApiKey = "test-key",
        BaseUrl = "https://healthcare.test",
        Environment = "sandbox",
        EligibilityPath = "/eligibility/v3",
        ClaimStatusPath = "/2024-04-01/change/medicalnetwork/claimstatus/v2",
        MaxRetries = 1
    };

    private static (StediHealthcareGateway Gateway, StubHttpMessageHandler Handler,
        InMemoryClaimTransmissionStore Transmissions, InMemoryClaimAcknowledgmentStore Acks,
        InMemoryClaimStatusInquiryStore Inquiries)
        NewGateway(StubHttpMessageHandler? handler = null, StediGatewayOptions? options = null)
    {
        handler ??= new StubHttpMessageHandler();
        options ??= ValidOptions();
        var opts = Options.Create(options);
        var factory = new StubHttpClientFactory(handler);
        var transmissions = new InMemoryClaimTransmissionStore();
        var acks = new InMemoryClaimAcknowledgmentStore();
        var inquiries = new InMemoryClaimStatusInquiryStore();
        var eligibility = new StediEligibilityApiClient(
            factory, opts, NullLogger<StediEligibilityApiClient>.Instance, delay: (_, _) => Task.CompletedTask);
        var claims = new StediClaimApiClient(
            factory, opts, NullLogger<StediClaimApiClient>.Instance, delay: (_, _) => Task.CompletedTask);
        var status = new StediClaimStatusApiClient(
            factory, opts, NullLogger<StediClaimStatusApiClient>.Instance, delay: (_, _) => Task.CompletedTask);
        var gateway = new StediHealthcareGateway(
            eligibility, PayerTestHarness.CreateResolver(opts), opts,
            NullLogger<StediHealthcareGateway>.Instance,
            claimClient: claims,
            transmissions: transmissions,
            acknowledgments: acks,
            statusInquiries: inquiries,
            statusClient: status);
        return (gateway, handler, transmissions, acks, inquiries);
    }

    [Fact]
    public async Task Success_NormalizesPaidWithoutTouching277ca()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.OK,
                "{\"status\":\"SUCCESS\",\"claimReference\":{\"correlationId\":\"01CLAIMCORR\",\"patientControlNumber\":\"CLM-P-1001\"},\"meta\":{\"traceId\":\"trace-claim-1\"}}")
            .EnqueueJson(HttpStatusCode.OK, PaidJson);
        var (gateway, _, transmissions, acks, _) = NewGateway(handler);
        var submitted = await gateway.SubmitClaimAsync(GatewayClaimFixtures.Professional());
        submitted.IsSuccess.Should().BeTrue();
        var tx = await transmissions.GetByIdAsync(submitted.Result!.TransmissionId);
        var processor = new ClaimAcknowledgmentProcessor(
            acks, transmissions, NullLogger<ClaimAcknowledgmentProcessor>.Instance);
        await processor.ProcessAsync(new GatewayClaimAcknowledgment
        {
            AcknowledgmentId = "ack-stedi",
            Gateway = "Stedi",
            TransmissionId = tx!.TransmissionId,
            OriginalSubmissionId = tx.SubmissionId,
            Status = ClaimAcknowledgmentStatus.Accepted,
            ClaimControlNumber = "PAYER-CCN-9",
            ReceivedAt = DateTimeOffset.UtcNow
        });

        var response = await gateway.CheckClaimStatusAsync(new ClaimStatusRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = tx.TransmissionId
        });

        response.IsSuccess.Should().BeTrue();
        response.Result!.Status.Should().Be(GatewayClaimStatus.Paid);
        response.Result.StatusCategoryCode.Should().Be("F1");
        response.Result.StatusCode.Should().Be("65");
        response.Result.PayerClaimControlNumber.Should().Be("PAYER-CCN-9");
        response.Result.ServiceLineStatuses.Should().ContainSingle(l => l.LineNumber == 1);
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.None);
        handler.Requests.Last().RequestUri!.ToString().Should().Contain("claimstatus/v2");
        (await acks.ListByTransmissionIdAsync(tx.TransmissionId)).Single().Status
            .Should().Be(ClaimAcknowledgmentStatus.Accepted);
        (await transmissions.GetByIdAsync(tx.TransmissionId))!.Status
            .Should().Be(GatewayClaimTransmissionStatus.AcknowledgmentAccepted);
    }

    [Theory]
    [InlineData("{\"claims\":[{\"claimStatus\":{\"statusCategoryCode\":\"A1\",\"statusCode\":\"20\"}}]}", GatewayClaimStatus.Received)]
    [InlineData("{\"claims\":[{\"claimStatus\":{\"statusCategoryCode\":\"P1\",\"statusCode\":\"20\"}}]}", GatewayClaimStatus.InProcess)]
    [InlineData("{\"claims\":[{\"claimStatus\":{\"statusCategoryCode\":\"P2\",\"statusCode\":\"20\"}}]}", GatewayClaimStatus.Pending)]
    [InlineData("{\"claims\":[{\"claimStatus\":{\"statusCategoryCode\":\"F4\",\"statusCode\":\"102\"}}]}", GatewayClaimStatus.Finalized)]
    [InlineData("{\"claims\":[{\"claimStatus\":{\"statusCategoryCode\":\"F2\",\"statusCode\":\"27\"}}]}", GatewayClaimStatus.Denied)]
    [InlineData("{\"claims\":[{\"claimStatus\":{\"statusCategoryCode\":\"A4\",\"statusCode\":\"35\"}}]}", GatewayClaimStatus.NoRecordFound)]
    [InlineData("{\"claims\":[{\"claimStatus\":{\"statusCategoryCode\":\"R3\",\"statusCode\":\"21\"}}]}", GatewayClaimStatus.AdditionalInformationRequested)]
    [InlineData("{\"claims\":[{\"claimStatus\":{\"statusCategoryCode\":\"ZZ\",\"statusCode\":\"99\"}}]}", GatewayClaimStatus.Unknown)]
    [InlineData("{\"claims\":[]}", GatewayClaimStatus.NoRecordFound)]
    public void Mapper_NormalizesDocumentedStatusCategories(string json, GatewayClaimStatus expected)
    {
        var dto = JsonSerializer.Deserialize<CloudHealthOffice.Infrastructure.Gateways.Stedi.Models.StediClaimStatusResponseDto>(
            json, StediHttpSender.JsonOptions)!;
        var request = new ClaimStatusRequest { ClaimId = "CLM-P-1001", TenantId = "tenant-alpha" };
        var canonical = StediClaimStatusMapper.ToCanonical(dto, request);
        canonical.Status.Should().Be(expected);
    }

    [Fact]
    public void Mapper_PartiallyPaid_WhenF1AndPaidLessThanSubmitted()
    {
        var dto = JsonSerializer.Deserialize<CloudHealthOffice.Infrastructure.Gateways.Stedi.Models.StediClaimStatusResponseDto>(
            """{"claims":[{"claimStatus":{"statusCategoryCode":"F1","statusCode":"65","submittedAmount":"100.00","amountPaid":"40.00"}}]}""",
            StediHttpSender.JsonOptions)!;
        var canonical = StediClaimStatusMapper.ToCanonical(dto, new ClaimStatusRequest());
        canonical.Status.Should().Be(GatewayClaimStatus.PartiallyPaid);
        canonical.PaidAmount.Should().Be(40.00m);
        canonical.ClaimAmount.Should().Be(100.00m);
    }

    [Fact]
    public void Mapper_Professional_UsesBaseJsonAndPayerControlNumber()
    {
        var request = new ClaimStatusRequest
        {
            TenantId = "tenant-alpha",
            ClaimId = "CLM-P-1001",
            PayerClaimControlNumber = "PAYER-CCN-9",
            PatientControlNumber = "CLM-P-1001",
            ServiceDateFrom = new DateOnly(2026, 1, 15),
            ServiceDateTo = new DateOnly(2026, 1, 15),
            Provider = new GatewayClaimProvider { Npi = "1999999984", OrganizationName = "Therapy Associates" },
            Subscriber = new GatewayEligibilityPerson
            {
                MemberId = "U7777788888",
                FirstName = "John",
                LastName = "Anon",
                DateOfBirth = new DateOnly(2000, 1, 1),
                Gender = "M"
            },
            ClaimType = GatewayClaimType.Professional
        };

        var json = JsonSerializer.Serialize(
            StediClaimStatusMapper.ToStediRequest(request, "60054"), StediHttpSender.JsonOptions);

        json.Should().Contain("\"tradingPartnerServiceId\":\"60054\"");
        json.Should().Contain("\"npi\":\"1999999984\"");
        json.Should().Contain("\"providerType\":\"BillingProvider\"");
        json.Should().Contain("\"memberId\":\"U7777788888\"");
        json.Should().Contain("\"tradingPartnerClaimNumber\":\"PAYER-CCN-9\"");
        json.Should().NotContain("patientAccountNumber");
        json.Should().Contain("\"beginningDateOfService\"");
        json.Should().Contain("\"dateOfBirth\":\"20000101\"");
        json.Should().Contain("\"gender\":\"M\"");
    }

    [Fact]
    public void Mapper_FallsBackToPatientControlNumber()
    {
        var request = new ClaimStatusRequest
        {
            PatientControlNumber = "CLM-P-1001",
            ServiceDateFrom = new DateOnly(2026, 1, 15),
            Provider = new GatewayClaimProvider { Npi = "1999999984", OrganizationName = "Therapy Associates" },
            Subscriber = new GatewayEligibilityPerson
            {
                MemberId = "U7777788888", FirstName = "John", LastName = "Anon"
            }
        };
        var json = JsonSerializer.Serialize(
            StediClaimStatusMapper.ToStediRequest(request, "60054"), StediHttpSender.JsonOptions);
        json.Should().Contain("\"patientAccountNumber\":\"CLM-P-1001\"");
        json.Should().NotContain("tradingPartnerClaimNumber");
    }

    [Fact]
    public void Mapper_ServiceLine_UsesDocumentedArray_NotDeprecatedObject()
    {
        var request = new ClaimStatusRequest
        {
            ServiceDateFrom = new DateOnly(2026, 1, 15),
            Provider = new GatewayClaimProvider { Npi = "1999999984", OrganizationName = "Therapy Associates" },
            Subscriber = new GatewayEligibilityPerson
            {
                MemberId = "U7777788888", FirstName = "John", LastName = "Anon"
            },
            ServiceLineNumber = 1,
            ClaimType = GatewayClaimType.Professional,
            ServiceLines =
            {
                new ClaimStatusLineSource
                {
                    LineNumber = 1,
                    ProcedureCode = "90837",
                    ChargeAmount = 109.20m,
                    Units = 1,
                    ServiceDateFrom = new DateOnly(2026, 1, 15),
                    Modifiers = { "95" }
                }
            }
        };
        var json = JsonSerializer.Serialize(
            StediClaimStatusMapper.ToStediRequest(request, "60054"), StediHttpSender.JsonOptions);
        json.Should().Contain("serviceLinesInformation");
        json.Should().Contain("\"procedureCode\":\"90837\"");
        json.Should().Contain("\"productOrServiceIDQualifier\":\"HC\"");
        json.Should().NotContain("serviceLineInformation\":{");
    }

    [Fact]
    public void Mapper_ServiceLineWithoutDetails_ThrowsInsteadOfWidening()
    {
        var request = new ClaimStatusRequest
        {
            ServiceDateFrom = new DateOnly(2026, 1, 15),
            Provider = new GatewayClaimProvider { Npi = "1999999984", OrganizationName = "Therapy Associates" },
            Subscriber = new GatewayEligibilityPerson
            {
                MemberId = "U7777788888", FirstName = "John", LastName = "Anon"
            },
            ServiceLineNumber = 1
        };

        var act = () => StediClaimStatusMapper.ToStediRequest(request, "60054");

        act.Should().Throw<InvalidOperationException>().WithMessage("*line details*");
    }

    [Fact]
    public void Mapper_DentalLine_UsesAdQualifier()
    {
        var request = new ClaimStatusRequest
        {
            ServiceDateFrom = new DateOnly(2026, 3, 1),
            Provider = new GatewayClaimProvider { Npi = "1999999984", OrganizationName = "Dental Group" },
            Subscriber = new GatewayEligibilityPerson
            {
                MemberId = "D1", FirstName = "Jane", LastName = "Doe"
            },
            ServiceLineNumber = 1,
            ClaimType = GatewayClaimType.Dental,
            ServiceLines =
            {
                new ClaimStatusLineSource
                {
                    LineNumber = 1, ProcedureCode = "D0120", ChargeAmount = 150m, Units = 1
                }
            }
        };
        var json = JsonSerializer.Serialize(
            StediClaimStatusMapper.ToStediRequest(request, "60054"), StediHttpSender.JsonOptions);
        json.Should().Contain("\"productOrServiceIDQualifier\":\"AD\"");
    }

    [Fact]
    public void Mapper_Institutional_IncludesBillingType()
    {
        var request = new ClaimStatusRequest
        {
            ServiceDateFrom = new DateOnly(2026, 2, 1),
            ServiceDateTo = new DateOnly(2026, 2, 3),
            TypeOfBill = "111",
            Provider = new GatewayClaimProvider { Npi = "1999999984", OrganizationName = "Demo Hospital" },
            Subscriber = new GatewayEligibilityPerson
            {
                MemberId = "MBR-INST", FirstName = "Jane", LastName = "Doe"
            },
            ClaimType = GatewayClaimType.Institutional
        };
        var json = JsonSerializer.Serialize(
            StediClaimStatusMapper.ToStediRequest(request, "60054"), StediHttpSender.JsonOptions);
        json.Should().Contain("\"billingType\":\"111\"");
    }

    [Fact]
    public void Mapper_Dependent_EmittedWhenPatientDiffersFromSubscriber()
    {
        var request = new ClaimStatusRequest
        {
            ServiceDateFrom = new DateOnly(2026, 1, 15),
            Provider = new GatewayClaimProvider { Npi = "1999999984", OrganizationName = "Therapy Associates" },
            Subscriber = new GatewayEligibilityPerson
            {
                MemberId = "SUB1", FirstName = "Jane", LastName = "Doe", DateOfBirth = new DateOnly(1980, 1, 1)
            },
            Patient = new GatewayEligibilityPerson
            {
                FirstName = "John", LastName = "Doe", DateOfBirth = new DateOnly(2010, 1, 1)
            }
        };
        var json = JsonSerializer.Serialize(
            StediClaimStatusMapper.ToStediRequest(request, "60054"), StediHttpSender.JsonOptions);
        json.Should().Contain("\"dependent\"");
        json.Should().Contain("\"firstName\":\"John\"");
    }

    [Fact]
    public async Task Http400_IsValidation_NotRetried()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.OK,
                "{\"status\":\"SUCCESS\",\"claimReference\":{\"correlationId\":\"c1\",\"patientControlNumber\":\"CLM-P-1001\"}}")
            .EnqueueStatus(HttpStatusCode.BadRequest);
        var (gateway, _, transmissions, _, _) = NewGateway(handler);
        await gateway.SubmitClaimAsync(GatewayClaimFixtures.Professional());
        var tx = (await transmissions.FindByTenantAndClaimIdAsync("tenant-alpha", "CLM-P-1001")).Single();

        var response = await gateway.CheckClaimStatusAsync(new ClaimStatusRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = tx.TransmissionId
        });

        response.IsSuccess.Should().BeFalse();
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.Validation);
        handler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task Http401_IsAuthentication()
    {
        await AssertStatus(HttpStatusCode.Unauthorized, GatewayErrorCategory.Authentication);
    }

    [Fact]
    public async Task Http403_IsAuthorization()
    {
        await AssertStatus(HttpStatusCode.Forbidden, GatewayErrorCategory.Authorization);
    }

    [Fact]
    public async Task Http429_IsRateLimited_AndRetried()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.OK,
                "{\"status\":\"SUCCESS\",\"claimReference\":{\"correlationId\":\"c1\",\"patientControlNumber\":\"CLM-P-1001\"}}")
            .EnqueueStatus(HttpStatusCode.TooManyRequests)
            .EnqueueJson(HttpStatusCode.OK, PaidJson);
        var (gateway, _, transmissions, _, _) = NewGateway(handler);
        await gateway.SubmitClaimAsync(GatewayClaimFixtures.Professional());
        var tx = (await transmissions.FindByTenantAndClaimIdAsync("tenant-alpha", "CLM-P-1001")).Single();

        var response = await gateway.CheckClaimStatusAsync(new ClaimStatusRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = tx.TransmissionId
        });

        response.IsSuccess.Should().BeTrue();
        response.Metadata.RetryCount.Should().Be(1);
        handler.CallCount.Should().Be(3);
    }

    [Fact]
    public async Task Http5xx_IsServiceUnavailable_AndRetried()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.OK,
                "{\"status\":\"SUCCESS\",\"claimReference\":{\"correlationId\":\"c1\",\"patientControlNumber\":\"CLM-P-1001\"}}")
            .EnqueueStatus(HttpStatusCode.InternalServerError)
            .EnqueueJson(HttpStatusCode.OK, PaidJson);
        var (gateway, _, transmissions, _, _) = NewGateway(handler);
        await gateway.SubmitClaimAsync(GatewayClaimFixtures.Professional());
        var tx = (await transmissions.FindByTenantAndClaimIdAsync("tenant-alpha", "CLM-P-1001")).Single();

        var response = await gateway.CheckClaimStatusAsync(new ClaimStatusRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = tx.TransmissionId
        });

        response.IsSuccess.Should().BeTrue();
        response.Metadata.RetryCount.Should().Be(1);
    }

    [Fact]
    public async Task Timeout_IsTimeout()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.OK,
                "{\"status\":\"SUCCESS\",\"claimReference\":{\"correlationId\":\"c1\",\"patientControlNumber\":\"CLM-P-1001\"}}")
            .EnqueueThrow(new TaskCanceledException());
        var (gateway, _, transmissions, _, _) = NewGateway(handler);
        await gateway.SubmitClaimAsync(GatewayClaimFixtures.Professional());
        var tx = (await transmissions.FindByTenantAndClaimIdAsync("tenant-alpha", "CLM-P-1001")).Single();

        var response = await gateway.CheckClaimStatusAsync(new ClaimStatusRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = tx.TransmissionId
        });

        response.IsSuccess.Should().BeFalse();
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.Timeout);
        response.Metadata.Status.Should().Be(GatewayTransactionStatus.TimedOut);
    }

    [Fact]
    public async Task NetworkError_IsConnectivity()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.OK,
                "{\"status\":\"SUCCESS\",\"claimReference\":{\"correlationId\":\"c1\",\"patientControlNumber\":\"CLM-P-1001\"}}")
            .EnqueueThrow(new HttpRequestException("boom"));
        var (gateway, _, transmissions, _, _) = NewGateway(handler);
        await gateway.SubmitClaimAsync(GatewayClaimFixtures.Professional());
        var tx = (await transmissions.FindByTenantAndClaimIdAsync("tenant-alpha", "CLM-P-1001")).Single();

        var response = await gateway.CheckClaimStatusAsync(new ClaimStatusRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = tx.TransmissionId
        });

        response.IsSuccess.Should().BeFalse();
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.Connectivity);
    }

    [Fact]
    public async Task MalformedResponse_IsMalformedResponse()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.OK,
                "{\"status\":\"SUCCESS\",\"claimReference\":{\"correlationId\":\"c1\",\"patientControlNumber\":\"CLM-P-1001\"}}")
            .EnqueueJson(HttpStatusCode.OK, "{not-json");
        var (gateway, _, transmissions, _, _) = NewGateway(handler);
        await gateway.SubmitClaimAsync(GatewayClaimFixtures.Professional());
        var tx = (await transmissions.FindByTenantAndClaimIdAsync("tenant-alpha", "CLM-P-1001")).Single();

        var response = await gateway.CheckClaimStatusAsync(new ClaimStatusRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = tx.TransmissionId
        });

        response.IsSuccess.Should().BeFalse();
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.MalformedResponse);
    }

    [Fact]
    public async Task Http200EmptyClaims_IsNoRecordFoundBusinessOutcome()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.OK,
                "{\"status\":\"SUCCESS\",\"claimReference\":{\"correlationId\":\"c1\",\"patientControlNumber\":\"CLM-P-1001\"}}")
            .EnqueueJson(HttpStatusCode.OK, "{\"claims\":[]}");
        var (gateway, _, transmissions, _, _) = NewGateway(handler);
        await gateway.SubmitClaimAsync(GatewayClaimFixtures.Professional());
        var tx = (await transmissions.FindByTenantAndClaimIdAsync("tenant-alpha", "CLM-P-1001")).Single();

        var response = await gateway.CheckClaimStatusAsync(new ClaimStatusRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = tx.TransmissionId
        });

        response.IsSuccess.Should().BeTrue();
        response.Result!.Status.Should().Be(GatewayClaimStatus.NoRecordFound);
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.None);
    }

    [Fact]
    public async Task Http200InvalidSubscriber_IsPayerRejectedBusinessOutcome()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.OK,
                "{\"status\":\"SUCCESS\",\"claimReference\":{\"correlationId\":\"c1\",\"patientControlNumber\":\"CLM-P-1001\"}}")
            .EnqueueJson(HttpStatusCode.OK,
                """{"claims":[],"errors":[{"code":"72","description":"Invalid/Missing Subscriber ID"}]}""");
        var (gateway, _, transmissions, _, _) = NewGateway(handler);
        await gateway.SubmitClaimAsync(GatewayClaimFixtures.Professional());
        var tx = (await transmissions.FindByTenantAndClaimIdAsync("tenant-alpha", "CLM-P-1001")).Single();

        var response = await gateway.CheckClaimStatusAsync(new ClaimStatusRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = tx.TransmissionId
        });

        response.IsSuccess.Should().BeTrue();
        response.Metadata.Status.Should().Be(GatewayTransactionStatus.Rejected);
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.PayerRejected);
        response.Result!.Messages.Should().Contain(m => m.Description!.Contains("Subscriber"));
    }

    [Fact]
    public async Task Http200UnableToRespond_IsClaimStatusUnavailable()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.OK,
                "{\"status\":\"SUCCESS\",\"claimReference\":{\"correlationId\":\"c1\",\"patientControlNumber\":\"CLM-P-1001\"}}")
            .EnqueueJson(HttpStatusCode.OK,
                """{"claims":[],"errors":[{"code":"E0","description":"Payer unable to respond at this time"}]}""");
        var (gateway, _, transmissions, _, _) = NewGateway(handler);
        await gateway.SubmitClaimAsync(GatewayClaimFixtures.Professional());
        var tx = (await transmissions.FindByTenantAndClaimIdAsync("tenant-alpha", "CLM-P-1001")).Single();

        var response = await gateway.CheckClaimStatusAsync(new ClaimStatusRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = tx.TransmissionId
        });

        response.IsSuccess.Should().BeTrue();
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.ClaimStatusUnavailable);
    }

    [Fact]
    public async Task ClaimStatusUnsupportedPayer_FailsNotSupported()
    {
        var handler = new StubHttpMessageHandler();
        var (gateway, _, transmissions, _, _) = NewGateway(handler);
        var tx = new ClaimTransmissionRecord
        {
            TenantId = "tenant-alpha",
            ClaimId = "CLM-UNSUP",
            GatewayName = "Stedi",
            PayerId = SyntheticPayerSeed.UnsupportedId,
            PatientControlNumber = "CLM-UNSUP",
            ServiceDateFrom = new DateOnly(2026, 1, 15),
            InquirySource = ClaimStatusInquirySource.FromSubmission(
                GatewayClaimFixtures.Professional(payerId: SyntheticPayerSeed.UnsupportedId, claimId: "CLM-UNSUP")),
            Status = GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway
        };
        await transmissions.SaveAsync(tx);

        var response = await gateway.CheckClaimStatusAsync(new ClaimStatusRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = tx.TransmissionId
        });

        response.IsSuccess.Should().BeFalse();
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.NotSupported);
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task MissingStediIdentifier_FailsExternalIdentifierMissing()
    {
        var handler = new StubHttpMessageHandler();
        var (gateway, _, transmissions, _, _) = NewGateway(handler);
        var source = GatewayClaimFixtures.Professional(claimId: "CLM-NOEXT");
        source.PayerId = SyntheticPayerSeed.MissingExternalId;
        var tx = new ClaimTransmissionRecord
        {
            TenantId = "tenant-alpha",
            ClaimId = "CLM-NOEXT",
            GatewayName = "Stedi",
            PayerId = SyntheticPayerSeed.MissingExternalId,
            PatientControlNumber = "CLM-NOEXT",
            ServiceDateFrom = new DateOnly(2026, 1, 15),
            InquirySource = ClaimStatusInquirySource.FromSubmission(source),
            Status = GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway
        };
        await transmissions.SaveAsync(tx);

        var response = await gateway.CheckClaimStatusAsync(new ClaimStatusRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = tx.TransmissionId
        });

        response.IsSuccess.Should().BeFalse();
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.ExternalIdentifierMissing);
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task TenantEnrollmentComplete_SendsInquiry()
    {
        var store = PayerTestHarness.CreateStore();
        var payers = PayerTestHarness.CreateService(store);
        await payers.SaveTenantOverrideAsync(new PayerTenantOverride
        {
            TenantId = "tenant-alpha",
            PayerId = SyntheticPayerSeed.EnrollmentId,
            EnrolledTransactions = { HealthcareTransactionType.ClaimStatus276277 }
        });

        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, PaidJson);
        var opts = Options.Create(ValidOptions());
        var factory = new StubHttpClientFactory(handler);
        var transmissions = new InMemoryClaimTransmissionStore();
        var source = GatewayClaimFixtures.Professional(claimId: "CLM-ENROLL-OK");
        source.PayerId = SyntheticPayerSeed.EnrollmentId;
        var tx = new ClaimTransmissionRecord
        {
            TenantId = "tenant-alpha",
            ClaimId = "CLM-ENROLL-OK",
            GatewayName = "Stedi",
            PayerId = SyntheticPayerSeed.EnrollmentId,
            PatientControlNumber = "CLM-ENROLL-OK",
            ServiceDateFrom = new DateOnly(2026, 1, 15),
            InquirySource = ClaimStatusInquirySource.FromSubmission(source),
            Status = GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway
        };
        await transmissions.SaveAsync(tx);

        var gateway = new StediHealthcareGateway(
            new StediEligibilityApiClient(factory, opts, NullLogger<StediEligibilityApiClient>.Instance,
                delay: (_, _) => Task.CompletedTask),
            PayerTestHarness.CreateResolver(opts, payers),
            opts,
            NullLogger<StediHealthcareGateway>.Instance,
            transmissions: transmissions,
            statusInquiries: new InMemoryClaimStatusInquiryStore(),
            statusClient: new StediClaimStatusApiClient(
                factory, opts, NullLogger<StediClaimStatusApiClient>.Instance, delay: (_, _) => Task.CompletedTask));

        var response = await gateway.CheckClaimStatusAsync(new ClaimStatusRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = tx.TransmissionId
        });

        response.IsSuccess.Should().BeTrue();
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task EnrollmentRequired_FailsEnrollmentRequired()
    {
        var handler = new StubHttpMessageHandler();
        var (gateway, _, transmissions, _, _) = NewGateway(handler);
        var source = GatewayClaimFixtures.Professional(claimId: "CLM-ENROLL");
        source.PayerId = SyntheticPayerSeed.EnrollmentId;
        var tx = new ClaimTransmissionRecord
        {
            TenantId = "tenant-alpha",
            ClaimId = "CLM-ENROLL",
            GatewayName = "Stedi",
            PayerId = SyntheticPayerSeed.EnrollmentId,
            PatientControlNumber = "CLM-ENROLL",
            ServiceDateFrom = new DateOnly(2026, 1, 15),
            InquirySource = ClaimStatusInquirySource.FromSubmission(source),
            Status = GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway
        };
        await transmissions.SaveAsync(tx);

        var response = await gateway.CheckClaimStatusAsync(new ClaimStatusRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = tx.TransmissionId
        });

        response.IsSuccess.Should().BeFalse();
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.EnrollmentRequired);
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task SupportedPayer_SendsTradingPartnerServiceId()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.OK,
                "{\"status\":\"SUCCESS\",\"claimReference\":{\"correlationId\":\"c1\",\"patientControlNumber\":\"CLM-P-1001\"}}")
            .EnqueueJson(HttpStatusCode.OK, PaidJson);
        var (gateway, _, transmissions, _, _) = NewGateway(handler);
        await gateway.SubmitClaimAsync(GatewayClaimFixtures.Professional());
        var tx = (await transmissions.FindByTenantAndClaimIdAsync("tenant-alpha", "CLM-P-1001")).Single();

        await gateway.CheckClaimStatusAsync(new ClaimStatusRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = tx.TransmissionId
        });

        handler.RequestBodies.Last().Should().Contain("\"tradingPartnerServiceId\":\"60054\"");
    }

    private async Task AssertStatus(HttpStatusCode status, GatewayErrorCategory expected)
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.OK,
                "{\"status\":\"SUCCESS\",\"claimReference\":{\"correlationId\":\"c1\",\"patientControlNumber\":\"CLM-P-1001\"}}")
            .EnqueueStatus(status);
        var (gateway, _, transmissions, _, _) = NewGateway(handler);
        await gateway.SubmitClaimAsync(GatewayClaimFixtures.Professional());
        var tx = (await transmissions.FindByTenantAndClaimIdAsync("tenant-alpha", "CLM-P-1001")).Single();

        var response = await gateway.CheckClaimStatusAsync(new ClaimStatusRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = tx.TransmissionId
        });

        response.IsSuccess.Should().BeFalse();
        response.Metadata.ErrorCategory.Should().Be(expected);
    }
}
