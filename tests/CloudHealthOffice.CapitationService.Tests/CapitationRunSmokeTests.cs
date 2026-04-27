using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NSubstitute;
using CapitationService.Models;
using CapitationService.Services;
using Xunit;

namespace CloudHealthOffice.CapitationService.Tests;

/// <summary>
/// Smoke tests for the capitation run lifecycle — exercising the full HTTP pipeline
/// (routing, middleware, controller, serialization) via WebApplicationFactory.
///
/// These verify the end-to-end happy path that a consumer of the API would experience:
///   Create contract → Create run → Execute run → View statements → Approve → Pay
/// </summary>
public class CapitationRunSmokeTests : IClassFixture<CapitationApiFactory>
{
    private readonly CapitationApiFactory _factory;
    private readonly HttpClient _client;

    // Match the server's wire format (string enums via JsonStringEnumConverter
    // registered by AddCloudHealthOfficeJsonOptions). Without this, deserializing
    // responses whose DTOs contain enum fields (CapitationRun.Status,
    // CapitationContract.ProviderType, etc.) fails with a JsonException.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public CapitationRunSmokeTests(CapitationApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", "smoke-test-tenant");
    }

    // ═══════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════

    private static CapitationContract CreateContract(string id = "contract-smoke-1") => new()
    {
        Id = id,
        TenantId = "smoke-test-tenant",
        ContractId = "pc-smoke-1",
        RateConfigNumber = "CAP-1234567890-2026-1",
        ContractNumber = "CAP-1234567890-2026",
        ProviderNPI = "1234567890",
        ProviderName = "Dr. Sarah Chen, MD",
        ProviderType = ProviderType.Individual,
        ContractType = ContractType.PrimaryCareOnly,
        LineOfBusiness = LineOfBusiness.Commercial,
        Status = CapitationRateConfigStatus.Active,
        EffectiveDate = new DateTime(2026, 1, 1),
        WithholdPercentage = 0.10m,
        RateTiers = new List<CapitationRateTier>
        {
            new() { TierName = "Adult Male 18-34", AgeFrom = 18, AgeTo = 34, Gender = "M", BasePMPM = 28.00m }
        }
    };

    private static CapitationRun CreateRun(
        string id = "run-smoke-1",
        CapitationRunStatus status = CapitationRunStatus.Pending) => new()
    {
        Id = id,
        TenantId = "smoke-test-tenant",
        RunNumber = "CAPRUN-2026-03-SMKE",
        RunType = CapitationRunType.Monthly,
        CapitationPeriod = new DateTime(2026, 3, 1),
        Status = status,
        Criteria = new CapitationRunCriteria { LineOfBusiness = LineOfBusiness.Commercial },
        CreatedBy = "smoke-test"
    };

    private static CapitationRun CreateCompletedRun() => new()
    {
        Id = "run-smoke-done",
        TenantId = "smoke-test-tenant",
        RunNumber = "CAPRUN-2026-03-DONE",
        RunType = CapitationRunType.Monthly,
        CapitationPeriod = new DateTime(2026, 3, 1),
        Status = CapitationRunStatus.Completed,
        TotalStatements = 1,
        TotalMemberMonths = 5,
        TotalGrossCapitation = 140.00m,
        TotalWithholds = 14.00m,
        TotalNetPayable = 126.00m,
        TotalProviders = 1,
        ExecutionStartedAt = DateTime.UtcNow.AddSeconds(-2),
        ExecutionCompletedAt = DateTime.UtcNow,
        ExecutionDurationSeconds = 2.1,
        StatementIds = new List<string> { "stmt-smoke-1" }
    };

    private static CapitationStatement CreateStatement(
        string id = "stmt-smoke-1",
        CapitationStatementStatus status = CapitationStatementStatus.Generated) => new()
    {
        Id = id,
        TenantId = "smoke-test-tenant",
        StatementNumber = "CAPSTMT-1234567890-2026-03",
        CapitationRunId = "run-smoke-done",
        ContractId = "contract-smoke-1",
        ContractNumber = "CAP-1234567890-2026",
        ProviderNPI = "1234567890",
        ProviderName = "Dr. Sarah Chen, MD",
        CapitationPeriodStart = new DateTime(2026, 3, 1),
        CapitationPeriodEnd = new DateTime(2026, 3, 31),
        Status = status,
        MemberMonths = 5,
        GrossCapitation = 140.00m,
        WithholdAmount = 14.00m,
        TotalAdjustments = 0,
        NetPayable = 126.00m,
        LineItems = new List<CapitationLineItem>
        {
            new() { MemberId = "MEM001", MemberName = "John Doe", MemberAge = 28, Gender = "M",
                     BasePMPM = 28.00m, RiskScore = 1.0m, AdjustedPMPM = 28.00m, ProrationFactor = 1.0m,
                     GrossAmount = 28.00m, WithholdAmount = 2.80m, NetAmount = 25.20m,
                     AssignmentEffectiveDate = new DateTime(2025, 1, 1) },
            new() { MemberId = "MEM002", MemberName = "Jane Smith", MemberAge = 30, Gender = "F",
                     BasePMPM = 28.00m, RiskScore = 1.0m, AdjustedPMPM = 28.00m, ProrationFactor = 1.0m,
                     GrossAmount = 28.00m, WithholdAmount = 2.80m, NetAmount = 25.20m,
                     AssignmentEffectiveDate = new DateTime(2025, 1, 1) }
        }
    };

    // ═══════════════════════════════════════════════════════════════════
    // SMOKE TEST 1: Full Run Lifecycle
    // Create → Execute → View Statements → Approve → Generate ERA
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FullRunLifecycle_CreateExecuteApprove_Returns200()
    {
        // Arrange: mock service to return expected results at each step
        var pendingRun = CreateRun();
        var completedRun = CreateCompletedRun();
        var statement = CreateStatement();
        var approvedStatement = CreateStatement(status: CapitationStatementStatus.Approved);

        _factory.RunService.CreateRunAsync(Arg.Any<CreateCapitationRunRequest>(), Arg.Any<string?>())
            .Returns(pendingRun);
        _factory.RunService.ExecuteRunAsync("run-smoke-1")
            .Returns(completedRun);
        _factory.StatementRepository.GetByRunIdAsync("run-smoke-done")
            .Returns(new List<CapitationStatement> { statement });
        _factory.RunService.ApproveStatementAsync("stmt-smoke-1")
            .Returns(approvedStatement);

        // Step 1: Create run
        var createResponse = await _client.PostAsJsonAsync("/api/v1/capitation/runs", new
        {
            runType = "Monthly",
            capitationPeriod = "2026-03-01T00:00:00Z",
            criteria = new { lineOfBusiness = "Commercial" },
            createdBy = "smoke-test"
        }, Json);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var createdRun = await createResponse.Content.ReadFromJsonAsync<CapitationRun>(Json);
        Assert.NotNull(createdRun);
        Assert.Equal("CAPRUN-2026-03-SMKE", createdRun!.RunNumber);

        // Step 2: Execute run
        var executeResponse = await _client.PostAsync("/api/v1/capitation/runs/run-smoke-1/execute", null);
        Assert.Equal(HttpStatusCode.OK, executeResponse.StatusCode);
        var executedRun = await executeResponse.Content.ReadFromJsonAsync<CapitationRun>(Json);
        Assert.NotNull(executedRun);
        Assert.Equal(CapitationRunStatus.Completed, executedRun!.Status);
        Assert.Equal(1, executedRun.TotalStatements);
        Assert.Equal(126.00m, executedRun.TotalNetPayable);

        // Step 3: View statements for the run
        var statementsResponse = await _client.GetAsync("/api/v1/capitation/runs/run-smoke-done/statements");
        Assert.Equal(HttpStatusCode.OK, statementsResponse.StatusCode);
        var statements = await statementsResponse.Content.ReadFromJsonAsync<List<CapitationStatement>>(Json);
        Assert.NotNull(statements);
        Assert.Single(statements!);
        Assert.Equal("CAPSTMT-1234567890-2026-03", statements[0].StatementNumber);

        // Step 4: Approve statement
        var approveResponse = await _client.PutAsync("/api/v1/capitation/statements/stmt-smoke-1/approve", null);
        Assert.Equal(HttpStatusCode.OK, approveResponse.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SMOKE TEST 2: Contract CRUD
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ContractCrud_CreateReadActivate_Returns200()
    {
        var contract = CreateContract();
        var draftContract = CreateContract();
        draftContract.Status = CapitationRateConfigStatus.Draft;

        _factory.ContractRepository.CreateAsync(Arg.Any<CapitationContract>())
            .Returns(ci => ci.Arg<CapitationContract>());
        // First call (Read) returns Active, second call (Activate check) returns Draft
        _factory.ContractRepository.GetByIdAsync("contract-smoke-1")
            .Returns(contract, draftContract);
        _factory.ContractRepository.UpdateAsync(Arg.Any<CapitationContract>())
            .Returns(ci =>
            {
                var c = ci.Arg<CapitationContract>();
                c.Status = CapitationRateConfigStatus.Active;
                return c;
            });

        // Create
        var createResponse = await _client.PostAsJsonAsync("/api/v1/capitation/contracts", contract, Json);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        // Read
        var getResponse = await _client.GetAsync("/api/v1/capitation/contracts/contract-smoke-1");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var fetched = await getResponse.Content.ReadFromJsonAsync<CapitationContract>(Json);
        Assert.NotNull(fetched);
        Assert.Equal("1234567890", fetched!.ProviderNPI);

        // Activate (needs Draft status)
        var activateResponse = await _client.PutAsync("/api/v1/capitation/contracts/contract-smoke-1/activate", null);
        Assert.Equal(HttpStatusCode.OK, activateResponse.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SMOKE TEST 3: Statement Detail + ERA Generation
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task StatementDetail_GetById_ReturnsLineItems()
    {
        var statement = CreateStatement();
        _factory.StatementRepository.GetByIdAsync("stmt-smoke-1")
            .Returns(statement);

        var response = await _client.GetAsync("/api/v1/capitation/statements/stmt-smoke-1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<CapitationStatement>(Json);
        Assert.NotNull(result);
        Assert.Equal(5, result!.MemberMonths);
        Assert.Equal(140.00m, result.GrossCapitation);
        Assert.Equal(14.00m, result.WithholdAmount);
        Assert.Equal(126.00m, result.NetPayable);
        Assert.Equal(2, result.LineItems.Count);
        Assert.Equal("MEM001", result.LineItems[0].MemberId);
        Assert.Equal(28.00m, result.LineItems[0].BasePMPM);
    }

    [Fact]
    public async Task StatementEra_Generate835_ReturnsTextPlain()
    {
        var statement = CreateStatement();
        var contract = CreateContract();

        _factory.StatementRepository.GetByIdAsync("stmt-smoke-1").Returns(statement);
        _factory.ContractRepository.GetByIdAsync("contract-smoke-1").Returns(contract);
        _factory.EraService.Generate835ForStatement(
                Arg.Any<CapitationStatement>(),
                Arg.Any<CapitationContract>(),
                Arg.Any<CapitationEraTradingPartnerInfo>())
            .Returns("ISA*00*          *00*          *ZZ*SENDER~GS*HP*~ST*835*0001~BPR*C*126.00~SE*10*0001~GE*1*1~IEA*1*000000001~");

        var response = await _client.PostAsync("/api/v1/capitation/statements/stmt-smoke-1/era", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("ISA*", content);
        Assert.Contains("ST*835*", content);
        Assert.Contains("BPR*C*126.00", content);
        Assert.Contains("IEA*", content);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SMOKE TEST 4: Disbursement Initiation
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Disbursement_InitiateSingle_Returns201()
    {
        var disbursement = new CapitationDisbursement
        {
            Id = "disb-smoke-1",
            StatementId = "stmt-smoke-1",
            StatementNumber = "CAPSTMT-1234567890-2026-03",
            ProviderNPI = "1234567890",
            Amount = 126.00m,
            Method = DisbursementMethod.NachaCredit,
            Status = DisbursementStatus.Pending
        };

        _factory.DisbursementService.InitiateDisbursementAsync(Arg.Any<InitiateDisbursementRequest>())
            .Returns(disbursement);

        var response = await _client.PostAsJsonAsync("/api/v1/capitation/disbursements", new
        {
            statementId = "stmt-smoke-1",
            initiatedBy = "smoke-test"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CapitationDisbursement>(Json);
        Assert.NotNull(created);
        Assert.Equal(126.00m, created!.Amount);
        Assert.Equal(DisbursementMethod.NachaCredit, created.Method);
    }

    [Fact]
    public async Task Disbursement_BatchInitiate_Returns200()
    {
        var batchResult = new BatchDisbursementResult
        {
            TotalStatements = 3,
            DisbursementsInitiated = 3,
            Skipped = 0,
            Errors = 0,
            TotalAmount = 15000.00m,
            DisbursementIds = new List<string> { "d1", "d2", "d3" },
            NachaFile = new NachaCreditFileResult
            {
                FileReference = "NACHA-CR-SMOKE",
                EntryCount = 3,
                TotalAmount = 15000.00m
            }
        };

        _factory.DisbursementService.InitiateBatchDisbursementAsync(Arg.Any<InitiateBatchDisbursementRequest>())
            .Returns(batchResult);

        var response = await _client.PostAsJsonAsync("/api/v1/capitation/disbursements/batch", new
        {
            statementIds = new[] { "s1", "s2", "s3" },
            initiatedBy = "smoke-test"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<BatchDisbursementResult>(Json);
        Assert.NotNull(result);
        Assert.Equal(3, result!.DisbursementsInitiated);
        Assert.Equal(15000.00m, result.TotalAmount);
        Assert.NotNull(result.NachaFile);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SMOKE TEST 5: Error Paths
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetContract_NotFound_Returns404()
    {
        _factory.ContractRepository.GetByIdAsync("nonexistent")
            .Returns((CapitationContract?)null);

        var response = await _client.GetAsync("/api/v1/capitation/contracts/nonexistent");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetStatement_NotFound_Returns404()
    {
        _factory.StatementRepository.GetByIdAsync("nonexistent")
            .Returns((CapitationStatement?)null);

        var response = await _client.GetAsync("/api/v1/capitation/statements/nonexistent");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ExecuteRun_InvalidState_Returns400()
    {
        _factory.RunService.ExecuteRunAsync("run-already-done")
            .Returns<CapitationRun>(x => throw new InvalidOperationException("Run is in Completed state, expected Pending"));

        var response = await _client.PostAsync("/api/v1/capitation/runs/run-already-done/execute", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CancelRun_Success_Returns204()
    {
        _factory.RunService.CancelRunAsync("run-smoke-1").Returns(Task.CompletedTask);

        var response = await _client.DeleteAsync("/api/v1/capitation/runs/run-smoke-1");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SMOKE TEST 6: Tenant Isolation
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task HealthEndpoint_NoTenantRequired_Returns200()
    {
        // Health endpoint should work without tenant header
        var client = _factory.CreateClient(); // No X-Tenant-ID header
        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SMOKE TEST 7: Unpaid Statements + Summary
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task UnpaidStatements_ReturnsListOfApproved()
    {
        var statements = new List<CapitationStatement>
        {
            CreateStatement("s1", CapitationStatementStatus.Approved),
            CreateStatement("s2", CapitationStatementStatus.Generated)
        };
        _factory.StatementRepository.GetUnpaidStatementsAsync().Returns(statements);

        var response = await _client.GetAsync("/api/v1/capitation/statements/unpaid");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<List<CapitationStatement>>(Json);
        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
    }

    [Fact]
    public async Task PeriodSummary_ReturnsTotals()
    {
        var summary = new CapitationPeriodSummary
        {
            Period = new DateTime(2026, 3, 1),
            TotalProviders = 3,
            TotalMemberMonths = 20,
            TotalGrossCapitation = 5000m,
            TotalWithholds = 500m,
            TotalNetPayable = 4500m,
            ByLineOfBusiness = new Dictionary<string, decimal> { { "Commercial", 3000m }, { "Medicaid", 1500m } },
            ByContractType = new Dictionary<string, decimal> { { "PrimaryCareOnly", 2000m }, { "GlobalCapitation", 2500m } }
        };
        _factory.RunService.GetCapitationSummaryAsync(Arg.Any<DateTime>()).Returns(summary);

        var response = await _client.GetAsync("/api/v1/capitation/statements/summary?period=2026-03-01");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<CapitationPeriodSummary>(Json);
        Assert.NotNull(result);
        Assert.Equal(3, result!.TotalProviders);
        Assert.Equal(4500m, result.TotalNetPayable);
        Assert.Equal(2, result.ByLineOfBusiness.Count);
    }
}
