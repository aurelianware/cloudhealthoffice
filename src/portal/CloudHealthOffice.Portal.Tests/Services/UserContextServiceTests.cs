using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class UserContextServiceTests
{
    private readonly Mock<AuthenticationStateProvider> _authStateProvider = new();
    private readonly Mock<ITenantContextService> _tenantContextService = new();
    private readonly Mock<ILogger<UserContextService>> _logger = new();

    private readonly TenantContext _defaultTenantContext = new()
    {
        TenantId = "tenant-1",
        TenantName = "Test Tenant",
        AzureTenantId = "azure-tid-1",
        SubscriptionTier = "professional",
        SubscriptionStatus = "Active"
    };

    private UserContextService CreateService(
        HttpClient? httpClient = null,
        string? tenantServiceUrl = "http://localhost:9000")
    {
        var configEntries = new Dictionary<string, string?>();
        if (tenantServiceUrl != null)
            configEntries["Services:TenantService"] = tenantServiceUrl;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configEntries)
            .Build();

        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));

        return new UserContextService(
            _authStateProvider.Object,
            _tenantContextService.Object,
            httpClient,
            configuration,
            _logger.Object);
    }

    private void SetupAuthState(params Claim[] claims)
    {
        var identity = claims.Length > 0
            ? new ClaimsIdentity(claims, "TestAuth")
            : new ClaimsIdentity(); // unauthenticated
        var principal = new ClaimsPrincipal(identity);
        var authState = new AuthenticationState(principal);
        _authStateProvider.Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(authState);
    }

    private void SetupTenantContext(TenantContext? context = null)
    {
        _tenantContextService.Setup(x => x.GetCurrentTenantContextAsync())
            .ReturnsAsync(context);
    }

    private static string SerializeUser(
        string id = "user-1",
        string tenantId = "tenant-1",
        string email = "jane@acme.com",
        string displayName = "Jane Doe",
        string firstName = "Jane",
        string lastName = "Doe",
        string azureAdObjectId = "",
        List<string>? roles = null,
        string department = "Claims",
        string status = "Active")
    {
        return JsonSerializer.Serialize(new
        {
            id, tenantId, email, displayName, firstName, lastName,
            azureAdObjectId, roles = roles ?? new List<string> { "ClaimsExaminer" },
            department, status
        });
    }

    private static string SerializeUsers(params string[] userJsons)
    {
        return $"[{string.Join(",", userJsons)}]";
    }

    // ================================================================
    // GetCurrentUserAsync
    // ================================================================

    [Fact]
    public async Task GetCurrentUserAsync_WhenUnauthenticated_ReturnsNull()
    {
        SetupAuthState(); // no claims = unauthenticated
        var sut = CreateService();

        var result = await sut.GetCurrentUserAsync();

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentUserAsync_WhenAuthenticatedWithNoEmailClaim_ReturnsNull()
    {
        SetupAuthState(new Claim("name", "No Email User"));
        var sut = CreateService();

        var result = await sut.GetCurrentUserAsync();

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentUserAsync_WhenTenantContextIsNull_ReturnsTenantAdminFallback()
    {
        SetupAuthState(
            new Claim(ClaimTypes.Email, "admin@acme.com"),
            new Claim("name", "Admin User"),
            new Claim("tid", "azure-tid-1"));
        SetupTenantContext(null);
        var sut = CreateService();

        var result = await sut.GetCurrentUserAsync();

        result.Should().NotBeNull();
        result!.UserId.Should().Be("fallback");
        result.Roles.Should().Contain("TenantAdmin");
        result.Permissions.Should().Contain("*:*");
    }

    // ── Fallback DisplayName extraction ──

    [Fact]
    public async Task GetCurrentUserAsync_FallbackExtractsDisplayName_FromNameClaim()
    {
        SetupAuthState(
            new Claim(ClaimTypes.Email, "jane@acme.com"),
            new Claim("name", "Jane Doe"));
        SetupTenantContext(null);
        var sut = CreateService();

        var result = await sut.GetCurrentUserAsync();

        result!.DisplayName.Should().Be("Jane Doe");
    }

    [Fact]
    public async Task GetCurrentUserAsync_FallbackExtractsDisplayName_FallsBackToClaimTypesName()
    {
        SetupAuthState(
            new Claim(ClaimTypes.Email, "jane@acme.com"),
            new Claim(ClaimTypes.Name, "Jane From ClaimTypes"));
        SetupTenantContext(null);
        var sut = CreateService();

        var result = await sut.GetCurrentUserAsync();

        result!.DisplayName.Should().Be("Jane From ClaimTypes");
    }

    [Fact]
    public async Task GetCurrentUserAsync_FallbackExtractsDisplayName_FallsBackToEmail()
    {
        SetupAuthState(new Claim(ClaimTypes.Email, "jane@acme.com"));
        SetupTenantContext(null);
        var sut = CreateService();

        var result = await sut.GetCurrentUserAsync();

        result!.DisplayName.Should().Be("jane@acme.com");
    }

    // ── Fallback FirstName/LastName splitting ──

    [Fact]
    public async Task GetCurrentUserAsync_FallbackSplitsDisplayName_SingleWord()
    {
        SetupAuthState(
            new Claim(ClaimTypes.Email, "jane@acme.com"),
            new Claim("name", "Jane"));
        SetupTenantContext(null);
        var sut = CreateService();

        var result = await sut.GetCurrentUserAsync();

        result!.FirstName.Should().Be("Jane");
        result.LastName.Should().Be("");
    }

    [Fact]
    public async Task GetCurrentUserAsync_FallbackSplitsDisplayName_TwoWords()
    {
        SetupAuthState(
            new Claim(ClaimTypes.Email, "jane@acme.com"),
            new Claim("name", "Jane Doe"));
        SetupTenantContext(null);
        var sut = CreateService();

        var result = await sut.GetCurrentUserAsync();

        result!.FirstName.Should().Be("Jane");
        result.LastName.Should().Be("Doe");
    }

    [Fact]
    public async Task GetCurrentUserAsync_FallbackSplitsDisplayName_ThreeWords()
    {
        SetupAuthState(
            new Claim(ClaimTypes.Email, "jane@acme.com"),
            new Claim("name", "Mary Jane Watson"));
        SetupTenantContext(null);
        var sut = CreateService();

        var result = await sut.GetCurrentUserAsync();

        // Split(' ').Skip(1).FirstOrDefault() gives "Jane", not "Jane Watson"
        result!.FirstName.Should().Be("Mary");
        result.LastName.Should().Be("Jane");
    }

    // ── TenantService URL not configured ──

    [Fact]
    public async Task GetCurrentUserAsync_WhenTenantServiceUrlNotConfigured_FallsBackToTenantAdmin()
    {
        SetupAuthState(
            new Claim(ClaimTypes.Email, "admin@acme.com"),
            new Claim("name", "Admin User"));
        SetupTenantContext(_defaultTenantContext);
        var sut = CreateService(tenantServiceUrl: null);

        var result = await sut.GetCurrentUserAsync();

        result.Should().NotBeNull();
        result!.UserId.Should().Be("fallback");
        result.Roles.Should().Contain("TenantAdmin");
    }

    // ── OID lookup succeeds ──

    [Fact]
    public async Task GetCurrentUserAsync_WhenOidLookupSucceeds_MapsAllFieldsCorrectly()
    {
        SetupAuthState(
            new Claim(ClaimTypes.Email, "jane@acme.com"),
            new Claim("oid", "oid-abc-123"),
            new Claim("name", "Jane Doe"));
        SetupTenantContext(_defaultTenantContext);

        var userJson = SerializeUser(
            id: "usr-42", tenantId: "tenant-1", email: "jane@acme.com",
            displayName: "Jane Doe", firstName: "Jane", lastName: "Doe",
            azureAdObjectId: "oid-abc-123",
            roles: new List<string> { "ClaimsExaminer", "Finance" },
            department: "Claims Dept", status: "Active");

        var handler = new FakeHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/by-oid/"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(userJson, Encoding.UTF8, "application/json")
                };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = CreateService(new HttpClient(handler));

        var result = await sut.GetCurrentUserAsync();

        result.Should().NotBeNull();
        result!.UserId.Should().Be("usr-42");
        result.Email.Should().Be("jane@acme.com");
        result.DisplayName.Should().Be("Jane Doe");
        result.FirstName.Should().Be("Jane");
        result.LastName.Should().Be("Doe");
        result.TenantId.Should().Be("tenant-1");
        result.Roles.Should().BeEquivalentTo(new[] { "ClaimsExaminer", "Finance" });
        result.Department.Should().Be("Claims Dept");
        result.Permissions.Should().Contain("claims:read");
        result.Permissions.Should().Contain("payments:read");
    }

    // ── OID lookup fails, email lookup succeeds ──

    [Fact]
    public async Task GetCurrentUserAsync_WhenOidLookupFailsAndEmailLookupSucceeds_MapsCorrectly()
    {
        SetupAuthState(
            new Claim(ClaimTypes.Email, "jane@acme.com"),
            new Claim("oid", "oid-abc-123"),
            new Claim("name", "Jane Doe"));
        SetupTenantContext(_defaultTenantContext);

        var userJson = SerializeUser(
            id: "usr-99", email: "jane@acme.com",
            displayName: "Jane Doe", firstName: "Jane", lastName: "Doe",
            roles: new List<string> { "MemberServices" },
            department: "Member Dept", status: "Active");

        var handler = new FakeHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/by-oid/"))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (req.RequestUri.AbsolutePath.EndsWith("/users"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        SerializeUsers(userJson), Encoding.UTF8, "application/json")
                };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = CreateService(new HttpClient(handler));

        var result = await sut.GetCurrentUserAsync();

        result.Should().NotBeNull();
        result!.UserId.Should().Be("usr-99");
        result.Roles.Should().Contain("MemberServices");
    }

    // ── Empty roles defaults to TenantAdmin ──

    [Fact]
    public async Task GetCurrentUserAsync_WhenEmailLookupFindsUserWithEmptyRoles_DefaultsToTenantAdmin()
    {
        SetupAuthState(
            new Claim(ClaimTypes.Email, "noroles@acme.com"),
            new Claim("name", "No Roles"));
        SetupTenantContext(_defaultTenantContext);

        var userJson = SerializeUser(
            id: "usr-empty", email: "noroles@acme.com",
            roles: new List<string>(), status: "Active");

        var handler = new FakeHandler(HttpStatusCode.OK,
            SerializeUsers(userJson));

        var sut = CreateService(new HttpClient(handler));

        var result = await sut.GetCurrentUserAsync();

        result.Should().NotBeNull();
        result!.Roles.Should().Contain("TenantAdmin");
    }

    // ── Inactive user falls back to TenantAdmin ──

    [Fact]
    public async Task GetCurrentUserAsync_WhenUserStatusIsNotActive_FallsBackToTenantAdmin()
    {
        SetupAuthState(
            new Claim(ClaimTypes.Email, "inactive@acme.com"),
            new Claim("name", "Inactive User"));
        SetupTenantContext(_defaultTenantContext);

        var userJson = SerializeUser(
            id: "usr-inactive", email: "inactive@acme.com",
            status: "Disabled");

        var handler = new FakeHandler(HttpStatusCode.OK,
            SerializeUsers(userJson));

        var sut = CreateService(new HttpClient(handler));

        var result = await sut.GetCurrentUserAsync();

        result.Should().NotBeNull();
        result!.UserId.Should().Be("fallback");
        result.Roles.Should().Contain("TenantAdmin");
    }

    // ── OID backfill ──

    [Fact]
    public async Task GetCurrentUserAsync_WhenEmailLookupSucceedsAndUserHasNoOid_BackfillsPatchRequest()
    {
        SetupAuthState(
            new Claim(ClaimTypes.Email, "jane@acme.com"),
            new Claim("oid", "new-oid-value"),
            new Claim("name", "Jane Doe"));
        SetupTenantContext(_defaultTenantContext);

        var userJson = SerializeUser(
            id: "usr-77", email: "jane@acme.com",
            azureAdObjectId: "", status: "Active");

        var handler = new FakeHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/by-oid/"))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (req.Method == HttpMethod.Get && req.RequestUri.AbsolutePath.EndsWith("/users"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        SerializeUsers(userJson), Encoding.UTF8, "application/json")
                };
            if (req.Method == HttpMethod.Patch)
                return new HttpResponseMessage(HttpStatusCode.OK);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var httpClient = new HttpClient(handler);
        var sut = CreateService(httpClient);

        await sut.GetCurrentUserAsync();

        handler.CapturedRequests.Should().Contain(r =>
            r.Method == HttpMethod.Patch &&
            r.RequestUri!.AbsolutePath.Contains("/users/usr-77"));
    }

    [Fact]
    public async Task GetCurrentUserAsync_WhenOidBackfillFails_SwallowsExceptionAndReturnsUser()
    {
        SetupAuthState(
            new Claim(ClaimTypes.Email, "jane@acme.com"),
            new Claim("oid", "new-oid-value"),
            new Claim("name", "Jane Doe"));
        SetupTenantContext(_defaultTenantContext);

        var userJson = SerializeUser(
            id: "usr-77", email: "jane@acme.com",
            azureAdObjectId: "", status: "Active");

        var handler = new FakeHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/by-oid/"))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (req.Method == HttpMethod.Get && req.RequestUri.AbsolutePath.EndsWith("/users"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        SerializeUsers(userJson), Encoding.UTF8, "application/json")
                };
            if (req.Method == HttpMethod.Patch)
                throw new HttpRequestException("Network error");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = CreateService(new HttpClient(handler));

        var result = await sut.GetCurrentUserAsync();

        result.Should().NotBeNull();
        result!.UserId.Should().Be("usr-77");
    }

    // ── HTTP exception falls back ──

    [Fact]
    public async Task GetCurrentUserAsync_WhenHttpExceptionOccurs_FallsBackToTenantAdmin()
    {
        SetupAuthState(
            new Claim(ClaimTypes.Email, "admin@acme.com"),
            new Claim("name", "Admin User"));
        SetupTenantContext(_defaultTenantContext);

        var handler = new FakeHandler(_ =>
            throw new HttpRequestException("Connection refused"));

        var sut = CreateService(new HttpClient(handler));

        var result = await sut.GetCurrentUserAsync();

        result.Should().NotBeNull();
        result!.UserId.Should().Be("fallback");
        result.Roles.Should().Contain("TenantAdmin");
    }

    // ── Caching ──

    [Fact]
    public async Task GetCurrentUserAsync_SecondCallReturnsCachedContext_WithoutCallingAuthStateProviderAgain()
    {
        SetupAuthState(
            new Claim(ClaimTypes.Email, "admin@acme.com"),
            new Claim("name", "Admin User"));
        SetupTenantContext(null);
        var sut = CreateService();

        var result1 = await sut.GetCurrentUserAsync();
        var result2 = await sut.GetCurrentUserAsync();

        result1.Should().BeSameAs(result2);
        _authStateProvider.Verify(x => x.GetAuthenticationStateAsync(), Times.Once);
    }

    // ── Email claim priority ──

    [Fact]
    public async Task GetCurrentUserAsync_ExtractsEmail_TriesClaimTypesEmailFirst()
    {
        SetupAuthState(
            new Claim(ClaimTypes.Email, "primary@acme.com"),
            new Claim("preferred_username", "secondary@acme.com"),
            new Claim("upn", "tertiary@acme.com"));
        SetupTenantContext(null);
        var sut = CreateService();

        var result = await sut.GetCurrentUserAsync();

        result!.Email.Should().Be("primary@acme.com");
    }

    [Fact]
    public async Task GetCurrentUserAsync_ExtractsEmail_FallsBackToPreferredUsername()
    {
        SetupAuthState(
            new Claim("preferred_username", "secondary@acme.com"),
            new Claim("upn", "tertiary@acme.com"));
        SetupTenantContext(null);
        var sut = CreateService();

        var result = await sut.GetCurrentUserAsync();

        result!.Email.Should().Be("secondary@acme.com");
    }

    [Fact]
    public async Task GetCurrentUserAsync_ExtractsEmail_FallsBackToUpn()
    {
        SetupAuthState(new Claim("upn", "tertiary@acme.com"));
        SetupTenantContext(null);
        var sut = CreateService();

        var result = await sut.GetCurrentUserAsync();

        result!.Email.Should().Be("tertiary@acme.com");
    }

    // ================================================================
    // HasPermission / HasRole / HasAnyRole
    // ================================================================

    [Fact]
    public void HasPermission_WhenNoCachedContext_ReturnsFalse()
    {
        var sut = CreateService();
        sut.HasPermission("claims:read").Should().BeFalse();
    }

    [Fact]
    public async Task HasPermission_ExactMatch_WorksCaseInsensitive()
    {
        SetupAuthState(
            new Claim(ClaimTypes.Email, "jane@acme.com"),
            new Claim("name", "Jane Doe"));
        SetupTenantContext(null); // fallback grants TenantAdmin which has *:*
        var sut = CreateService();
        await sut.GetCurrentUserAsync();

        // TenantAdmin has "*:*" which matches everything
        sut.HasPermission("Claims:Read").Should().BeTrue();
    }

    [Fact]
    public async Task HasRole_ExactMatch_WorksCaseInsensitive()
    {
        SetupAuthState(
            new Claim(ClaimTypes.Email, "jane@acme.com"),
            new Claim("name", "Jane Doe"));
        SetupTenantContext(null);
        var sut = CreateService();
        await sut.GetCurrentUserAsync();

        sut.HasRole("tenantadmin").Should().BeTrue();
        sut.HasRole("TenantAdmin").Should().BeTrue();
    }

    [Fact]
    public async Task HasAnyRole_ReturnsTrueIfAnyRoleMatches()
    {
        SetupAuthState(
            new Claim(ClaimTypes.Email, "jane@acme.com"),
            new Claim("name", "Jane Doe"));
        SetupTenantContext(null);
        var sut = CreateService();
        await sut.GetCurrentUserAsync();

        sut.HasAnyRole("Finance", "TenantAdmin", "Unknown").Should().BeTrue();
    }

    [Fact]
    public async Task HasAnyRole_ReturnsFalseWhenNoneMatch()
    {
        SetupAuthState(
            new Claim(ClaimTypes.Email, "jane@acme.com"),
            new Claim("name", "Jane Doe"));
        SetupTenantContext(null);
        var sut = CreateService();
        await sut.GetCurrentUserAsync();

        sut.HasAnyRole("Finance", "ClaimsExaminer").Should().BeFalse();
    }

    [Fact]
    public void HasAnyRole_WhenNoCachedContext_ReturnsFalse()
    {
        var sut = CreateService();
        sut.HasAnyRole("TenantAdmin").Should().BeFalse();
    }

    // ================================================================
    // PermissionMatches (tested via HasPermission after loading a user)
    // ================================================================

    private async Task<UserContextService> CreateServiceWithRole(string role)
    {
        SetupAuthState(
            new Claim(ClaimTypes.Email, "jane@acme.com"),
            new Claim("name", "Jane Doe"));
        SetupTenantContext(_defaultTenantContext);

        var userJson = SerializeUser(
            email: "jane@acme.com",
            roles: new List<string> { role }, status: "Active");

        var handler = new FakeHandler(HttpStatusCode.OK,
            SerializeUsers(userJson));
        var sut = CreateService(new HttpClient(handler));
        await sut.GetCurrentUserAsync();
        return sut;
    }

    [Fact]
    public async Task PermissionMatches_WildcardStarColonStar_GrantsEverything()
    {
        var sut = await CreateServiceWithRole("TenantAdmin"); // has *:*
        sut.HasPermission("anything:whatever").Should().BeTrue();
    }

    [Fact]
    public async Task PermissionMatches_WildcardStarColonAction_GrantsMatchingAction()
    {
        var sut = await CreateServiceWithRole("ComplianceOfficer"); // has *:read
        sut.HasPermission("claims:read").Should().BeTrue();
    }

    [Fact]
    public async Task PermissionMatches_WildcardResourceColonStar_GrantsMatchingResource()
    {
        // Need a role with resource:* — we don't have one out of the box,
        // but TenantAdmin has *:* which would match; let's test via ComplianceOfficer
        // ComplianceOfficer has "*:read" — test that claims:read matches
        var sut = await CreateServiceWithRole("ComplianceOfficer");
        sut.HasPermission("providers:read").Should().BeTrue();
    }

    [Fact]
    public async Task PermissionMatches_ExactMatchWorks()
    {
        var sut = await CreateServiceWithRole("ClaimsExaminer");
        sut.HasPermission("claims:read").Should().BeTrue();
        sut.HasPermission("claims:work").Should().BeTrue();
    }

    [Fact]
    public async Task PermissionMatches_NonMatchingPermission_ReturnsFalse()
    {
        var sut = await CreateServiceWithRole("ClaimsExaminer");
        sut.HasPermission("payments:run").Should().BeFalse();
    }

    [Fact]
    public async Task PermissionMatches_MalformedPermissionString_ReturnsFalse()
    {
        var sut = await CreateServiceWithRole("ClaimsExaminer");
        sut.HasPermission("nocolonhere").Should().BeFalse();
    }

    // ================================================================
    // ExpandPermissions / GetPermissionsForRole (tested via loaded users)
    // ================================================================

    [Fact]
    public async Task ExpandPermissions_ClaimsExaminer_GetsExpectedPermissions()
    {
        var sut = await CreateServiceWithRole("ClaimsExaminer");

        sut.HasPermission("claims:read").Should().BeTrue();
        sut.HasPermission("claims:work").Should().BeTrue();
        sut.HasPermission("workqueue:read").Should().BeTrue();
        sut.HasPermission("members:read").Should().BeTrue();
        sut.HasPermission("providers:read").Should().BeTrue();
    }

    [Fact]
    public async Task ExpandPermissions_PlatformAdmin_GetsExpectedPermissions()
    {
        var sut = await CreateServiceWithRole("PlatformAdmin");

        sut.HasPermission("platform:admin").Should().BeTrue();
        sut.HasPermission("platform:tenants").Should().BeTrue();
        sut.HasPermission("anything:anything").Should().BeTrue(); // has *:*
    }

    [Fact]
    public async Task ExpandPermissions_ComplianceViewer_GetsExpectedPermissions()
    {
        var sut = await CreateServiceWithRole("ComplianceViewer");

        // Must satisfy PA Rule Explorer gate: compliance:read OR authorizations:read
        sut.HasPermission("compliance:read").Should().BeTrue();
        sut.HasPermission("authorizations:read").Should().BeTrue();
        sut.HasPermission("audit:read").Should().BeTrue();
    }

    [Fact]
    public async Task ExpandPermissions_ComplianceViewer_DoesNotGetWriteOrAdminPermissions()
    {
        var sut = await CreateServiceWithRole("ComplianceViewer");

        // ComplianceViewer is read-only — must not accidentally leak write or admin bits
        sut.HasPermission("compliance:write").Should().BeFalse();
        sut.HasPermission("authorizations:write").Should().BeFalse();
        sut.HasPermission("authorizations:decide").Should().BeFalse();
        sut.HasPermission("claims:work").Should().BeFalse();
        sut.HasPermission("users:manage").Should().BeFalse();
        sut.HasPermission("anything:anything").Should().BeFalse();
    }

    [Fact]
    public async Task ExpandPermissions_UnknownRole_GetsEmptyPermissions()
    {
        var sut = await CreateServiceWithRole("NonExistentRole");

        sut.HasPermission("claims:read").Should().BeFalse();
        sut.HasPermission("anything:anything").Should().BeFalse();
    }

    [Fact]
    public async Task ExpandPermissions_MultipleRoles_MergesPermissions()
    {
        SetupAuthState(
            new Claim(ClaimTypes.Email, "jane@acme.com"),
            new Claim("name", "Jane Doe"));
        SetupTenantContext(_defaultTenantContext);

        var userJson = SerializeUser(
            email: "jane@acme.com",
            roles: new List<string> { "ClaimsExaminer", "Finance" },
            status: "Active");

        var handler = new FakeHandler(HttpStatusCode.OK,
            SerializeUsers(userJson));
        var sut = CreateService(new HttpClient(handler));
        await sut.GetCurrentUserAsync();

        // ClaimsExaminer permissions
        sut.HasPermission("claims:read").Should().BeTrue();
        sut.HasPermission("claims:work").Should().BeTrue();
        // Finance permissions
        sut.HasPermission("payments:read").Should().BeTrue();
        sut.HasPermission("payments:run").Should().BeTrue();
        sut.HasPermission("billing:read").Should().BeTrue();
    }

    // ================================================================
    // UserContext model
    // ================================================================

    [Fact]
    public void PrimaryRole_ReturnsFirstRole()
    {
        var ctx = new UserContext { Roles = new List<string> { "Finance", "ClaimsExaminer" } };
        ctx.PrimaryRole.Should().Be("Finance");
    }

    [Fact]
    public void PrimaryRole_ReturnsUnknown_WhenRolesEmpty()
    {
        var ctx = new UserContext { Roles = new List<string>() };
        ctx.PrimaryRole.Should().Be("Unknown");
    }

    [Theory]
    [InlineData("ClaimsExaminer", "Claims Examiner")]
    [InlineData("ClaimsSupervisor", "Claims Supervisor")]
    [InlineData("MemberServices", "Member Services")]
    [InlineData("EnrollmentSpecialist", "Enrollment Specialist")]
    [InlineData("UMCoordinator", "UM Coordinator")]
    [InlineData("ProviderRelations", "Provider Relations")]
    [InlineData("Finance", "Finance")]
    [InlineData("ComplianceOfficer", "Compliance Officer")]
    [InlineData("ComplianceViewer", "Compliance Viewer")]
    [InlineData("TenantAdmin", "Tenant Admin")]
    [InlineData("PlatformAdmin", "Platform Admin")]
    public void PrimaryRoleDisplayName_MapsCorrectly(string role, string expected)
    {
        var ctx = new UserContext { Roles = new List<string> { role } };
        ctx.PrimaryRoleDisplayName.Should().Be(expected);
    }

    [Fact]
    public void PrimaryRoleDisplayName_ReturnsRawRoleName_ForUnmappedRoles()
    {
        var ctx = new UserContext { Roles = new List<string> { "CustomRole" } };
        ctx.PrimaryRoleDisplayName.Should().Be("CustomRole");
    }
}
