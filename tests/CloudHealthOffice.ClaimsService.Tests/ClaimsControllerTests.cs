using System.Net;
using System.Net.Http.Json;
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
    private readonly IClaimAcknowledgmentService _ackService;

    public ClaimsControllerTests(ClaimsApiFactory factory)
    {
        _factory = factory;
        _repo = factory.ClaimRepository;
        _ackService = factory.AcknowledgmentService;
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
    // SUBMIT CLAIM
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SubmitClaim_ValidClaim_Returns201WithIdAssigned()
    {
        var claim = CreateValidClaim();

        _repo.CreateAsync(Arg.Any<Claim>())
            .Returns(ci => ci.Arg<Claim>());

        var response = await _client.PostAsJsonAsync("/api/claims", claim);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<Claim>();
        Assert.NotNull(created);
        Assert.NotNull(created.Id);
        Assert.NotEmpty(created.Id);
        Assert.Equal(ClaimStatus.Submitted, created.Status);
    }

    [Fact]
    public async Task SubmitClaim_ZeroLines_Returns400()
    {
        var claim = CreateValidClaim(lines: new List<ClaimLine>());

        var response = await _client.PostAsJsonAsync("/api/claims", claim);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SubmitClaim_CalculatesTotalChargeFromLines()
    {
        var serviceDate = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var lines = new List<ClaimLine>
        {
            new()
            {
                LineNumber = 1,
                ProcedureCode = "99213",
                ChargeAmount = 150.00m,
                Units = 2,
                ServiceDateFrom = serviceDate,
                ServiceDateTo = serviceDate,
                DiagnosisPointers = new List<int> { 1 }
            },
            new()
            {
                LineNumber = 2,
                ProcedureCode = "85025",
                ChargeAmount = 35.50m,
                Units = 1,
                ServiceDateFrom = serviceDate,
                ServiceDateTo = serviceDate,
                DiagnosisPointers = new List<int> { 1 }
            }
        };
        var claim = CreateValidClaim(lines: lines);

        Claim? capturedClaim = null;
        _repo.CreateAsync(Arg.Any<Claim>())
            .Returns(ci =>
            {
                capturedClaim = ci.Arg<Claim>();
                return capturedClaim;
            });

        var response = await _client.PostAsJsonAsync("/api/claims", claim);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(capturedClaim);
        // 150 * 2 + 35.50 * 1 = 335.50
        Assert.Equal(335.50m, capturedClaim.TotalChargeAmount);
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
    public async Task GetClaimById_NonexistentClaim_Returns404()
    {
        _repo.GetByIdAsync("nonexistent").Returns((Claim?)null);

        var response = await _client.GetAsync("/api/claims/nonexistent");

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
