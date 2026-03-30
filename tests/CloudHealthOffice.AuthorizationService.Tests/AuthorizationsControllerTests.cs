using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AuthorizationService.Models;
using Authorization = AuthorizationService.Models.Authorization;
using NSubstitute;

namespace CloudHealthOffice.AuthorizationService.Tests;

public class AuthorizationsControllerTests : IClassFixture<AuthorizationApiFactory>
{
    private readonly HttpClient _client;
    private readonly AuthorizationApiFactory _factory;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public AuthorizationsControllerTests(AuthorizationApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.IssueToken());
    }

    // ═══════════════════════════════════════════════════════════════════
    // Health
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════════
    // POST /api/authorizations — Submit Authorization
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SubmitAuthorization_ValidRequest_ReturnsCreated()
    {
        var expectedAuth = new Authorization
        {
            Id = Guid.NewGuid().ToString(),
            TenantId = "test-tenant",
            AuthorizationNumber = "AUTH-001",
            MemberId = "MBR-001",
            Status = AuthorizationStatus.Submitted,
            CreatedDate = DateTime.UtcNow
        };

        _factory.AuthorizationRepository.CreateAsync(Arg.Any<Authorization>())
            .Returns(expectedAuth);

        var request = new
        {
            tenantId = "test-tenant",
            authorizationNumber = "AUTH-001",
            memberId = "MBR-001",
            coverageId = "COV-001",
            patientFirstName = "John",
            patientLastName = "Smith",
            patientDateOfBirth = "1978-03-22",
            requestingProviderNPI = "1234567890",
            requestingProviderName = "Test Primary Care",
            serviceTypeCode = "42",
            levelOfService = "E",
            requestedServiceDateFrom = DateTime.UtcNow.AddDays(7),
            requestedServiceDateTo = DateTime.UtcNow.AddDays(7),
            diagnosisCodes = new[]
            {
                new { code = "M54.5", codeQualifier = "BK", description = "Low back pain" }
            },
            requestedServices = new[]
            {
                new
                {
                    procedureCode = "72148",
                    procedureDescription = "MRI Lumbar Spine",
                    requestedUnits = 1,
                    unitType = "UN",
                    placeOfServiceCode = "22"
                }
            },
            authorizationType = 0
        };

        var response = await _client.PostAsJsonAsync("/api/authorizations", request, Json);

        Assert.True(
            response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK,
            $"Expected 201/200 but got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    // ═══════════════════════════════════════════════════════════════════
    // GET /api/authorizations/{id} — Get Authorization
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetById_ExistingAuth_ReturnsOk()
    {
        var authId = Guid.NewGuid().ToString();
        var auth = new Authorization
        {
            Id = authId,
            TenantId = "test-tenant",
            AuthorizationNumber = "AUTH-002",
            MemberId = "MBR-002",
            Status = AuthorizationStatus.Approved,
            CreatedDate = DateTime.UtcNow
        };

        _factory.AuthorizationRepository.GetByIdAsync(authId).Returns(auth);

        var response = await _client.GetAsync($"/api/authorizations/{authId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(authId, result.GetProperty("id").GetString());
    }

    [Fact]
    public async Task GetById_NonExistent_ReturnsNotFound()
    {
        _factory.AuthorizationRepository.GetByIdAsync("nonexistent").Returns((Authorization?)null);

        var response = await _client.GetAsync("/api/authorizations/nonexistent");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════════
    // GET /api/authorizations/search
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Search_ByMember_ReturnsResults()
    {
        var auths = new List<Authorization>
        {
            new()
            {
                Id = Guid.NewGuid().ToString(),
                TenantId = "test-tenant",
                MemberId = "MBR-003",
                AuthorizationNumber = "AUTH-003",
                Status = AuthorizationStatus.Approved
            }
        };

        _factory.AuthorizationRepository.SearchAsync(
            "MBR-003", Arg.Any<string?>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(),
            Arg.Any<AuthorizationStatus?>(), Arg.Any<LineOfBusiness?>(), 1, 10)
            .Returns(auths);

        var response = await _client.GetAsync(
            "/api/authorizations/search?memberId=MBR-003&page=1&pageSize=10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════════
    // GET /api/authorizations/{authNumber}/validate
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Validate_ApprovedAuth_ReturnsValid()
    {
        var auth = new Authorization
        {
            Id = Guid.NewGuid().ToString(),
            TenantId = "test-tenant",
            AuthorizationNumber = "AUTH-VAL-001",
            MemberId = "MBR-004",
            Status = AuthorizationStatus.Approved,
            ApprovedUnits = 1,
            ApprovedServiceDateFrom = DateTime.UtcNow.AddDays(-1),
            ApprovedServiceDateTo = DateTime.UtcNow.AddDays(30),
            ExpirationDate = DateTime.UtcNow.AddDays(90)
        };

        _factory.AuthorizationRepository.GetByAuthorizationNumberAsync("AUTH-VAL-001")
            .Returns(auth);

        var response = await _client.GetAsync("/api/authorizations/AUTH-VAL-001/validate");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.True(result.TryGetProperty("isValid", out _));
    }

    // ═══════════════════════════════════════════════════════════════════
    // DELETE /api/authorizations/{id} — Cancel Authorization
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Cancel_ExistingAuth_ReturnsSuccess()
    {
        var authId = Guid.NewGuid().ToString();
        var auth = new Authorization
        {
            Id = authId,
            TenantId = "test-tenant",
            Status = AuthorizationStatus.Submitted
        };

        _factory.AuthorizationRepository.GetByIdAsync(authId).Returns(auth);

        var response = await _client.DeleteAsync($"/api/authorizations/{authId}");

        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"Expected 200/204 but got {response.StatusCode}");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Authentication — Unauthenticated access
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task UnauthenticatedRequest_ReturnsUnauthorized()
    {
        using var noAuthClient = _factory.CreateClient();
        // No Authorization header

        var response = await noAuthClient.GetAsync("/api/authorizations/search?page=1&pageSize=10");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
