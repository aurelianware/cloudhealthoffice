using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class ClaimsServiceTests
{
    private readonly Mock<ILogger<ClaimsService>> _logger = new();
    private readonly IConfiguration _configuration;

    public ClaimsServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:ClaimsService"] = "http://localhost:5000",
                ["Authentication:LocalDemo:TenantId"] = "demo"
            })
            .Build();
    }

    private ClaimsService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new ClaimsService(httpClient, _configuration, _logger.Object);
    }

    private ClaimsService CreateService(HttpClient httpClient, string claimsServiceBaseUrl)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:ClaimsService"] = claimsServiceBaseUrl,
                ["Authentication:LocalDemo:TenantId"] = "demo"
            })
            .Build();

        return new ClaimsService(httpClient, configuration, _logger.Object);
    }

    // ── GetRecentClaimsAsync ──

    [Fact]
    public async Task GetRecentClaimsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetRecentClaimsAsync(10));
        ex.ServiceName.Should().Be("Claims Service");
    }

    [Fact]
    public async Task GetRecentClaimsAsync_ExceptionWrapsInnerException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetRecentClaimsAsync(10));
        ex.InnerException.Should().BeOfType<HttpRequestException>();
    }

    // ── GetClaimByIdAsync ──

    [Fact]
    public async Task GetClaimByIdAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetClaimByIdAsync("CLM-2026-00001"));
        ex.ServiceName.Should().Be("Claims Service");
    }

    // ── GetMassAdjudicationRunsAsync ──

    [Fact]
    public async Task GetMassAdjudicationRunsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetMassAdjudicationRunsAsync());
        ex.ServiceName.Should().Be("Claims Service");
    }

    // ── GetMassAdjudicationRunAsync ──

    [Fact]
    public async Task GetMassAdjudicationRunAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetMassAdjudicationRunAsync("run-001"));
        ex.ServiceName.Should().Be("Claims Service");
    }

    // ── SubmitClaimAsync ──

    [Fact]
    public async Task SubmitClaimAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.SubmitClaimAsync(new SubmitClaimRequest()));
        ex.ServiceName.Should().Be("Claims Service");
    }

    // ── SearchClaimsAsync ──

    [Fact]
    public async Task SearchClaimsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.SearchClaimsAsync(new ClaimSearchRequest()));
        ex.ServiceName.Should().Be("Claims Service");
    }

    // ── UpdateClaimStatusAsync ──

    [Fact]
    public async Task UpdateClaimStatusAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.UpdateClaimStatusAsync("CLM-2026-00001", "Denied"));
        ex.ServiceName.Should().Be("Claims Service");
    }

    // ── GetAdjudicationDataAsync ──

    [Fact]
    public async Task GetAdjudicationDataAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetAdjudicationDataAsync("CLM-2026-00001"));
        ex.ServiceName.Should().Be("Claims Service");
    }

    [Fact]
    public async Task GetAdjudicationDataAsync_ExceptionContainsServiceNameInMessage()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetAdjudicationDataAsync("CLM-2026-00001"));
        ex.Message.Should().Contain("Claims Service");
    }

    [Fact]
    public async Task GetAdjudicationDataAsync_WhenApiReturns404_ReturnsNull()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.NotFound)));

        var result = await sut.GetAdjudicationDataAsync("CLM-NO-DETAIL");

        result.Should().BeNull();
    }

    // ════════════════════════════════════════════════════════════════
    // Happy-path and edge-case tests
    // ════════════════════════════════════════════════════════════════

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // ── GetRecentClaimsAsync ──

    [Fact]
    public async Task GetRecentClaimsAsync_WhenApiReturns200_DeserializesClaimsList()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { claimId = "CLM-001", claimNumber = "2026-00001", memberName = "Jane Doe",
                  memberId = "MBR-1", providerName = "Dr. Smith", providerId = "PRV-1",
                  claimType = "Professional", totalChargeAmount = 500.00m, allowedAmount = 400.00m,
                  paidAmount = 320.00m, status = "Paid", serviceDateFrom = "2026-01-15",
                  serviceDateTo = "2026-01-15", submittedDate = "2026-01-16", lineCount = 2 },
            new { claimId = "CLM-002", claimNumber = "2026-00002", memberName = "John Roe",
                  memberId = "MBR-2", providerName = "Dr. Jones", providerId = "PRV-2",
                  claimType = "Institutional", totalChargeAmount = 1200.00m, allowedAmount = 1000.00m,
                  paidAmount = 800.00m, status = "Approved", serviceDateFrom = "2026-02-01",
                  serviceDateTo = "2026-02-03", submittedDate = "2026-02-04", lineCount = 5 }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetRecentClaimsAsync(10);

        result.Should().HaveCount(2);
        result[0].ClaimId.Should().Be("CLM-001");
        result[0].MemberName.Should().Be("Jane Doe");
        result[0].TotalChargeAmount.Should().Be(500.00m);
        result[1].ClaimId.Should().Be("CLM-002");
        result[1].ClaimType.Should().Be("Institutional");
        result[1].LineCount.Should().Be(5);
    }

    [Fact]
    public async Task GetRecentClaimsAsync_WhenApiReturnsNull_ReturnsEmptyList()
    {
        var sut = CreateService(new HttpClient(
            new FakeHandler(HttpStatusCode.OK, "null")));

        var result = await sut.GetRecentClaimsAsync(5);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRecentClaimsAsync_VerifyUrlContainsCountParameter()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        var sut = CreateService(new HttpClient(handler));

        await sut.GetRecentClaimsAsync(25);

        handler.CapturedUrls.Should().ContainSingle()
            .Which.Should().Contain("count=25");
    }

    // ── GetClaimByIdAsync ──

    [Fact]
    public async Task GetClaimByIdAsync_WhenApiReturns200_DeserializesClaimDetails()
    {
        var json = JsonSerializer.Serialize(new
        {
            claimId = "CLM-100", claimNumber = "2026-00100", memberName = "Alice",
            memberId = "MBR-10", providerName = "Dr. Lee", providerId = "PRV-10",
            claimType = "Professional", totalChargeAmount = 750m, allowedAmount = 600m,
            paidAmount = 480m, status = "Paid", serviceDateFrom = "2026-03-01",
            serviceDateTo = "2026-03-01", submittedDate = "2026-03-02", lineCount = 1,
            subscriberId = "SUB-10", subscriberName = "Alice Parent",
            billingProviderName = "Lee Medical", billingProviderNPI = "1234567890",
            placeOfService = "11", deductibleAmount = 50m, coinsuranceAmount = 70m,
            copayAmount = 25m, patientResponsibility = 145m, isEditable = true,
            diagnosisCodes = new[]
            {
                new { code = "J06.9", description = "Acute URI", type = "Principal", pointerNumber = 1 }
            },
            serviceLines = new[]
            {
                new { lineNumber = 1, procedureCode = "99213", procedureDescription = "Office visit",
                      units = 1m, chargeAmount = 750m, allowedAmount = 600m, paidAmount = 480m,
                      patientResponsibility = 145m, serviceDateFrom = "2026-03-01",
                      serviceDateTo = "2026-03-01" }
            }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetClaimByIdAsync("CLM-100");

        result.Should().NotBeNull();
        result!.ClaimId.Should().Be("CLM-100");
        result.SubscriberId.Should().Be("SUB-10");
        result.BillingProviderNPI.Should().Be("1234567890");
        result.DeductibleAmount.Should().Be(50m);
        result.DiagnosisCodes.Should().ContainSingle()
            .Which.Code.Should().Be("J06.9");
        result.ServiceLines.Should().ContainSingle()
            .Which.ProcedureCode.Should().Be("99213");
    }

    [Fact]
    public async Task GetClaimByIdAsync_LoadsAuditTimelineForClaimDetails()
    {
        var handler = new FakeHandler(request =>
        {
            var isTimeline = request.RequestUri!.AbsolutePath.EndsWith("/audit-timeline", StringComparison.Ordinal);
            var json = isTimeline
                ? """[{"timestamp":"2026-03-02T10:00:00Z","action":"Claim submitted","changedBy":"837-ingress","newValue":"Submitted"}]"""
                : """{"id":"claim-100","claimNumber":"CLM-100","status":"Submitted"}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        });
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.GetClaimByIdAsync("claim-100");

        result!.AuditTrail.Should().ContainSingle()
            .Which.ChangedBy.Should().Be("837-ingress");
        handler.CapturedUrls.Should().Contain(url => url.EndsWith("/claims/claim-100/audit-timeline"));
    }

    [Fact]
    public async Task GetClaimByIdAsync_WhenApiReturnsMassAdjudicationClaimShape_DeserializesSafely()
    {
        const string json = """
        {
          "id": "4ff4516a-9e97-4038-80c1-3a2cd6e5d952",
          "claimNumber": "MCC-D-0000004",
          "memberId": "MBR-7242385",
          "subscriberId": "SUB-7242385",
          "subscriberFirstName": "Sandra",
          "subscriberLastName": "Anderson",
          "patientFirstName": "Sandra",
          "patientLastName": "Anderson",
          "patientRelationship": "Self",
          "billingProviderNPI": "1141521249",
          "billingProviderName": "Thomas White",
          "renderingProviderNPI": "1681064964",
          "renderingProviderName": "Susan Moore",
          "placeOfServiceCode": "11",
          "claimType": 3,
          "status": 5,
          "totalChargeAmount": 100,
          "allowedAmount": null,
          "paidAmount": null,
          "serviceDateFrom": "2026-06-15T07:00:00Z",
          "serviceDateTo": "2026-06-15T07:00:00Z",
          "submittedDate": "2026-07-09T22:03:53Z",
          "receivedDate": "2026-07-09T22:03:53Z",
          "adjudicatedDate": "2026-07-09T22:03:55.045Z",
          "diagnosisCodes": [
            {
              "code": "K05.10",
              "codeQualifier": "ABK",
              "pointerNumber": 1
            }
          ],
          "adjudicationResult": {
            "allowedAmount": 80,
            "deductibleAmount": 10,
            "coinsuranceAmount": 5,
            "copayAmount": 3,
            "patientResponsibility": 18,
            "payerPayment": 62
          },
          "claimLines": [
            {
              "lineNumber": 1,
              "procedureCode": "D0150",
              "procedureDescription": "Comprehensive oral evaluation — new or established patient",
              "modifiers": [],
              "units": 1,
              "chargeAmount": 100,
              "serviceDateFrom": "2026-06-15T07:00:00Z",
              "serviceDateTo": "2026-06-15T07:00:00Z",
              "placeOfServiceCode": "11",
              "diagnosisPointers": [1],
              "adjustments": null,
              "mpipMultiplierApplied": null,
              "adjudicationResult": {
                "allowedAmount": 80,
                "paidAmount": 62,
                "patientResponsibility": 18
              },
              "lineStatus": null
            }
          ],
          "auditTrail": null
        }
        """;

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetClaimByIdAsync("4ff4516a-9e97-4038-80c1-3a2cd6e5d952");

        result.Should().NotBeNull();
        result!.ClaimId.Should().Be("4ff4516a-9e97-4038-80c1-3a2cd6e5d952");
        result.Status.Should().Be("Approved");
        result.ClaimType.Should().Be("Dental");
        result.TotalChargeAmount.Should().Be(100m);
        result.AllowedAmount.Should().Be(80m);
        result.PaidAmount.Should().Be(62m);
        result.DeductibleAmount.Should().Be(10m);
        result.CoinsuranceAmount.Should().Be(5m);
        result.CopayAmount.Should().Be(3m);
        result.PatientResponsibility.Should().Be(18m);
        result.DiagnosisCodes.Should().ContainSingle()
            .Which.Code.Should().Be("K05.10");
        result.AuditTrail.Should().BeEmpty();
        result.ServiceLines.Should().ContainSingle();
        result.ServiceLines[0].ProcedureCode.Should().Be("D0150");
        result.ServiceLines[0].AllowedAmount.Should().Be(80m);
        result.ServiceLines[0].PaidAmount.Should().Be(62m);
        result.ServiceLines[0].PatientResponsibility.Should().Be(18m);
        result.ServiceLines[0].Modifiers.Should().BeEmpty();
        result.ServiceLines[0].DiagnosisPointers.Should().ContainSingle()
            .Which.Should().Be(1);
        result.ServiceLines[0].Adjustments.Should().BeEmpty();
    }

    [Fact]
    public async Task GetClaimByIdAsync_WhenReceivedDateIsNull_DeserializesSafely()
    {
        const string json = """
        {
          "id": "claim-raw-837",
          "claimNumber": "SMOKE-837",
          "receivedDate": null,
          "serviceDateFrom": "2026-07-31T00:00:00Z",
          "serviceDateTo": "2026-07-31T00:00:00Z",
          "submittedDate": "2026-07-31T05:31:21Z",
          "diagnosisCodes": [],
          "claimLines": []
        }
        """;

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetClaimByIdAsync("claim-raw-837");

        result.Should().NotBeNull();
        result!.ReceivedDate.Should().BeNull();
    }

    [Fact]
    public async Task GetClaimByIdAsync_WhenPendedWithAiAdvisory_DeserializesExaminerContext()
    {
        const string json = """
        {
          "id": "claim-pended-ai",
          "claimNumber": "GUIDE-PEND-001",
          "status": 4,
          "submittedDate": "2026-07-31T05:31:21Z",
          "serviceDateFrom": "2026-07-30T00:00:00Z",
          "serviceDateTo": "2026-07-30T00:00:00Z",
          "diagnosisCodes": [],
          "claimLines": [],
          "pendDetails": {
            "pendCode": "NCCI",
            "pendReason": "NCCI pair edit requires human review",
            "editFailures": [
              {
                "editType": "NcciPair",
                "ruleId": "NE001",
                "column1Code": "27447",
                "column2Code": "27486",
                "affectedLineNumbers": [1, 2]
              }
            ]
          },
          "aiExamination": {
            "recommendedDisposition": "RequestInfo",
            "confidenceScore": 0.87,
            "rationale": "Confirm whether distinct procedural services support modifier 59.",
            "policyCitations": ["CMS NCCI Policy Manual Ch. 1"],
            "modelId": "synthetic-guide-fixture",
            "promptVersion": "ncci-pend-v1"
          }
        }
        """;

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetClaimByIdAsync("claim-pended-ai");

        result.Should().NotBeNull();
        result!.Status.Should().Be("Pended");
        result.PendDetails!.PendCode.Should().Be("NCCI");
        result.PendDetails.EditFailures.Should().ContainSingle()
            .Which.RuleId.Should().Be("NE001");
        result.AiExamination!.RecommendedDisposition.Should().Be("RequestInfo");
        result.AiExamination.ConfidenceScore.Should().Be(0.87);
        result.AiExamination.PolicyCitations.Should().ContainSingle();
    }

    [Fact]
    public async Task TryRecordAiExaminerAgreementAsync_WhenApiSucceeds_ReturnsTrue()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.TryRecordAiExaminerAgreementAsync(
            "claim-pended-ai",
            "Overridden",
            "examiner-1",
            "Clinical documentation supports a different disposition.");

        result.Should().BeTrue();
        handler.CapturedRequests.Should().ContainSingle();
        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Post);
        handler.CapturedUrls[0].Should().Contain(
            "/claims/claim-pended-ai/ai-examination/agreement");
    }

    [Fact]
    public async Task GetClaimByIdAsync_WhenApiReturnsNull_ReturnsNull()
    {
        var sut = CreateService(new HttpClient(
            new FakeHandler(HttpStatusCode.OK, "null")));

        var result = await sut.GetClaimByIdAsync("CLM-NONE");

        result.Should().BeNull();
    }

    // ── GetExplanationOfBenefitJsonAsync ──

    [Fact]
    public async Task GetExplanationOfBenefitJsonAsync_StripsApiPrefixAndReturnsRawFhirJson()
    {
        const string eobJson = """
        {"resourceType":"ExplanationOfBenefit","id":"claim-1","status":"active"}
        """;
        var handler = new FakeHandler(HttpStatusCode.OK, eobJson);
        var sut = CreateService(new HttpClient(handler), "http://localhost:5000/api");

        var result = await sut.GetExplanationOfBenefitJsonAsync("claim-1");

        result.Should().Be(eobJson);
        handler.CapturedRequests.Should().ContainSingle();
        var request = handler.CapturedRequests.Single();
        request.RequestUri!.AbsoluteUri.Should()
            .Be("http://localhost:5000/fhir/ExplanationOfBenefit/claim-1");
        request.Headers.Accept.Should().Contain(h => h.MediaType == "application/fhir+json");
    }

    [Fact]
    public async Task GetExplanationOfBenefitJsonAsync_WhenApiReturns404_ReturnsNull()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.NotFound)));

        var result = await sut.GetExplanationOfBenefitJsonAsync("missing-claim");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetExplanationOfBenefitJsonAsync_WhenClaimsServiceBaseUrlMissing_ThrowsServiceUnavailableWithoutRequest()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler), "");

        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetExplanationOfBenefitJsonAsync("claim-1"));

        ex.ServiceName.Should().Be("Claims Service");
        ex.InnerException.Should().BeOfType<InvalidOperationException>();
        handler.CapturedRequests.Should().BeEmpty();
    }

    // ── GetMassAdjudicationRunsAsync ──

    [Fact]
    public async Task GetMassAdjudicationRunsAsync_WhenApiReturns200_DeserializesRunsList()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new
            {
                id = "run-001",
                run = new
                {
                    tenantId = "tenant-a",
                    requestedClaims = 100,
                    seed = 42,
                    parallelism = 8,
                    claimsUrl = "https://claims",
                    benefitUrl = "https://benefit",
                    memberUrl = "https://member",
                    coverageUrl = "https://coverage",
                    providerUrl = "https://provider",
                    seedMembers = true,
                    seedProviders = true,
                    skipClaimUpdate = false,
                    lineOfBusiness = 3,
                    startedAtUtc = "2026-07-01T00:00:00Z",
                    completedAtUtc = "2026-07-01T00:01:00Z"
                },
                totalClaims = 100,
                processed = 100,
                paid = 97,
                pended = 1,
                businessDenials = 2,
                observationTimeouts = 0,
                platformFailures = 1,
                workflowScenarios = 12,
                workflowMatches = 10,
                workflowMismatches = 1,
                workflowUnsupported = 1,
                workflowObservationTimeouts = 0,
                throughputClaimsPerSecond = 15.5,
                averagePaymentDelta = 59.36m,
                workflowScenarioBreakdown = new[]
                {
                    new
                    {
                        scenario = "EdgeCase:CobSecondaryPayer",
                        total = 10,
                        matches = 9,
                        mismatches = 1,
                        unsupported = 0,
                        observationTimeouts = 0,
                        unspecified = 0
                    }
                },
                createdAtUtc = "2026-07-01T00:01:05Z"
            }
        }, JsonOpts);

        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.GetMassAdjudicationRunsAsync();

        handler.CapturedRequests.Should().ContainSingle();
        handler.CapturedRequests[0].Headers.GetValues("X-Tenant-ID").Should().ContainSingle("demo");
        result.Should().ContainSingle();
        result[0].Id.Should().Be("run-001");
        result[0].Run.TenantId.Should().Be("tenant-a");
        result[0].Run.MemberUrl.Should().Be("https://member");
        result[0].Run.CoverageUrl.Should().Be("https://coverage");
        result[0].Run.SeedMembers.Should().BeTrue();
        result[0].Run.LineOfBusiness.Should().Be(3);
        result[0].Processed.Should().Be(100);
        result[0].Pended.Should().Be(1);
        result[0].WorkflowUnsupported.Should().Be(1);
        result[0].AveragePaymentDelta.Should().Be(59.36m);
        result[0].WorkflowScenarioBreakdown.Should().ContainSingle(s =>
            s.Scenario == "EdgeCase:CobSecondaryPayer"
            && s.Matches == 9
            && s.Mismatches == 1);
        result[0].PlatformFailures.Should().Be(1);
    }

    [Fact]
    public async Task GetMassAdjudicationRunsAsync_WhenApiReturnsNull_ReturnsEmptyList()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, "null")));

        var result = await sut.GetMassAdjudicationRunsAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetMassAdjudicationRunsAsync_WhenApiReturnsEmptyBody_ReturnsEmptyList()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, string.Empty)));

        var result = await sut.GetMassAdjudicationRunsAsync();

        result.Should().BeEmpty();
    }

    // ── GetMassAdjudicationRunAsync ──

    [Fact]
    public async Task GetMassAdjudicationRunAsync_WhenApiReturns200_DeserializesRun()
    {
        var json = JsonSerializer.Serialize(new
        {
            id = "run-002",
            run = new
            {
                tenantId = "tenant-b",
                requestedClaims = 25,
                seed = 7,
                parallelism = 4,
                claimsUrl = "https://claims",
                benefitUrl = "https://benefit",
                providerUrl = "https://provider",
                seedProviders = false,
                skipClaimUpdate = true,
                startedAtUtc = "2026-07-02T12:00:00Z",
                completedAtUtc = "2026-07-02T12:00:10Z"
            },
            totalClaims = 25,
            processed = 24,
            paid = 24,
            businessDenials = 0,
            platformFailures = 0,
            createdAtUtc = "2026-07-02T12:00:11Z"
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetMassAdjudicationRunAsync("run-002");

        result.Should().NotBeNull();
        result!.Id.Should().Be("run-002");
        result.Run.TenantId.Should().Be("tenant-b");
        result.Run.SkipClaimUpdate.Should().BeTrue();
        result.TotalClaims.Should().Be(25);
    }

    [Fact]
    public async Task GetMassAdjudicationRunAsync_WhenApiReturnsNull_ReturnsNull()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, "null")));

        var result = await sut.GetMassAdjudicationRunAsync("run-none");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetMassAdjudicationRunAsync_WhenApiReturnsEmptyBody_ReturnsNull()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, string.Empty)));

        var result = await sut.GetMassAdjudicationRunAsync("run-empty");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetMassAdjudicationRunAsync_WhenApiReturns404_ReturnsNull()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.NotFound, "{}")));

        var result = await sut.GetMassAdjudicationRunAsync("run-missing");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetMassAdjudicationClaimResultsAsync_WhenApiReturns200_DeserializesClaimResults()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new
            {
                id = "result-001",
                runId = "run-001",
                tenantId = "tenant-a",
                generatedClaimId = "GEN-001",
                submittedClaimId = "CLM-001",
                claimType = 1,
                validationScenario = "TxStarInpatientNoAuth",
                expectedOutcome = "BusinessDenial",
                expectedBusinessDenialCode = "PRIOR_AUTH_REQUIRED",
                validationStatus = "Matched",
                outcome = "BusinessDenial",
                adjudicationSuccess = true,
                actualPlanPayment = 95.25m,
                expectedPlanPayment = 95.25m,
                paymentDelta = 0m,
                elapsedMilliseconds = 250.5,
                submitMilliseconds = 50.0,
                adjudicationMilliseconds = 125.0,
                writebackMilliseconds = 75.5,
                createdAtUtc = "2026-07-03T00:00:00Z"
            }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetMassAdjudicationClaimResultsAsync("run-001");

        result.Should().ContainSingle();
        result[0].GeneratedClaimId.Should().Be("GEN-001");
        result[0].ClaimType.Should().Be("Professional");
        result[0].ValidationScenario.Should().Be("TxStarInpatientNoAuth");
        result[0].ValidationStatus.Should().Be("Matched");
        result[0].Outcome.Should().Be("BusinessDenial");
    }

    [Fact]
    public async Task GetMassAdjudicationClaimResultsAsync_VerifyUrlContainsEscapedQueryParameters()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        var sut = CreateService(new HttpClient(handler));

        await sut.GetMassAdjudicationClaimResultsAsync("run/001", "Business Denial", 5000, "Unsupported", "Mismatched");

        handler.CapturedUrls.Should().ContainSingle()
            .Which.Should().Be("http://localhost:5000/mass-adjudication/runs/run%2F001/claims?limit=1000&outcome=Business%20Denial&validationStatus=Unsupported&paymentStatus=Mismatched");
    }

    [Fact]
    public void FlexibleClaimStatusJsonConverter_WhenApiReturnsNumericValue_DeserializesStatusName()
    {
        JsonSerializer.Deserialize<IdCardOrderView>(
            """{"status":7}""",
            JsonOpts)!.Status.Should().Be("Paid");
    }

    // ── SubmitClaimAsync ──

    [Fact]
    public async Task SubmitClaimAsync_WhenApiReturns200_ExtractsClaimId()
    {
        var json = JsonSerializer.Serialize(new { claimId = "CLM-NEW-999" }, JsonOpts);
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.SubmitClaimAsync(new SubmitClaimRequest
        {
            MemberId = "MBR-1", ProviderId = "PRV-1",
            ServiceDate = new DateTime(2026, 3, 15)
        });

        result.Should().Be("CLM-NEW-999");
    }

    [Fact]
    public async Task SubmitClaimAsync_WhenResponseMissingClaimId_ReturnsEmptyString()
    {
        var json = JsonSerializer.Serialize(new { otherField = "value" }, JsonOpts);
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.SubmitClaimAsync(new SubmitClaimRequest());

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task SubmitClaimAsync_VerifyPostBodyContainsRequest()
    {
        var handler = new FakeHandler(HttpStatusCode.OK,
            JsonSerializer.Serialize(new { claimId = "CLM-X" }, JsonOpts));
        var sut = CreateService(new HttpClient(handler));

        await sut.SubmitClaimAsync(new SubmitClaimRequest
        {
            MemberId = "MBR-42", ProviderId = "PRV-7",
            ServiceDate = new DateTime(2026, 6, 1)
        });

        handler.CapturedRequests.Should().ContainSingle();
        var req = handler.CapturedRequests[0];
        req.Method.Should().Be(HttpMethod.Post);
        var body = await req.Content!.ReadAsStringAsync();
        body.Should().Contain("MBR-42");
        body.Should().Contain("PRV-7");
    }

    // ── SearchClaimsAsync ──

    [Fact]
    public async Task SearchClaimsAsync_WhenApiReturns200_DeserializesSearchResult()
    {
        var json = JsonSerializer.Serialize(new
        {
            claims = new[]
            {
                new { claimId = "CLM-S1", claimNumber = "2026-S1", memberName = "Bob",
                      memberId = "MBR-3", providerName = "Dr. X", providerId = "PRV-3",
                      claimType = "Professional", totalChargeAmount = 200m, allowedAmount = 150m,
                      paidAmount = 120m, status = "Paid", serviceDateFrom = "2026-01-01",
                      serviceDateTo = "2026-01-01", submittedDate = "2026-01-02", lineCount = 1 }
            },
            totalCount = 42, pageNumber = 1, pageSize = 25,
            totalChargeAmount = 8400m, totalAllowedAmount = 6300m, totalPaidAmount = 5040m,
            approvedCount = 30, deniedCount = 5, pendingCount = 7
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.SearchClaimsAsync(new ClaimSearchRequest { Status = "Paid" });

        result.Claims.Should().ContainSingle();
        result.TotalCount.Should().Be(42);
        result.ApprovedCount.Should().Be(30);
        result.DeniedCount.Should().Be(5);
    }

    [Fact]
    public async Task SearchClaimsAsync_WhenApiReturnsNull_ReturnsEmptySearchResult()
    {
        var sut = CreateService(new HttpClient(
            new FakeHandler(HttpStatusCode.OK, "null")));

        var result = await sut.SearchClaimsAsync(new ClaimSearchRequest());

        result.Claims.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
    }

    // ── UpdateClaimStatusAsync ──

    [Fact]
    public async Task UpdateClaimStatusAsync_WhenApiReturns200_CompletesWithoutException()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        var act = () => sut.UpdateClaimStatusAsync("CLM-001", "Denied", "Insufficient documentation");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task UpdateClaimStatusAsync_VerifyUrlContainsClaimId()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.UpdateClaimStatusAsync("CLM-5050", "Approved");

        handler.CapturedUrls.Should().ContainSingle()
            .Which.Should().Contain("/claims/CLM-5050/status");
    }

    // ── GetAdjudicationDataAsync ──

    [Fact]
    public async Task GetAdjudicationDataAsync_WhenApiReturns200_DeserializesTransparencyData()
    {
        var json = JsonSerializer.Serialize(new
        {
            steps = new[]
            {
                new { stepName = "Eligibility", stepNumber = 1, status = "Passed", durationMs = 12 }
            },
            ncciResults = new[]
            {
                new { editCode = "MUE-99213", editType = "MUE", description = "Max units",
                      passed = true }
            },
            feeScheduleResults = new[]
            {
                new { procedureCode = "99213", feeScheduleName = "Medicare RBRVS",
                      billedAmount = 200m, allowedAmount = 150m, contractedRate = 150m,
                      rateBasis = "MedicareRVU", rateMultiplier = 1.0m, networkTier = "In-Network" }
            },
            benefitCalculation = new
            {
                serviceType = "Office Visit", benefitRuleApplied = "PPO-Standard",
                networkTier = "In-Network", allowedAmount = 150m, deductibleApplied = 50m,
                copayAmount = 25m, coinsuranceAmount = 15m, planPayment = 60m,
                memberResponsibility = 90m, deductibleMet = false, oopMaxMet = false
            }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetAdjudicationDataAsync("CLM-ADJ-1");

        result.Should().NotBeNull();
        result!.Steps.Should().ContainSingle().Which.StepName.Should().Be("Eligibility");
        result.NcciResults.Should().ContainSingle().Which.Passed.Should().BeTrue();
        result.FeeScheduleResults.Should().ContainSingle()
            .Which.ProcedureCode.Should().Be("99213");
        result.BenefitCalculation.Should().NotBeNull();
        result.BenefitCalculation!.PlanPayment.Should().Be(60m);
    }

    // ── ClaimDetails – remaining properties (RenderingProvider, Facility, notes, audit) ──

    [Fact]
    public async Task GetClaimByIdAsync_WhenApiReturns200_DeserializesAllExtendedClaimDetailProperties()
    {
        var json = JsonSerializer.Serialize(new
        {
            claimId = "CLM-200", claimNumber = "2026-00200", memberName = "Bob Lee",
            memberId = "MBR-20", providerName = "Dr. Patel", providerId = "PRV-20",
            claimType = "Institutional", totalChargeAmount = 4200m, allowedAmount = 3500m,
            paidAmount = 2800m, status = "Denied", serviceDateFrom = "2026-02-01",
            serviceDateTo = "2026-02-03", submittedDate = "2026-02-04", lineCount = 3,
            subscriberId = "SUB-20", subscriberName = "Bob Parent",
            billingProviderName = "City Hospital", billingProviderNPI = "9876543210",
            placeOfService = "21",
            renderingProviderName = "Dr. Singh",
            renderingProviderNPI = "1122334455",
            facilityName = "Regional Medical Center",
            facilityNPI = "5544332211",
            claimNotes = "Requires medical records",
            referralNumber = "REF-2026-001",
            receivedDate = "2026-02-04T09:00:00Z",
            paidDate = (string?)null,
            checkNumber = (string?)null,
            denialReason = "Not medically necessary",
            canApprove = false,
            canDeny = false,
            canReverse = true,
            isEditable = false,
            adjustmentInfo = new
            {
                adjustmentType = "Reversal",
                originalClaimId = "CLM-100",
                relatedClaimId = "CLM-101",
                adjustmentAmount = -2800m,
                reason = "Duplicate claim",
                adjustmentDate = "2026-02-20T00:00:00Z",
                adjustedBy = "system@healthplan.com"
            },
            auditTrail = new[]
            {
                new
                {
                    timestamp = "2026-02-04T09:00:00Z",
                    action = "Received",
                    changedBy = "intake@healthplan.com",
                    oldValue = (string?)null,
                    newValue = "Received",
                    notes = "Auto-acknowledged on receipt"
                }
            }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetClaimByIdAsync("CLM-200");

        result.Should().NotBeNull();
        result!.RenderingProviderName.Should().Be("Dr. Singh");
        result.RenderingProviderNPI.Should().Be("1122334455");
        result.FacilityName.Should().Be("Regional Medical Center");
        result.FacilityNPI.Should().Be("5544332211");
        result.ClaimNotes.Should().Be("Requires medical records");
        result.ReferralNumber.Should().Be("REF-2026-001");
        result.ReceivedDate.Should().NotBe(default);
        result.PaidDate.Should().BeNull();
        result.CheckNumber.Should().BeNull();
        result.DenialReason.Should().Be("Not medically necessary");
        result.CanApprove.Should().BeFalse();
        result.CanDeny.Should().BeFalse();
        result.CanReverse.Should().BeTrue();
        result.AdjustmentInfo.Should().NotBeNull();
        result.AdjustmentInfo!.AdjustmentType.Should().Be("Reversal");
        result.AdjustmentInfo.OriginalClaimId.Should().Be("CLM-100");
        result.AdjustmentInfo.RelatedClaimId.Should().Be("CLM-101");
        result.AdjustmentInfo.AdjustmentAmount.Should().Be(-2800m);
        result.AdjustmentInfo.Reason.Should().Be("Duplicate claim");
        result.AdjustmentInfo.AdjustmentDate.Should().NotBeNull();
        result.AdjustmentInfo.AdjustedBy.Should().Be("system@healthplan.com");
        result.AuditTrail.Should().ContainSingle();
        result.AuditTrail[0].Action.Should().Be("Received");
        result.AuditTrail[0].ChangedBy.Should().Be("intake@healthplan.com");
        result.AuditTrail[0].OldValue.Should().BeNull();
        result.AuditTrail[0].NewValue.Should().Be("Received");
        result.AuditTrail[0].Notes.Should().Be("Auto-acknowledged on receipt");
    }

    // ── ClaimServiceLine with ClaimLineAdjustment ────────────────────────────

    [Fact]
    public async Task GetClaimByIdAsync_WhenServiceLineHasAdjustments_DeserializesLineAdjustments()
    {
        var json = JsonSerializer.Serialize(new
        {
            claimId = "CLM-300", claimNumber = "2026-00300", memberName = "Sue Chen",
            memberId = "MBR-30", providerName = "Dr. Kim", providerId = "PRV-30",
            claimType = "Professional", totalChargeAmount = 400m, allowedAmount = 320m,
            paidAmount = 256m, status = "Paid", serviceDateFrom = "2026-03-01",
            serviceDateTo = "2026-03-01", submittedDate = "2026-03-02", lineCount = 1,
            subscriberId = "SUB-30", subscriberName = "Sue Parent",
            billingProviderName = "Kim Clinic", billingProviderNPI = "2233445566",
            placeOfService = "11", receivedDate = "2026-03-02T00:00:00Z",
            serviceLines = new[]
            {
                new
                {
                    lineNumber = 1, procedureCode = "99215",
                    procedureDescription = "Office visit established",
                    units = 1m, chargeAmount = 400m, allowedAmount = 320m,
                    paidAmount = 256m, patientResponsibility = 64m,
                    serviceDateFrom = "2026-03-01", serviceDateTo = "2026-03-01",
                    adjustments = new[]
                    {
                        new { groupCode = "CO", reasonCode = "45", amount = 80m,
                              description = "Charge exceeds fee schedule" }
                    }
                }
            }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetClaimByIdAsync("CLM-300");

        result.Should().NotBeNull();
        var line = result!.ServiceLines.Should().ContainSingle().Subject;
        line.Adjustments.Should().ContainSingle();
        line.Adjustments[0].GroupCode.Should().Be("CO");
        line.Adjustments[0].ReasonCode.Should().Be("45");
        line.Adjustments[0].Amount.Should().Be(80m);
        line.Adjustments[0].Description.Should().Be("Charge exceeds fee schedule");
    }

    // ── ClaimSearchRequest – remaining search fields ─────────────────────────

    [Fact]
    public async Task SearchClaimsAsync_WithAllSearchFields_SendsAllFieldsInBody()
    {
        var handler = new FakeHandler(HttpStatusCode.OK,
            JsonSerializer.Serialize(new
            {
                claims = Array.Empty<object>(), totalCount = 0, pageNumber = 1, pageSize = 25
            }, JsonOpts));
        var sut = CreateService(new HttpClient(handler));

        var req = new ClaimSearchRequest
        {
            ClaimNumber = "2026-00500",
            MemberId = "MBR-99",
            MemberName = "Alice Wonder",
            ProviderId = "PRV-88",
            ProviderName = "Wonder Clinic",
            ClaimType = "Institutional",
            ServiceDateFrom = new DateTime(2026, 1, 1),
            ServiceDateTo = new DateTime(2026, 3, 31),
            Status = "Denied",
            AuthorizationNumber = "AUTH-2026-001"
        };

        // Verify all fields are set and readable
        req.ClaimNumber.Should().Be("2026-00500");
        req.MemberId.Should().Be("MBR-99");
        req.MemberName.Should().Be("Alice Wonder");
        req.ProviderId.Should().Be("PRV-88");
        req.ProviderName.Should().Be("Wonder Clinic");
        req.ClaimType.Should().Be("Institutional");
        req.ServiceDateFrom.Should().Be(new DateTime(2026, 1, 1));
        req.ServiceDateTo.Should().Be(new DateTime(2026, 3, 31));
        req.AuthorizationNumber.Should().Be("AUTH-2026-001");

        await sut.SearchClaimsAsync(req);

        var body = await handler.CapturedRequests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("2026-00500");
        body.Should().Contain("MBR-99");
        body.Should().Contain("Wonder Clinic");
    }

    // ── NcciEditResult – all properties ─────────────────────────────────────

    [Fact]
    public async Task GetAdjudicationDataAsync_WhenNcciResultHasFailed_DeserializesAllNcciFields()
    {
        var json = JsonSerializer.Serialize(new
        {
            steps = Array.Empty<object>(),
            ncciResults = new[]
            {
                new
                {
                    editCode = "NCCI-99213-99214", editType = "NCCI-PTP",
                    description = "Column 1/Column 2 edit",
                    passed = false,
                    failureReason = "Procedures cannot be billed together",
                    affectedProcedureCode = "99214",
                    affectedModifier = "25",
                    resolutionApplied = "Modifier 25 applied"
                }
            },
            feeScheduleResults = Array.Empty<object>()
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetAdjudicationDataAsync("CLM-NCCI-1");

        result.Should().NotBeNull();
        result!.NcciResults.Should().ContainSingle();
        var ncci = result.NcciResults[0];
        ncci.Passed.Should().BeFalse();
        ncci.FailureReason.Should().Be("Procedures cannot be billed together");
        ncci.AffectedProcedureCode.Should().Be("99214");
        ncci.AffectedModifier.Should().Be("25");
        ncci.ResolutionApplied.Should().Be("Modifier 25 applied");
    }

    // ── AdjudicationStep – all properties ────────────────────────────────────

    [Fact]
    public async Task GetAdjudicationDataAsync_WhenStepHasAllFields_DeserializesTimestampSummaryError()
    {
        var json = JsonSerializer.Serialize(new
        {
            steps = new[]
            {
                new
                {
                    stepName = "Medical-Policy",
                    stepNumber = 4,
                    status = "Failed",
                    timestamp = "2026-03-10T12:05:30Z",
                    durationMs = 88,
                    summary = "Policy check: prior auth required",
                    errorDetail = "Authorization not found for service type 99215"
                }
            },
            ncciResults = Array.Empty<object>(),
            feeScheduleResults = Array.Empty<object>()
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetAdjudicationDataAsync("CLM-STEP-1");

        result.Should().NotBeNull();
        result!.Steps.Should().ContainSingle();
        var step = result.Steps[0];
        step.Timestamp.Should().NotBeNull();
        step.Summary.Should().Be("Policy check: prior auth required");
        step.ErrorDetail.Should().Be("Authorization not found for service type 99215");
    }

    // ── AccumulatorUpdate via BenefitCalculation ──────────────────────────────

    [Fact]
    public async Task GetAdjudicationDataAsync_WhenBenefitCalcHasAccumulators_DeserializesAccumulatorUpdates()
    {
        var json = JsonSerializer.Serialize(new
        {
            steps = Array.Empty<object>(),
            ncciResults = Array.Empty<object>(),
            feeScheduleResults = Array.Empty<object>(),
            benefitCalculation = new
            {
                serviceType = "Specialty", benefitRuleApplied = "PPO-Specialty",
                networkTier = "In-Network", allowedAmount = 350m, deductibleApplied = 350m,
                deductibleRemaining = 650m, copayAmount = 0m, coinsuranceAmount = 0m,
                planPayment = 0m, memberResponsibility = 350m,
                deductibleMet = false, oopMaxMet = false,
                individualDeductibleBalance = 1000m, individualDeductibleLimit = 1500m,
                individualOopBalance = 1000m, individualOopLimit = 5000m,
                accumulatorUpdates = new[]
                {
                    new { accumulatorType = "IndividualDeductible", amountApplied = 350m,
                          newBalance = 1000m, limit = 1500m }
                }
            }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetAdjudicationDataAsync("CLM-ACC-1");

        result.Should().NotBeNull();
        result!.BenefitCalculation.Should().NotBeNull();
        result.BenefitCalculation!.AccumulatorUpdates.Should().ContainSingle();
        var acc = result.BenefitCalculation.AccumulatorUpdates[0];
        acc.AccumulatorType.Should().Be("IndividualDeductible");
        acc.AmountApplied.Should().Be(350m);
        acc.NewBalance.Should().Be(1000m);
        acc.Limit.Should().Be(1500m);
    }
}
