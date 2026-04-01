using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class CapitationServiceTests
{
    private readonly Mock<ILogger<CapitationService>> _logger = new();
    private readonly IConfiguration _configuration;

    public CapitationServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:CapitationService"] = "http://localhost:6000"
            })
            .Build();
    }

    private CapitationService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new CapitationService(httpClient, _configuration, _logger.Object);
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // ════════════════════════════════════════════════════════════════
    // Contracts — error paths
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetContractsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetContractsAsync());
        ex.ServiceName.Should().Be("Capitation Service");
    }

    [Fact]
    public async Task GetContractByIdAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetContractByIdAsync("C-1"));
        ex.ServiceName.Should().Be("Capitation Service");
    }

    [Fact]
    public async Task CreateContractAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.CreateContractAsync(new CapitationContractSummary()));
        ex.ServiceName.Should().Be("Capitation Service");
    }

    [Fact]
    public async Task UpdateContractAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.UpdateContractAsync("C-1", new CapitationContractSummary()));
        ex.ServiceName.Should().Be("Capitation Service");
    }

    [Fact]
    public async Task ActivateContractAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.ActivateContractAsync("C-1"));
        ex.ServiceName.Should().Be("Capitation Service");
    }

    [Fact]
    public async Task TerminateContractAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.TerminateContractAsync("C-1", "End of term"));
        ex.ServiceName.Should().Be("Capitation Service");
    }

    // ════════════════════════════════════════════════════════════════
    // Runs — error paths
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetRunsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetRunsAsync());
        ex.ServiceName.Should().Be("Capitation Service");
    }

    [Fact]
    public async Task CreateRunAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.CreateRunAsync(new CreateCapRunRequest()));
        ex.ServiceName.Should().Be("Capitation Service");
    }

    [Fact]
    public async Task ExecuteRunAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.ExecuteRunAsync("R-1"));
        ex.ServiceName.Should().Be("Capitation Service");
    }

    [Fact]
    public async Task CancelRunAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.CancelRunAsync("R-1"));
        ex.ServiceName.Should().Be("Capitation Service");
    }

    // ════════════════════════════════════════════════════════════════
    // Statements — error paths
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetStatementsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetStatementsAsync());
        ex.ServiceName.Should().Be("Capitation Service");
    }

    [Fact]
    public async Task ApproveStatementAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.ApproveStatementAsync("S-1"));
        ex.ServiceName.Should().Be("Capitation Service");
    }

    [Fact]
    public async Task InitiateDisbursementAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.InitiateDisbursementAsync("S-1"));
        ex.ServiceName.Should().Be("Capitation Service");
    }

    // ════════════════════════════════════════════════════════════════
    // Contracts — happy paths
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetContractsAsync_WhenApiReturns200_DeserializesContractList()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { id = "C-1", rateConfigNumber = "RC-001", providerNPI = "1111111111",
                  providerName = "Dr. Smith", contractType = "PrimaryCareOnly",
                  lineOfBusiness = "Commercial", status = "Active",
                  effectiveDate = "2025-01-01" },
            new { id = "C-2", rateConfigNumber = "RC-002", providerNPI = "2222222222",
                  providerName = "Dr. Jones", contractType = "FullRisk",
                  lineOfBusiness = "Medicare", status = "Draft",
                  effectiveDate = "2025-06-01" }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));
        var result = await sut.GetContractsAsync();

        result.Should().HaveCount(2);
        result[0].Id.Should().Be("C-1");
        result[0].ProviderNPI.Should().Be("1111111111");
        result[1].Status.Should().Be("Draft");
    }

    [Fact]
    public async Task GetContractsAsync_WithFilters_BuildsCorrectQueryString()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        var sut = CreateService(new HttpClient(handler));

        await sut.GetContractsAsync(npi: "1234567890", status: "Active", lob: "Commercial");

        var url = handler.CapturedUrls.Single();
        url.Should().Contain("npi=1234567890");
        url.Should().Contain("status=Active");
        url.Should().Contain("lob=Commercial");
    }

    [Fact]
    public async Task GetContractsAsync_WithNoFilters_HasNoQueryString()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        var sut = CreateService(new HttpClient(handler));

        await sut.GetContractsAsync();

        handler.CapturedUrls.Single().Should().EndWith("/v1/capitation/contracts");
    }

    [Fact]
    public async Task GetContractByIdAsync_WhenApiReturns200_DeserializesContract()
    {
        var json = JsonSerializer.Serialize(new
        {
            id = "C-10", providerNPI = "5555555555", providerName = "Dr. House",
            contractType = "PrimaryCareOnly", withholdPercentage = 15.0m,
            riskAdjusted = true, defaultRiskScore = 1.2m, status = "Active",
            effectiveDate = "2025-01-01"
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));
        var result = await sut.GetContractByIdAsync("C-10");

        result.Should().NotBeNull();
        result!.WithholdPercentage.Should().Be(15.0m);
        result.RiskAdjusted.Should().BeTrue();
        result.DefaultRiskScore.Should().Be(1.2m);
    }

    [Fact]
    public async Task GetContractByIdAsync_WhenApiReturnsNull_ReturnsNull()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, "null")));
        var result = await sut.GetContractByIdAsync("C-NONE");
        result.Should().BeNull();
    }

    [Fact]
    public async Task CreateContractAsync_WhenApiReturns200_ExtractsContractId()
    {
        var json = JsonSerializer.Serialize(new { id = "C-NEW-1", status = "Draft" }, JsonOpts);
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.CreateContractAsync(new CapitationContractSummary
        {
            ProviderNPI = "9999999999", ContractType = "PrimaryCareOnly"
        });

        result.Should().Be("C-NEW-1");
    }

    [Fact]
    public async Task CreateContractAsync_VerifyPostBody()
    {
        var handler = new FakeHandler(HttpStatusCode.OK,
            JsonSerializer.Serialize(new { id = "C-X" }, JsonOpts));
        var sut = CreateService(new HttpClient(handler));

        await sut.CreateContractAsync(new CapitationContractSummary
        {
            ProviderNPI = "8888888888", LineOfBusiness = "Medicare"
        });

        handler.CapturedRequests.Should().ContainSingle();
        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Post);
        var body = await handler.CapturedRequests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("8888888888");
        body.Should().Contain("Medicare");
    }

    [Fact]
    public async Task UpdateContractAsync_SendsPutWithCorrectUrl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.UpdateContractAsync("C-10", new CapitationContractSummary { Status = "Active" });

        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Put);
        handler.CapturedUrls[0].Should().Contain("/v1/capitation/contracts/C-10");
    }

    [Fact]
    public async Task ActivateContractAsync_SendsPutToCorrectUrl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.ActivateContractAsync("C-10");

        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Put);
        handler.CapturedUrls[0].Should().Contain("/v1/capitation/contracts/C-10/activate");
    }

    [Fact]
    public async Task TerminateContractAsync_SendsPutWithReasonInBody()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.TerminateContractAsync("C-10", "Provider left network", new DateTime(2026, 12, 31));

        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Put);
        handler.CapturedUrls[0].Should().Contain("/v1/capitation/contracts/C-10/terminate");
        var body = await handler.CapturedRequests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("Provider left network");
    }

    // ════════════════════════════════════════════════════════════════
    // Runs — happy paths
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetRunsAsync_WhenApiReturns200_DeserializesRunList()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { id = "R-1", runNumber = "RUN-2026-01", runType = "Monthly",
                  status = "Completed", totalStatements = 15, totalMemberMonths = 4500,
                  totalGrossCapitation = 225000m, totalNetPayable = 191250m }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));
        var result = await sut.GetRunsAsync();

        result.Should().ContainSingle();
        result[0].TotalGrossCapitation.Should().Be(225000m);
        result[0].TotalNetPayable.Should().Be(191250m);
    }

    [Fact]
    public async Task GetRunsAsync_WithDateFilters_BuildsCorrectQueryString()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        var sut = CreateService(new HttpClient(handler));

        await sut.GetRunsAsync(from: new DateTime(2026, 1, 1), lineOfBusiness: "Commercial");

        var url = handler.CapturedUrls.Single();
        url.Should().Contain("from=");
        url.Should().Contain("lineOfBusiness=Commercial");
    }

    [Fact]
    public async Task GetRunByIdAsync_WhenApiReturns200_DeserializesRun()
    {
        var json = JsonSerializer.Serialize(new
        {
            id = "R-5", runNumber = "RUN-2026-05", status = "Completed",
            totalStatements = 20, totalMemberMonths = 6000,
            totalGrossCapitation = 300000m, totalWithholds = 45000m, totalNetPayable = 255000m
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));
        var result = await sut.GetRunByIdAsync("R-5");

        result.Should().NotBeNull();
        result!.TotalWithholds.Should().Be(45000m);
    }

    [Fact]
    public async Task CreateRunAsync_WhenApiReturns200_ExtractsRunId()
    {
        var json = JsonSerializer.Serialize(new { id = "R-NEW-1", status = "Pending" }, JsonOpts);
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.CreateRunAsync(new CreateCapRunRequest
        {
            RunType = "Monthly", CapitationPeriod = new DateTime(2026, 3, 1),
            CreatedBy = "admin@acme.com"
        });

        result.Should().Be("R-NEW-1");
    }

    [Fact]
    public async Task CreateRunAsync_VerifyPostBodyContainsRequest()
    {
        var handler = new FakeHandler(HttpStatusCode.OK,
            JsonSerializer.Serialize(new { id = "R-X" }, JsonOpts));
        var sut = CreateService(new HttpClient(handler));

        await sut.CreateRunAsync(new CreateCapRunRequest
        {
            RunType = "Monthly", CreatedBy = "finance@acme.com",
            Description = "March 2026 capitation"
        });

        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Post);
        handler.CapturedUrls[0].Should().Contain("/v1/capitation/runs");
        var body = await handler.CapturedRequests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("finance@acme.com");
        body.Should().Contain("March 2026 capitation");
    }

    [Fact]
    public async Task ExecuteRunAsync_WhenApiReturns200_DeserializesExecutedRun()
    {
        var json = JsonSerializer.Serialize(new
        {
            id = "R-5", status = "Completed", totalStatements = 20,
            totalGrossCapitation = 300000m, totalNetPayable = 255000m
        }, JsonOpts);

        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));
        var result = await sut.ExecuteRunAsync("R-5");

        result.Status.Should().Be("Completed");
        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Post);
        handler.CapturedUrls[0].Should().Contain("/v1/capitation/runs/R-5/execute");
    }

    [Fact]
    public async Task CancelRunAsync_SendsDeleteToCorrectUrl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.CancelRunAsync("R-5");

        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Delete);
        handler.CapturedUrls[0].Should().Contain("/v1/capitation/runs/R-5");
    }

    // ════════════════════════════════════════════════════════════════
    // Statements — happy paths
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetStatementsAsync_WhenApiReturns200_DeserializesStatementList()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { id = "S-1", statementNumber = "STMT-001", providerNPI = "1111111111",
                  providerName = "Dr. Smith", status = "Generated",
                  memberMonths = 300, grossCapitation = 15000m,
                  withholdAmount = 2250m, netPayable = 12750m }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));
        var result = await sut.GetStatementsAsync();

        result.Should().ContainSingle();
        result[0].GrossCapitation.Should().Be(15000m);
        result[0].NetPayable.Should().Be(12750m);
    }

    [Fact]
    public async Task GetStatementsAsync_WithFilters_BuildsCorrectQueryString()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        var sut = CreateService(new HttpClient(handler));

        await sut.GetStatementsAsync(npi: "1111111111", status: "Approved");

        var url = handler.CapturedUrls.Single();
        url.Should().Contain("npi=1111111111");
        url.Should().Contain("status=Approved");
    }

    [Fact]
    public async Task GetStatementByIdAsync_WhenApiReturnsNull_ReturnsNull()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, "null")));
        var result = await sut.GetStatementByIdAsync("S-NONE");
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetStatementsByRunAsync_UrlContainsRunId()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        var sut = CreateService(new HttpClient(handler));

        await sut.GetStatementsByRunAsync("R-5");

        handler.CapturedUrls.Single().Should().Contain("/v1/capitation/runs/R-5/statements");
    }

    [Fact]
    public async Task GetUnpaidStatementsAsync_CallsCorrectEndpoint()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        var sut = CreateService(new HttpClient(handler));

        await sut.GetUnpaidStatementsAsync();

        handler.CapturedUrls.Single().Should().Contain("/v1/capitation/statements/unpaid");
    }

    [Fact]
    public async Task ApproveStatementAsync_SendsPutToCorrectUrl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.ApproveStatementAsync("S-1");

        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Put);
        handler.CapturedUrls[0].Should().Contain("/v1/capitation/statements/S-1/approve");
    }

    [Fact]
    public async Task VoidStatementAsync_SendsPutWithReasonInBody()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.VoidStatementAsync("S-1", "Duplicate payment");

        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Put);
        handler.CapturedUrls[0].Should().Contain("/v1/capitation/statements/S-1/void");
        var body = await handler.CapturedRequests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("Duplicate payment");
    }

    [Fact]
    public async Task HoldStatementAsync_SendsPutWithReasonInBody()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.HoldStatementAsync("S-1", "Pending review");

        handler.CapturedUrls[0].Should().Contain("/v1/capitation/statements/S-1/hold");
        var body = await handler.CapturedRequests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("Pending review");
    }

    [Fact]
    public async Task GetPeriodSummaryAsync_DeserializesSummary()
    {
        var json = JsonSerializer.Serialize(new
        {
            period = "2026-03-01", totalProviders = 25, totalMemberMonths = 7500,
            totalGrossCapitation = 375000m, totalWithholds = 56250m, totalNetPayable = 318750m,
            byLineOfBusiness = new Dictionary<string, decimal>
            {
                ["Commercial"] = 225000m, ["Medicare"] = 150000m
            }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));
        var result = await sut.GetPeriodSummaryAsync(new DateTime(2026, 3, 1));

        result.TotalProviders.Should().Be(25);
        result.TotalGrossCapitation.Should().Be(375000m);
        result.TotalNetPayable.Should().Be(318750m);
    }

    // ════════════════════════════════════════════════════════════════
    // Disbursements — happy paths
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task InitiateDisbursementAsync_WhenApiReturns200_ReturnsOk()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.InitiateDisbursementAsync("S-1", "finance@acme.com");

        result.Should().Be("ok");
        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Post);
        handler.CapturedUrls[0].Should().Contain("/v1/capitation/disbursements");
        var body = await handler.CapturedRequests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("S-1");
        body.Should().Contain("finance@acme.com");
    }

    [Fact]
    public async Task InitiateBatchDisbursementAsync_WhenApiReturns200_DeserializesResult()
    {
        var json = JsonSerializer.Serialize(new
        {
            totalStatements = 5, disbursementsInitiated = 4,
            skipped = 1, errors = 0, totalAmount = 50000m,
            errorMessages = new string[0]
        }, JsonOpts);

        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.InitiateBatchDisbursementAsync(
            new List<string> { "S-1", "S-2", "S-3", "S-4", "S-5" }, "finance@acme.com");

        result.TotalStatements.Should().Be(5);
        result.DisbursementsInitiated.Should().Be(4);
        result.Skipped.Should().Be(1);
        result.TotalAmount.Should().Be(50000m);
        handler.CapturedUrls[0].Should().Contain("/v1/capitation/disbursements/batch");
    }

    // ── CapRunSummary – remaining properties ──────────────────────────────────

    [Fact]
    public async Task GetRunsAsync_WhenApiReturns200_DeserializesAllCapRunSummaryProperties()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new
            {
                id = "RUN-100", runNumber = "CAP-2026-01", runType = "Monthly",
                capitationPeriod = "2026-01-01T00:00:00Z",
                status = "Completed",
                lineOfBusiness = "Commercial",
                description = "January commercial capitation run",
                criteria = new
                {
                    lineOfBusiness = "Commercial",
                    providerNPI = "1234567890",
                    contractType = "Capitation",
                    originalPeriod = "2025-12-01T00:00:00Z"
                },
                totalStatements = 45,
                totalMemberMonths = 13500,
                totalGrossCapitation = 675000m,
                totalWithholds = 101250m,
                totalNetPayable = 573750m,
                totalProviders = 45,
                createdAt = "2026-01-10T08:00:00Z",
                createdBy = "admin@healthplan.com",
                executionStartedAt = "2026-01-11T06:00:00Z",
                executionCompletedAt = "2026-01-11T06:15:30Z",
                executionDurationSeconds = 930.0
            }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));
        var result = await sut.GetRunsAsync();

        result.Should().ContainSingle();
        var run = result[0];
        run.Id.Should().Be("RUN-100");
        run.RunType.Should().Be("Monthly");
        run.CapitationPeriod.Should().NotBe(default);
        run.LineOfBusiness.Should().Be("Commercial");
        run.Description.Should().Be("January commercial capitation run");
        run.Criteria.Should().NotBeNull();
        run.Criteria!.LineOfBusiness.Should().Be("Commercial");
        run.Criteria.ProviderNPI.Should().Be("1234567890");
        run.Criteria.ContractType.Should().Be("Capitation");
        run.Criteria.OriginalPeriod.Should().NotBeNull();
        run.TotalProviders.Should().Be(45);
        run.CreatedAt.Should().NotBe(default);
        run.CreatedBy.Should().Be("admin@healthplan.com");
        run.ExecutionStartedAt.Should().NotBeNull();
        run.ExecutionCompletedAt.Should().NotBeNull();
        run.ExecutionDurationSeconds.Should().Be(930.0);
    }

    // ── CapStatementSummary with CapLineItem and CapAdjustment ────────────────

    [Fact]
    public async Task GetStatementByIdAsync_WhenApiReturns200_DeserializesLineItemsAndAdjustments()
    {
        var json = JsonSerializer.Serialize(new
        {
            id = "S-200", statementNumber = "STMT-200",
            capitationRunId = "RUN-100",
            contractId = "CTR-1", contractNumber = "CTR-2026-001",
            providerNPI = "1234567890", providerName = "Dr. Brown",
            capitationPeriodStart = "2026-01-01T00:00:00Z",
            capitationPeriodEnd = "2026-01-31T00:00:00Z",
            status = "Approved",
            memberMonths = 300, grossCapitation = 90000m,
            withholdAmount = 13500m, totalAdjustments = -5000m, netPayable = 71500m,
            paymentDate = "2026-02-15T00:00:00Z",
            lineItems = new[]
            {
                new
                {
                    memberId = "MBR-001", memberName = "Jane Doe", planId = "PLN-001",
                    memberAge = 35, gender = "F",
                    basePMPM = 280m, riskScore = 1.05m, adjustedPMPM = 294m,
                    prorationFactor = 1.0m, grossAmount = 294m, withholdAmount = 44.1m,
                    netAmount = 249.9m, isRetroactive = false, adjustmentReason = (string?)null
                }
            },
            adjustments = new[]
            {
                new
                {
                    type = "Retro", description = "Rate correction for Q4 2025",
                    amount = -5000m, relatedMemberId = "MBR-002",
                    adjustmentDate = "2026-01-15T00:00:00Z"
                }
            }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));
        var result = await sut.GetStatementByIdAsync("S-200");

        result.Should().NotBeNull();
        result!.PaymentDate.Should().NotBeNull();
        result.LineItems.Should().ContainSingle();
        var li = result.LineItems[0];
        li.MemberId.Should().Be("MBR-001");
        li.MemberName.Should().Be("Jane Doe");
        li.PlanId.Should().Be("PLN-001");
        li.MemberAge.Should().Be(35);
        li.Gender.Should().Be("F");
        li.BasePMPM.Should().Be(280m);
        li.RiskScore.Should().Be(1.05m);
        li.AdjustedPMPM.Should().Be(294m);
        li.ProrationFactor.Should().Be(1.0m);
        li.GrossAmount.Should().Be(294m);
        li.WithholdAmount.Should().Be(44.1m);
        li.NetAmount.Should().Be(249.9m);
        li.IsRetroactive.Should().BeFalse();
        li.AdjustmentReason.Should().BeNull();

        result.Adjustments.Should().ContainSingle();
        var adj = result.Adjustments[0];
        adj.Type.Should().Be("Retro");
        adj.Description.Should().Be("Rate correction for Q4 2025");
        adj.Amount.Should().Be(-5000m);
        adj.RelatedMemberId.Should().Be("MBR-002");
        adj.AdjustmentDate.Should().NotBe(default);
    }

    // ── CapRateTier via GetContractByIdAsync ──────────────────────────────────

    [Fact]
    public async Task GetContractByIdAsync_WhenApiReturns200_DeserializesCapRateTiers()
    {
        var json = JsonSerializer.Serialize(new
        {
            id = "CTR-200", contractNumber = "CAP-CTR-200",
            providerNPI = "9876543210", providerName = "Community Health", status = "Active",
            lineOfBusiness = "Commercial", paymentMethodology = "FullCapitation",
            effectiveDate = "2025-01-01T00:00:00Z",
            rateTiers = new[]
            {
                new
                {
                    tierName = "Adult Female", ageFrom = 18, ageTo = 64,
                    gender = "F", ageSexCategory = "Adult 18-64 Female",
                    basePMPM = 295.50m, serviceCategory = "Medical"
                },
                new
                {
                    tierName = "Senior Male", ageFrom = 65, ageTo = 99,
                    gender = "M", ageSexCategory = "Senior 65+ Male",
                    basePMPM = 520.00m, serviceCategory = (string?)null
                }
            }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));
        var result = await sut.GetContractByIdAsync("CTR-200");

        result.Should().NotBeNull();
        result!.RateTiers.Should().HaveCount(2);
        var tier1 = result.RateTiers[0];
        tier1.TierName.Should().Be("Adult Female");
        tier1.AgeFrom.Should().Be(18);
        tier1.AgeTo.Should().Be(64);
        tier1.Gender.Should().Be("F");
        tier1.AgeSexCategory.Should().Be("Adult 18-64 Female");
        tier1.BasePMPM.Should().Be(295.50m);
        tier1.ServiceCategory.Should().Be("Medical");
        result.RateTiers[1].ServiceCategory.Should().BeNull();
    }
}
