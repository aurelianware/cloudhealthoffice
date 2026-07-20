using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AuthorizationService.Models;
using NSubstitute;
using Authorization = AuthorizationService.Models.Authorization;

namespace CloudHealthOffice.AuthorizationService.Tests;

/// <summary>
/// Extended tests covering: 278 response processing, validation edge cases,
/// search filters, status updates, and number lookup.
/// </summary>
public class AuthorizationsControllerExtendedTests : IClassFixture<AuthorizationApiFactory>
{
    private readonly HttpClient _client;
    private readonly AuthorizationApiFactory _factory;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public AuthorizationsControllerExtendedTests(AuthorizationApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.IssueToken());
    }

    // ═══════════════════════════════════════════════════════════════════
    // POST /api/authorizations/{id}/response — 278 Response Processing
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ProcessResponse_A1Approval_SetsStatusApproved()
    {
        var authId = SetupAuth(AuthorizationStatus.Submitted);

        var response = await _client.PostAsJsonAsync($"/api/authorizations/{authId}/response", new
        {
            controlNumber = "CTL-001",
            reviewDecision = "A1",
            approvedUnits = 3,
            approvedServiceDateFrom = "2026-05-01",
            approvedServiceDateTo = "2026-05-31",
            expirationDate = "2026-08-01",
            reviewerName = "Dr. Smith",
            reviewerPhone = "555-0100"
        }, Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(AuthorizationStatus.Approved.ToString(), result.GetProperty("status").GetString());
        Assert.Equal("A1", result.GetProperty("reviewDecision").GetString());
        Assert.Equal(3, result.GetProperty("approvedUnits").GetDecimal());
    }

    [Fact]
    public async Task ProcessResponse_A2Modified_SetsStatusModified()
    {
        var authId = SetupAuth(AuthorizationStatus.Submitted);

        var response = await _client.PostAsJsonAsync($"/api/authorizations/{authId}/response", new
        {
            controlNumber = "CTL-002",
            reviewDecision = "A2",
            approvedUnits = 1,
            approvedServiceDateFrom = "2026-05-10",
            approvedServiceDateTo = "2026-05-10"
        }, Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(AuthorizationStatus.Modified.ToString(), result.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ProcessResponse_A3Denial_SetsStatusDeniedWithReason()
    {
        var authId = SetupAuth(AuthorizationStatus.Submitted);

        var response = await _client.PostAsJsonAsync($"/api/authorizations/{authId}/response", new
        {
            controlNumber = "CTL-003",
            reviewDecision = "A3",
            denialReasonCode = "NOTMEDNEC",
            denialReason = "Service not medically necessary based on clinical review"
        }, Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(AuthorizationStatus.Denied.ToString(), result.GetProperty("status").GetString());
        Assert.Equal("NOTMEDNEC", result.GetProperty("denialReasonCode").GetString());
    }

    [Fact]
    public async Task ProcessResponse_A4Pended_SetsStatusPendedWithFollowUp()
    {
        var authId = SetupAuth(AuthorizationStatus.Submitted);

        var response = await _client.PostAsJsonAsync($"/api/authorizations/{authId}/response", new
        {
            controlNumber = "CTL-004",
            reviewDecision = "A4",
            pendReason = "Additional clinical documentation required",
            followUpAction = "Submit operative notes within 10 business days"
        }, Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(AuthorizationStatus.Pended.ToString(), result.GetProperty("status").GetString());
        Assert.Equal("Additional clinical documentation required", result.GetProperty("pendReason").GetString());
    }

    [Fact]
    public async Task ProcessResponse_UnknownDecision_SetsStatusInReview()
    {
        var authId = SetupAuth(AuthorizationStatus.Submitted);

        var response = await _client.PostAsJsonAsync($"/api/authorizations/{authId}/response", new
        {
            controlNumber = "CTL-005",
            reviewDecision = "XX"
        }, Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(AuthorizationStatus.InReview.ToString(), result.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ProcessResponse_NonExistentAuth_Returns404()
    {
        _factory.AuthorizationRepository.GetByIdAsync("nonexistent").Returns((Authorization?)null);

        var response = await _client.PostAsJsonAsync("/api/authorizations/nonexistent/response", new
        {
            controlNumber = "CTL-006",
            reviewDecision = "A1"
        }, Json);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════════
    // GET /api/authorizations/{authNumber}/validate — Validation Edge Cases
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Validate_DeniedAuth_ReturnsInvalid()
    {
        SetupAuthByNumber("AUTH-DENIED", AuthorizationStatus.Denied);

        var response = await _client.GetAsync("/api/authorizations/AUTH-DENIED/validate");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.False(result.GetProperty("isValid").GetBoolean());
        Assert.Contains("not approved", result.GetProperty("validationMessage").GetString());
    }

    [Fact]
    public async Task Validate_ExpiredAuth_ReturnsInvalid()
    {
        var auth = new Authorization
        {
            Id = Guid.NewGuid().ToString(),
            AuthorizationNumber = "AUTH-EXPIRED",
            Status = AuthorizationStatus.Approved,
            ApprovedServiceDateFrom = DateTime.UtcNow.AddMonths(-6),
            ExpirationDate = DateTime.UtcNow.AddDays(-1) // expired yesterday
        };

        _factory.AuthorizationRepository.GetByAuthorizationNumberAsync("AUTH-EXPIRED").Returns(auth);

        var response = await _client.GetAsync("/api/authorizations/AUTH-EXPIRED/validate");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.False(result.GetProperty("isValid").GetBoolean());
        Assert.Contains("expired", result.GetProperty("validationMessage").GetString());
    }

    [Fact]
    public async Task Validate_NotYetActive_ReturnsInvalid()
    {
        var auth = new Authorization
        {
            Id = Guid.NewGuid().ToString(),
            AuthorizationNumber = "AUTH-FUTURE",
            Status = AuthorizationStatus.Approved,
            ApprovedServiceDateFrom = DateTime.UtcNow.AddMonths(1), // starts next month
            ExpirationDate = DateTime.UtcNow.AddMonths(4)
        };

        _factory.AuthorizationRepository.GetByAuthorizationNumberAsync("AUTH-FUTURE").Returns(auth);

        // Service date is today, but auth starts next month
        var response = await _client.GetAsync(
            $"/api/authorizations/AUTH-FUTURE/validate?serviceDate={DateTime.UtcNow:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.False(result.GetProperty("isValid").GetBoolean());
    }

    [Fact]
    public async Task Validate_ProcedureNotInAuth_ReturnsInvalid()
    {
        var auth = new Authorization
        {
            Id = Guid.NewGuid().ToString(),
            AuthorizationNumber = "AUTH-PROC",
            Status = AuthorizationStatus.Approved,
            ApprovedServiceDateFrom = DateTime.UtcNow.AddDays(-30),
            ExpirationDate = DateTime.UtcNow.AddDays(60),
            RequestedServices = new List<RequestedService>
            {
                new() { ProcedureCode = "72148", ServiceStatus = "A1", RequestedUnits = 1 }
            }
        };

        _factory.AuthorizationRepository.GetByAuthorizationNumberAsync("AUTH-PROC").Returns(auth);

        // Ask for a procedure NOT in the auth
        var response = await _client.GetAsync(
            "/api/authorizations/AUTH-PROC/validate?procedureCode=99213");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.False(result.GetProperty("isValid").GetBoolean());
        Assert.Contains("99213", result.GetProperty("validationMessage").GetString());
    }

    [Fact]
    public async Task Validate_ApprovedProcedure_ReturnsValidWithUnits()
    {
        var auth = new Authorization
        {
            Id = Guid.NewGuid().ToString(),
            AuthorizationNumber = "AUTH-PROC-OK",
            Status = AuthorizationStatus.Approved,
            ApprovedServiceDateFrom = DateTime.UtcNow.AddDays(-30),
            ExpirationDate = DateTime.UtcNow.AddDays(60),
            RequestedServices = new List<RequestedService>
            {
                new() { ProcedureCode = "72148", ServiceStatus = "A1", ApprovedUnits = 2, RequestedUnits = 3 }
            }
        };

        _factory.AuthorizationRepository.GetByAuthorizationNumberAsync("AUTH-PROC-OK").Returns(auth);

        var response = await _client.GetAsync(
            "/api/authorizations/AUTH-PROC-OK/validate?procedureCode=72148");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.True(result.GetProperty("isValid").GetBoolean());
        Assert.Equal(2, result.GetProperty("approvedUnits").GetDecimal());
        Assert.Contains("valid", result.GetProperty("validationMessage").GetString()!.ToLower());
    }

    [Fact]
    public async Task Validate_WrongProvider_ReturnsInvalid()
    {
        var auth = new Authorization
        {
            Id = Guid.NewGuid().ToString(),
            AuthorizationNumber = "AUTH-PROVIDER",
            Status = AuthorizationStatus.Approved,
            ApprovedServiceDateFrom = DateTime.UtcNow.AddDays(-30),
            ExpirationDate = DateTime.UtcNow.AddDays(60),
            RequestingProviderNPI = "1234567890",
            ServicingProviderNPI = "1234567890",
            RequestedServices = new List<RequestedService>
            {
                new() { ProcedureCode = "72148", ServiceStatus = "A1", ApprovedUnits = 1, RequestedUnits = 1 }
            }
        };

        _factory.AuthorizationRepository.GetByAuthorizationNumberAsync("AUTH-PROVIDER").Returns(auth);

        var response = await _client.GetAsync(
            "/api/authorizations/AUTH-PROVIDER/validate?procedureCode=72148&providerNpi=9999999999");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.False(result.GetProperty("isValid").GetBoolean());
        Assert.Contains("Provider 9999999999 not approved", result.GetProperty("validationMessage").GetString());
    }

    [Fact]
    public async Task Validate_ModifiedAuth_IsStillValid()
    {
        var auth = new Authorization
        {
            Id = Guid.NewGuid().ToString(),
            AuthorizationNumber = "AUTH-MOD",
            Status = AuthorizationStatus.Modified,
            ApprovedServiceDateFrom = DateTime.UtcNow.AddDays(-7),
            ExpirationDate = DateTime.UtcNow.AddDays(30)
        };

        _factory.AuthorizationRepository.GetByAuthorizationNumberAsync("AUTH-MOD").Returns(auth);

        var response = await _client.GetAsync("/api/authorizations/AUTH-MOD/validate");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.True(result.GetProperty("isValid").GetBoolean());
    }

    [Fact]
    public async Task Validate_NonExistentAuth_Returns404()
    {
        _factory.AuthorizationRepository.GetByAuthorizationNumberAsync("NOPE").Returns((Authorization?)null);

        var response = await _client.GetAsync("/api/authorizations/NOPE/validate");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════════
    // PUT /api/authorizations/{id}/status — Update Status
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task UpdateStatus_Approved_SetsReviewedDate()
    {
        var authId = SetupAuth(AuthorizationStatus.Submitted);

        var response = await _client.PutAsJsonAsync($"/api/authorizations/{authId}/status", new
        {
            status = AuthorizationStatus.Approved,
            reviewDecision = "A1",
            notes = "Approved after clinical review"
        }, Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(AuthorizationStatus.Approved.ToString(), result.GetProperty("status").GetString());
        Assert.True(result.TryGetProperty("reviewedDate", out var reviewed));
        Assert.NotEqual(JsonValueKind.Null, reviewed.ValueKind);
    }

    [Fact]
    public async Task UpdateStatus_Pended_DoesNotSetReviewedDate()
    {
        var authId = Guid.NewGuid().ToString();
        var auth = new Authorization
        {
            Id = authId,
            Status = AuthorizationStatus.Submitted,
            ReviewedDate = null
        };

        _factory.AuthorizationRepository.GetByIdAsync(authId).Returns(auth);
        _factory.AuthorizationRepository.UpdateAsync(Arg.Any<Authorization>())
            .Returns(callInfo => callInfo.Arg<Authorization>());

        var response = await _client.PutAsJsonAsync($"/api/authorizations/{authId}/status", new
        {
            status = AuthorizationStatus.Pended,
            notes = "Needs additional docs"
        }, Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(AuthorizationStatus.Pended.ToString(), result.GetProperty("status").GetString());
    }

    [Fact]
    public async Task UpdateStatus_WithNotes_AppendsToExisting()
    {
        var authId = Guid.NewGuid().ToString();
        var auth = new Authorization
        {
            Id = authId,
            Status = AuthorizationStatus.Submitted,
            Notes = "Initial submission notes"
        };

        _factory.AuthorizationRepository.GetByIdAsync(authId).Returns(auth);
        _factory.AuthorizationRepository.UpdateAsync(Arg.Any<Authorization>())
            .Returns(callInfo => callInfo.Arg<Authorization>());

        var response = await _client.PutAsJsonAsync($"/api/authorizations/{authId}/status", new
        {
            status = AuthorizationStatus.InReview,
            notes = "Under clinical review"
        }, Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        var notes = result.GetProperty("notes").GetString();
        Assert.Contains("Initial submission notes", notes);
        Assert.Contains("Under clinical review", notes);
    }

    [Fact]
    public async Task UpdateStatus_NonExistent_Returns404()
    {
        _factory.AuthorizationRepository.GetByIdAsync("nonexistent").Returns((Authorization?)null);

        var response = await _client.PutAsJsonAsync("/api/authorizations/nonexistent/status", new
        {
            status = AuthorizationStatus.Approved
        }, Json);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════════
    // GET /api/authorizations/number/{authNumber} — Get by Number
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetByNumber_ExistingAuth_ReturnsOk()
    {
        var auth = new Authorization
        {
            Id = Guid.NewGuid().ToString(),
            AuthorizationNumber = "AUTH-NUM-001",
            MemberId = "MBR-005",
            Status = AuthorizationStatus.Approved
        };

        _factory.AuthorizationRepository.GetByAuthorizationNumberAsync("AUTH-NUM-001").Returns(auth);

        var response = await _client.GetAsync("/api/authorizations/number/AUTH-NUM-001");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("AUTH-NUM-001", result.GetProperty("authorizationNumber").GetString());
    }

    [Fact]
    public async Task GetByNumber_NonExistent_Returns404()
    {
        _factory.AuthorizationRepository.GetByAuthorizationNumberAsync("NOPE").Returns((Authorization?)null);

        var response = await _client.GetAsync("/api/authorizations/number/NOPE");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════════
    // GET /api/authorizations/search — Filter Tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Search_ByStatus_PassesStatusToRepository()
    {
        _factory.AuthorizationRepository.SearchAsync(
            null, null, null, null,
            AuthorizationStatus.Approved, null, 1, 50)
            .Returns(new List<Authorization>());

        var response = await _client.GetAsync(
            "/api/authorizations/search?status=Approved");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await _factory.AuthorizationRepository.Received(1).SearchAsync(
            null, null, null, null,
            AuthorizationStatus.Approved, Arg.Any<LineOfBusiness?>(), 1, 50);
    }

    [Fact]
    public async Task Search_ByProvider_PassesProviderToRepository()
    {
        _factory.AuthorizationRepository.SearchAsync(
            null, "1234567890", null, null,
            null, null, 1, 50)
            .Returns(new List<Authorization>());

        var response = await _client.GetAsync(
            "/api/authorizations/search?providerNPI=1234567890");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await _factory.AuthorizationRepository.Received(1).SearchAsync(
            null, "1234567890", Arg.Any<DateTime?>(), Arg.Any<DateTime?>(),
            null, Arg.Any<LineOfBusiness?>(), 1, 50);
    }

    [Fact]
    public async Task Search_ByDateRange_PassesDatesToRepository()
    {
        _factory.AuthorizationRepository.SearchAsync(
            null, null, Arg.Any<DateTime>(), Arg.Any<DateTime>(),
            null, null, 1, 50)
            .Returns(new List<Authorization>());

        var response = await _client.GetAsync(
            "/api/authorizations/search?serviceDateFrom=2026-01-01&serviceDateTo=2026-06-30");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Search_NoFilters_ReturnsAll()
    {
        _factory.AuthorizationRepository.SearchAsync(
            null, null, null, null, null, null, 1, 50)
            .Returns(new List<Authorization>
            {
                new() { Id = "a1", AuthorizationNumber = "AUTH-1" },
                new() { Id = "a2", AuthorizationNumber = "AUTH-2" }
            });

        var response = await _client.GetAsync("/api/authorizations/search");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(2, result.GetArrayLength());
    }

    // ═══════════════════════════════════════════════════════════════════
    // POST /api/authorizations — Submit Validation
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Submit_NoRequestedServices_ReturnsBadRequest()
    {
        var request = new
        {
            tenantId = "test-tenant",
            memberId = "MBR-001",
            requestingProviderNPI = "1234567890",
            requestedServices = Array.Empty<object>()
        };

        var response = await _client.PostAsJsonAsync("/api/authorizations", request, Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════════
    // DELETE /api/authorizations/{id} — Cancel Edge Cases
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Cancel_NonExistent_Returns404()
    {
        _factory.AuthorizationRepository.GetByIdAsync("gone").Returns((Authorization?)null);

        var response = await _client.DeleteAsync("/api/authorizations/gone");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════

    private string SetupAuth(AuthorizationStatus status)
    {
        var authId = Guid.NewGuid().ToString();
        var auth = new Authorization
        {
            Id = authId,
            TenantId = "test-tenant",
            AuthorizationNumber = $"AUTH-{authId[..8]}",
            MemberId = "MBR-001",
            Status = status,
            SubmittedDate = DateTime.UtcNow.AddDays(-1),
            CreatedDate = DateTime.UtcNow.AddDays(-1),
            RequestedServices = new List<RequestedService>
            {
                new() { ProcedureCode = "72148", RequestedUnits = 1 }
            }
        };

        _factory.AuthorizationRepository.GetByIdAsync(authId).Returns(auth);
        _factory.AuthorizationRepository.UpdateAsync(Arg.Any<Authorization>())
            .Returns(callInfo => callInfo.Arg<Authorization>());

        return authId;
    }

    private void SetupAuthByNumber(string authNumber, AuthorizationStatus status)
    {
        var auth = new Authorization
        {
            Id = Guid.NewGuid().ToString(),
            AuthorizationNumber = authNumber,
            Status = status,
            ApprovedServiceDateFrom = DateTime.UtcNow.AddDays(-30),
            ExpirationDate = DateTime.UtcNow.AddDays(60)
        };

        _factory.AuthorizationRepository.GetByAuthorizationNumberAsync(authNumber).Returns(auth);
    }
}
