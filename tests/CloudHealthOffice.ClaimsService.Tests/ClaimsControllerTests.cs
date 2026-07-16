using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests;

public class ClaimsControllerTests : IClassFixture<ClaimsApiFactory>
{
    private readonly ClaimsApiFactory _factory;
    private readonly HttpClient _client;
    private readonly IClaimRepository _repo;
    private readonly IMassAdjudicationRunRepository _massRunRepo;
    private readonly IClaimAcknowledgmentService _ackService;
    private readonly IClaimSubmissionService _submissionService;

    public ClaimsControllerTests(ClaimsApiFactory factory)
    {
        _factory = factory;
        _repo = factory.ClaimRepository;
        _massRunRepo = factory.MassAdjudicationRunRepository;
        _ackService = factory.AcknowledgmentService;
        _submissionService = factory.SubmissionService;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");
    }

    // ═══════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════

    private static Claim CreateValidClaim(List<ClaimLine>? lines = null)
    {
        var serviceDate = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        return new Claim
        {
            TenantId = "test-tenant",
            ClaimNumber = "CLM-20260115-001",
            MemberId = "MEM-001",
            BillingProviderNPI = "1234567890",
            LineOfBusiness = LineOfBusiness.Commercial,
            ClaimType = ClaimType.Professional,
            ServiceDateFrom = serviceDate,
            ServiceDateTo = serviceDate,
            DiagnosisCodes = new List<DiagnosisCode>
            {
                new() { Code = "E11.9", CodeQualifier = "ABK", PointerNumber = 1, Description = "Type 2 diabetes" }
            },
            ClaimLines = lines ?? new List<ClaimLine>
            {
                new()
                {
                    LineNumber = 1,
                    ProcedureCode = "99213",
                    ChargeAmount = 150.00m,
                    Units = 1,
                    ServiceDateFrom = serviceDate,
                    ServiceDateTo = serviceDate,
                    DiagnosisPointers = new List<int> { 1 }
                }
            }
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    // SUBMIT CLAIM (legacy POST /api/claims)
    //
    // Capability 5.3 routed legacy submission through IClaimSubmissionService;
    // the controller is a thin adapter that maps Claim ↔ AdapterClaim around
    // the canonical service. Detailed validation / event-emission coverage
    // lives on ClaimSubmissionServiceTests; the tests below assert the
    // controller wires correctly and emits the deprecation signal.
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SubmitClaim_ValidClaim_RoutesThroughSubmissionService_Returns201()
    {
        var claim = CreateValidClaim();

        _submissionService
            .SubmitAsync(Arg.Any<AdapterClaim>(), "test-tenant",
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var inbound = ci.Arg<AdapterClaim>();
                inbound.Id = "assigned-by-adapter";
                inbound.ClaimVersionId = "assigned-by-adapter";
                inbound.VersionNumber = 1;
                inbound.VersionState = ClaimVersionState.Submitted;
                inbound.Status = ClaimStatus.Submitted;
                return ClaimSubmissionResult.Ok(inbound);
            });

        var response = await _client.PostAsJsonAsync("/api/claims", claim);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.Contains("Deprecation"));

        var created = await response.Content.ReadFromJsonAsync<Claim>();
        Assert.NotNull(created);
        Assert.Equal("assigned-by-adapter", created.Id);
        Assert.Equal(ClaimStatus.Submitted, created.Status);
    }

    [Fact]
    public async Task SubmitClaim_ValidationFailure_Returns400_WithDeprecationHeader()
    {
        var claim = CreateValidClaim(lines: new List<ClaimLine>());

        _submissionService
            .SubmitAsync(Arg.Any<AdapterClaim>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ClaimSubmissionResult.ValidationFailed(new[]
            {
                new ValidationError
                {
                    Field = "ClaimLines",
                    Code = "MinCount",
                    Message = "Claim must have at least one service line"
                }
            }));

        var response = await _client.PostAsJsonAsync("/api/claims", claim);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.Contains("Deprecation"));
    }

    // ═══════════════════════════════════════════════════════════════════
    // GET CLAIM BY ID
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetClaimById_ExistingClaim_Returns200()
    {
        var claim = CreateValidClaim();
        claim.Id = "claim-123";

        _repo.GetByIdAsync("claim-123").Returns(claim);

        var response = await _client.GetAsync("/api/claims/claim-123");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var returned = await response.Content.ReadFromJsonAsync<Claim>();
        Assert.NotNull(returned);
        Assert.Equal("claim-123", returned.Id);
        Assert.Equal("MEM-001", returned.MemberId);
    }

    [Fact]
    public async Task GetClaimById_EnrichesDiagnosisMetadataForDisplay()
    {
        var claim = CreateValidClaim();
        claim.Id = "claim-dx";
        claim.DiagnosisCodes = new List<DiagnosisCode>
        {
            new() { Code = "K08.1" },
            new() { Code = "M79.3", CodeQualifier = "", PointerNumber = 2 }
        };

        _repo.GetByIdAsync("claim-dx").Returns(claim);

        var response = await _client.GetAsync("/api/claims/claim-dx");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var returned = await response.Content.ReadFromJsonAsync<Claim>();
        Assert.NotNull(returned);
        Assert.Equal("Complete loss of teeth", returned!.DiagnosisCodes[0].Description);
        Assert.Equal("ABK", returned.DiagnosisCodes[0].CodeQualifier);
        Assert.Equal(1, returned.DiagnosisCodes[0].PointerNumber);
        Assert.Equal("Panniculitis, unspecified", returned.DiagnosisCodes[1].Description);
        Assert.Equal("ABF", returned.DiagnosisCodes[1].CodeQualifier);
        Assert.Equal(2, returned.DiagnosisCodes[1].PointerNumber);
    }

    [Fact]
    public async Task GetClaimById_NonexistentClaim_Returns404()
    {
        _repo.GetByIdAsync("nonexistent").Returns((Claim?)null);

        var response = await _client.GetAsync("/api/claims/nonexistent");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetAdjudicationDetail_AdjudicatedClaim_ReturnsProjection()
    {
        var claim = CreateValidClaim();
        claim.Id = "claim-adj-1";
        claim.Status = ClaimStatus.Approved;
        claim.AdjudicationResult = new AdjudicationResult
        {
            NetworkTier = "InNetwork",
            AllowedAmount = 100m,
            DeductibleAmount = 10m,
            CoinsuranceAmount = 5m,
            CopayAmount = 20m,
            PatientResponsibility = 35m,
            PayerPayment = 65m
        };
        claim.ClaimLines[0].AdjudicationResult = new LineAdjudicationResult
        {
            AllowedAmount = 100m,
            PaidAmount = 65m,
            PatientResponsibility = 35m
        };
        _repo.GetByIdAsync("claim-adj-1").Returns(claim);

        var response = await _client.GetAsync("/api/claims/claim-adj-1/adjudication-detail");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var returned = await response.Content.ReadFromJsonAsync<AdjudicationTransparencyData>();
        Assert.NotNull(returned);
        Assert.NotEmpty(returned!.Steps);
        Assert.Single(returned.FeeScheduleResults);
        Assert.Equal(65m, returned.BenefitCalculation!.PlanPayment);
    }

    [Fact]
    public async Task GetAdjudicationDetail_MissingProjection_Returns404()
    {
        var claim = CreateValidClaim();
        claim.Id = "claim-unadjudicated";
        _repo.GetByIdAsync("claim-unadjudicated").Returns(claim);

        var response = await _client.GetAsync("/api/claims/claim-unadjudicated/adjudication-detail");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════════
    // GET CLAIM BY NUMBER
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetClaimByNumber_ExistingClaim_Returns200()
    {
        var claim = CreateValidClaim();
        claim.Id = "claim-456";

        _repo.GetByClaimNumberAsync("CLM-20260115-001").Returns(claim);

        var response = await _client.GetAsync("/api/claims/number/CLM-20260115-001");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var returned = await response.Content.ReadFromJsonAsync<Claim>();
        Assert.NotNull(returned);
        Assert.Equal("CLM-20260115-001", returned.ClaimNumber);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SEARCH CLAIMS
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SearchClaims_ByMemberId_ReturnsFilteredResults()
    {
        var claim1 = CreateValidClaim();
        claim1.Id = "c1";
        claim1.MemberId = "MEM-001";

        var claim2 = CreateValidClaim();
        claim2.Id = "c2";
        claim2.MemberId = "MEM-001";

        _repo.SearchAsync("MEM-001", null, null, null, null, null, 1, 50)
            .Returns(new List<Claim> { claim1, claim2 });

        var response = await _client.GetAsync("/api/claims/search?memberId=MEM-001");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var results = await response.Content.ReadFromJsonAsync<List<Claim>>();
        Assert.NotNull(results);
        Assert.Equal(2, results.Count);
        Assert.All(results, c => Assert.Equal("MEM-001", c.MemberId));
    }

    [Fact]
    public async Task SearchClaimsPost_ByRunId_ReturnsClaimsFromMassAdjudicationRun()
    {
        var runId = $"run-{Guid.NewGuid():N}";
        var claim = CreateValidClaim();
        claim.Id = "submitted-claim-1";
        claim.ClaimNumber = "MCC-P-0000001";

        _massRunRepo.GetAsync("test-tenant", runId, Arg.Any<CancellationToken>())
            .Returns(new MassAdjudicationRunSummary
            {
                Id = runId,
                Run = new MassAdjudicationRunMetadata { TenantId = "test-tenant" }
            });
        _massRunRepo.ListSubmittedClaimIdsAsync("test-tenant", runId, Arg.Any<CancellationToken>())
            .Returns(new List<string> { "submitted-claim-1", "submitted-claim-2" });
        _repo.SearchByIdsAsync(
                Arg.Any<IReadOnlyCollection<string>>(),
                null,
                null,
                null,
                null,
                null,
                1,
                25)
            .Returns((new List<Claim> { claim }, 1));

        var response = await _client.PostAsJsonAsync("/api/claims/search", new ClaimSearchBody
        {
            RunId = runId,
            PageNumber = 1,
            PageSize = 25
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, doc.RootElement.GetProperty("totalCount").GetInt32());
        var claims = doc.RootElement.GetProperty("claims");
        Assert.Equal("MCC-P-0000001", claims[0].GetProperty("claimNumber").GetString());

        await _repo.Received(1).SearchByIdsAsync(
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.Contains("submitted-claim-1") && ids.Contains("submitted-claim-2")),
            null,
            null,
            null,
            null,
            null,
            1,
            25);
    }

    [Fact]
    public async Task SearchClaimsPost_StandardSearch_ReturnsFullTotalCount()
    {
        var claim = CreateValidClaim();
        claim.Id = "page-claim-1";
        claim.ClaimNumber = "MCC-P-0000001";

        _repo.SearchWithCountAsync(
                "MEM-001",
                null,
                null,
                null,
                null,
                null,
                2,
                1)
            .Returns((new List<Claim> { claim }, 3));

        var response = await _client.PostAsJsonAsync("/api/claims/search", new ClaimSearchBody
        {
            MemberId = "MEM-001",
            PageNumber = 2,
            PageSize = 1
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(3, doc.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("pageNumber").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("claims").GetArrayLength());
    }

    // ═══════════════════════════════════════════════════════════════════
    // 277CA ACKNOWLEDGMENT
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetAcknowledgment_ExistingClaim_Returns200WithEdiContent()
    {
        var claim = CreateValidClaim();
        claim.Id = "claim-ack";

        _repo.GetByIdAsync("claim-ack").Returns(claim);
        _ackService.Generate277CA(Arg.Any<Claim>(), Arg.Any<ClaimAcknowledgmentConfig>())
            .Returns("ISA*00*...");

        var response = await _client.GetAsync("/api/claims/claim-ack/277ca");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Equal("ISA*00*...", content);
    }

    // ═══════════════════════════════════════════════════════════════════
    // TENANT HEADER
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task MissingTenantHeader_FallsBackToDefaultTenant()
    {
        // TenantMiddleware falls back to "default-tenant" when no header is present.
        // Verify the request passes through (no 4xx from middleware).
        var clientNoTenant = _factory.CreateClient();
        // No X-Tenant-ID header added

        _repo.GetByIdAsync("any-id").Returns((Claim?)null);

        var response = await clientNoTenant.GetAsync("/api/claims/any-id");

        // 404 means the middleware did NOT reject the request — it passed through
        // to the controller which returned 404 for a nonexistent claim.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
