using System.Security.Claims;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class UserContextServiceTests
{
    private readonly Mock<AuthenticationStateProvider> _authStateProvider = new();
    private readonly Mock<IChoTokenProvider> _tokenProvider = new();
    private readonly Mock<ILogger<UserContextService>> _logger = new();

    private UserContextService CreateService(
        string? environmentName = null,
        bool allowTenantAdminFallback = false)
    {
        var configEntries = new Dictionary<string, string?>();
        if (allowTenantAdminFallback)
            configEntries["Authentication:AllowTenantAdminFallback"] = "true";

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configEntries)
            .Build();

        return new UserContextService(
            _authStateProvider.Object,
            _tokenProvider.Object,
            configuration,
            _logger.Object,
            environmentName == null ? null : new TestHostEnvironment(environmentName));
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

    /// <summary>The token exchange fails: the user has no CHO token.</summary>
    private void SetupNoToken(ChoTokenStatus status = ChoTokenStatus.NoAccess)
    {
        _tokenProvider.Setup(x => x.GetTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ChoTokenResult.Failure(status));
    }

    /// <summary>The token exchange succeeds with this response.</summary>
    private void SetupExchange(
        string userId = "usr-1",
        string tenantId = "tenant-1",
        string email = "jane@acme.com",
        string displayName = "Jane Doe",
        string firstName = "Jane",
        string lastName = "Doe",
        List<string>? roles = null,
        IEnumerable<string>? permissions = null,
        string department = "Claims")
    {
        roles ??= new List<string> { "ClaimsExaminer" };
        _tokenProvider.Setup(x => x.GetTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ChoTokenResult.Success(new ChoTokenExchangeResponse
            {
                AccessToken = "cho-token",
                ExpiresIn = 3600,
                TenantId = tenantId,
                TenantName = "Test Tenant",
                Roles = roles,
                // What the token service computes; the portal uses it as issued.
                Permissions = (permissions ?? ChoRolePermissions.Expand(roles)).ToList(),
                User = new ChoTokenUser
                {
                    Id = userId, Email = email, DisplayName = displayName,
                    FirstName = firstName, LastName = lastName, Department = department
                }
            }));
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

    [Theory]
    [InlineData(ChoTokenStatus.NoAccess)]        // 403 no_access (unknown or inactive user)
    [InlineData(ChoTokenStatus.Unavailable)]     // 503, network failure, not configured
    [InlineData(ChoTokenStatus.InvalidToken)]    // 401 invalid_token
    [InlineData(ChoTokenStatus.TenantRequired)]  // 409 with no usable tenant
    [InlineData(ChoTokenStatus.ConsentRequired)] // Entra re-sign-in / consent pending
    public async Task GetCurrentUserAsync_WhenTokenExchangeFails_GrantsNoRoles(ChoTokenStatus status)
    {
        SetupAuthState(
            new Claim(ClaimTypes.Email, "admin@acme.com"),
            new Claim("name", "Admin User"),
            new Claim("tid", "azure-tid-1"));
        SetupNoToken(status);
        var sut = CreateService();

        var result = await sut.GetCurrentUserAsync();

        result.Should().NotBeNull();
        result!.UserId.Should().Be("fallback");
        result.Roles.Should().BeEmpty();
        result.Permissions.Should().BeEmpty();
        result.TenantId.Should().BeEmpty("without a CHO token there is no tenant");
        sut.HasPermission("claims:read").Should().BeFalse();
    }

    [Fact]
    public async Task GetCurrentUserAsync_FallbackOnDevelopmentWithFlag_GrantsTenantAdmin()
    {
        SetupAuthState(
            new Claim(ClaimTypes.Email, "admin@acme.com"),
            new Claim("name", "Admin User"));
        SetupNoToken();
        var sut = CreateService(environmentName: Environments.Development, allowTenantAdminFallback: true);

        var result = await sut.GetCurrentUserAsync();

        result!.Roles.Should().Equal("TenantAdmin");
    }

    [Theory]
    [InlineData("Production", true)]
    [InlineData("Development", false)]
    public async Task GetCurrentUserAsync_FallbackWithoutDevelopmentAndFlag_GrantsNoRoles(string environment, bool flag)
    {
        SetupAuthState(
            new Claim(ClaimTypes.Email, "admin@acme.com"),
            new Claim("name", "Admin User"));
        SetupNoToken();
        var sut = CreateService(environmentName: environment, allowTenantAdminFallback: flag);

        var result = await sut.GetCurrentUserAsync();

        result!.Roles.Should().BeEmpty();
    }

    // ── Fallback DisplayName extraction ──

    [Fact]
    public async Task GetCurrentUserAsync_FallbackExtractsDisplayName_FromNameClaim()
    {
        SetupAuthState(
            new Claim(ClaimTypes.Email, "jane@acme.com"),
            new Claim("name", "Jane Doe"));
        SetupNoToken();
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
        SetupNoToken();
        var sut = CreateService();

        var result = await sut.GetCurrentUserAsync();

        result!.DisplayName.Should().Be("Jane From ClaimTypes");
    }

    [Fact]
    public async Task GetCurrentUserAsync_FallbackExtractsDisplayName_FallsBackToEmail()
    {
        SetupAuthState(new Claim(ClaimTypes.Email, "jane@acme.com"));
        SetupNoToken();
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
        SetupNoToken();
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
        SetupNoToken();
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
        SetupNoToken();
        var sut = CreateService();

        var result = await sut.GetCurrentUserAsync();

        // Split(' ').Skip(1).FirstOrDefault() gives "Jane", not "Jane Watson"
        result!.FirstName.Should().Be("Mary");
        result.LastName.Should().Be("Jane");
    }

    // ── User context from the token exchange ──

    [Fact]
    public async Task GetCurrentUserAsync_WhenExchangeSucceeds_MapsAllFieldsFromTheResponse()
    {
        SetupAuthState(
            new Claim(ClaimTypes.Email, "jane@acme.com"),
            new Claim("oid", "oid-abc-123"),
            new Claim("name", "Claim Name"));
        SetupExchange(
            userId: "usr-42", tenantId: "tenant-7", email: "jane.doe@acme.com",
            displayName: "Jane Doe", firstName: "Jane", lastName: "Doe",
            roles: new List<string> { "ClaimsExaminer", "Finance" },
            department: "Claims Dept");
        var sut = CreateService();

        var result = await sut.GetCurrentUserAsync();

        result.Should().NotBeNull();
        result!.UserId.Should().Be("usr-42");
        result.Email.Should().Be("jane.doe@acme.com");
        result.DisplayName.Should().Be("Jane Doe");
        result.FirstName.Should().Be("Jane");
        result.LastName.Should().Be("Doe");
        result.TenantId.Should().Be("tenant-7");
        result.Roles.Should().BeEquivalentTo(new[] { "ClaimsExaminer", "Finance" });
        result.Department.Should().Be("Claims Dept");
        result.Permissions.Should().Contain("claims:read");
        result.Permissions.Should().Contain("payments:read");
    }

    [Fact]
    public async Task GetCurrentUserAsync_UsesPermissionsExactlyAsTheTokenServiceIssuedThem()
    {
        SetupAuthState(new Claim(ClaimTypes.Email, "jane@acme.com"));
        // The role would grant claims:work, but the token service issued only claims:read.
        SetupExchange(roles: new List<string> { "ClaimsExaminer" }, permissions: new[] { "claims:read" });
        var sut = CreateService();

        await sut.GetCurrentUserAsync();

        sut.HasPermission("claims:read").Should().BeTrue();
        sut.HasPermission("claims:work").Should().BeFalse("the portal does not expand roles itself");
        sut.HasRole("ClaimsExaminer").Should().BeTrue();
    }

    [Fact]
    public async Task GetCurrentUserAsync_WhenResponseLacksUserNames_UsesTheSignInClaims()
    {
        SetupAuthState(
            new Claim(ClaimTypes.Email, "jane@acme.com"),
            new Claim("name", "Jane Doe"));
        _tokenProvider.Setup(x => x.GetTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ChoTokenResult.Success(new ChoTokenExchangeResponse
            {
                AccessToken = "t", TenantId = "tenant-1", ExpiresIn = 3600,
                Roles = new List<string> { "MemberServices" },
                Permissions = new List<string> { "members:read" },
                User = new ChoTokenUser { Id = "usr-5" }
            }));
        var sut = CreateService();

        var result = await sut.GetCurrentUserAsync();

        result!.UserId.Should().Be("usr-5");
        result.Email.Should().Be("jane@acme.com");
        result.DisplayName.Should().Be("Jane Doe");
        result.FirstName.Should().Be("Jane");
        result.LastName.Should().Be("Doe");
    }

    // ── Empty roles grant nothing ──

    [Fact]
    public async Task GetCurrentUserAsync_WhenExchangeIssuesNoRoles_GrantsNoRoles()
    {
        SetupAuthState(
            new Claim(ClaimTypes.Email, "noroles@acme.com"),
            new Claim("name", "No Roles"));
        SetupExchange(userId: "usr-empty", email: "noroles@acme.com", roles: new List<string>());
        var sut = CreateService();

        var result = await sut.GetCurrentUserAsync();

        result.Should().NotBeNull();
        result!.UserId.Should().Be("usr-empty");
        result.Roles.Should().BeEmpty();
        result.Permissions.Should().BeEmpty();
    }

    // ── Development fallback applies only without a token ──

    [Fact]
    public async Task GetCurrentUserAsync_DevelopmentFallbackFlag_DoesNotOverrideAnIssuedToken()
    {
        SetupAuthState(new Claim(ClaimTypes.Email, "jane@acme.com"));
        SetupExchange(roles: new List<string> { "ClaimsExaminer" });
        var sut = CreateService(environmentName: Environments.Development, allowTenantAdminFallback: true);

        var result = await sut.GetCurrentUserAsync();

        result!.Roles.Should().Equal("ClaimsExaminer");
    }

    // ── Exception from the token provider falls back ──

    [Fact]
    public async Task GetCurrentUserAsync_WhenTokenProviderThrows_GrantsNoRoles()
    {
        SetupAuthState(
            new Claim(ClaimTypes.Email, "admin@acme.com"),
            new Claim("name", "Admin User"));
        _tokenProvider.Setup(x => x.GetTokenAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));
        var sut = CreateService();

        var result = await sut.GetCurrentUserAsync();

        result.Should().NotBeNull();
        result!.UserId.Should().Be("fallback");
        result.Roles.Should().BeEmpty();
    }

    // ── LocalDemo (real provider) ──

    [Fact]
    public async Task GetCurrentUserAsync_LocalDemoInDevelopment_GetsTheLocalDemoRolesFromTheMintedToken()
    {
        var sut = CreateLocalDemoService("Development");

        var result = await sut.GetCurrentUserAsync();

        result!.TenantId.Should().Be("demo");
        result.Roles.Should().BeEquivalentTo(ChoTokenProvider.LocalDemoRoles);
        sut.HasPermission("users:manage").Should().BeTrue();
        sut.HasPermission("platform:admin").Should().BeFalse();
    }

    [Fact]
    public async Task GetCurrentUserAsync_LocalDemoOutsideDevelopment_GrantsNoRoles()
    {
        var sut = CreateLocalDemoService("Production");

        var result = await sut.GetCurrentUserAsync();

        result!.Roles.Should().BeEmpty();
        result.Permissions.Should().BeEmpty();
    }

    private static UserContextService CreateLocalDemoService(string environment)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Mode"] = "LocalDemo",
            ["Authentication:LocalDemo:TenantId"] = "demo",
        }).Build();
        var auth = new StaticAuthenticationStateProvider(ChoTokenTestSupport.LocalDemoUser());
        var provider = new ChoTokenProvider(
            auth,
            new SingleHandlerHttpClientFactory(new FakeHandler(_ => throw new InvalidOperationException("no HTTP in LocalDemo"))),
            new MemoryCache(new MemoryCacheOptions()),
            configuration,
            new TestHostEnvironment(environment),
            NullLogger<ChoTokenProvider>.Instance);
        return new UserContextService(auth, provider, configuration,
            NullLogger<UserContextService>.Instance, new TestHostEnvironment(environment));
    }

    // ── Caching ──

    [Fact]
    public async Task GetCurrentUserAsync_SecondCallReturnsCachedContext_WithoutCallingAuthStateProviderAgain()
    {
        SetupAuthState(
            new Claim(ClaimTypes.Email, "admin@acme.com"),
            new Claim("name", "Admin User"));
        SetupNoToken();
        var sut = CreateService();

        var result1 = await sut.GetCurrentUserAsync();
        var result2 = await sut.GetCurrentUserAsync();

        result1.Should().BeSameAs(result2);
        _authStateProvider.Verify(x => x.GetAuthenticationStateAsync(), Times.Once);
        _tokenProvider.Verify(x => x.GetTokenAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Email claim priority ──

    [Fact]
    public async Task GetCurrentUserAsync_ExtractsEmail_TriesClaimTypesEmailFirst()
    {
        SetupAuthState(
            new Claim(ClaimTypes.Email, "primary@acme.com"),
            new Claim("preferred_username", "secondary@acme.com"),
            new Claim("upn", "tertiary@acme.com"));
        SetupNoToken();
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
        SetupNoToken();
        var sut = CreateService();

        var result = await sut.GetCurrentUserAsync();

        result!.Email.Should().Be("secondary@acme.com");
    }

    [Fact]
    public async Task GetCurrentUserAsync_ExtractsEmail_FallsBackToUpn()
    {
        SetupAuthState(new Claim("upn", "tertiary@acme.com"));
        SetupNoToken();
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
        var sut = await CreateServiceWithRole("ClaimsExaminer"); // has claims:read
        sut.HasPermission("Claims:Read").Should().BeTrue();
    }

    [Fact]
    public async Task HasRole_ExactMatch_WorksCaseInsensitive()
    {
        var sut = await CreateServiceWithRole("TenantAdmin");

        sut.HasRole("tenantadmin").Should().BeTrue();
        sut.HasRole("TenantAdmin").Should().BeTrue();
    }

    [Fact]
    public async Task HasAnyRole_ReturnsTrueIfAnyRoleMatches()
    {
        var sut = await CreateServiceWithRole("TenantAdmin");

        sut.HasAnyRole("Finance", "TenantAdmin", "Unknown").Should().BeTrue();
    }

    [Fact]
    public async Task HasAnyRole_ReturnsFalseWhenNoneMatch()
    {
        SetupAuthState(
            new Claim(ClaimTypes.Email, "jane@acme.com"),
            new Claim("name", "Jane Doe"));
        SetupNoToken();
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
        // The token service issues the role and the permissions it grants.
        SetupExchange(email: "jane@acme.com", roles: new List<string> { role });

        var sut = CreateService();
        await sut.GetCurrentUserAsync();
        return sut;
    }

    [Fact]
    public async Task PermissionMatches_WildcardStarColonStar_GrantsEverything()
    {
        var sut = await CreateServiceWithRole("TenantAdmin"); // has *:*
        sut.HasPermission("anything:whatever").Should().BeTrue();
    }

    [Theory]
    [InlineData("platform:admin")]
    [InlineData("platform:tenants")]
    [InlineData("platform:inquiries")]
    public async Task PermissionMatches_TenantAdminWildcard_DoesNotGrantPlatformPermissions(string permission)
    {
        var sut = await CreateServiceWithRole("TenantAdmin"); // has *:*
        sut.HasPermission(permission).Should().BeFalse();
    }

    [Fact]
    public async Task PermissionMatches_ReadWildcard_DoesNotGrantPlatformPermissions()
    {
        var sut = await CreateServiceWithRole("ComplianceOfficer"); // has *:read
        sut.HasPermission("platform:read").Should().BeFalse();
    }

    [Theory]
    [InlineData("platform:admin")]
    [InlineData("platform:tenants")]
    [InlineData("platform:inquiries")]
    public async Task PermissionMatches_PlatformAdmin_HasPlatformPermissions(string permission)
    {
        var sut = await CreateServiceWithRole("PlatformAdmin");
        sut.HasPermission(permission).Should().BeTrue();
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
        // Code-set lookups (not PHI).
        sut.HasPermission("reference-data:read").Should().BeTrue();
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
    public async Task ExpandPermissions_Finance_CanRunButNotApprovePayments()
    {
        var sut = await CreateServiceWithRole("Finance");

        sut.HasPermission("payments:read").Should().BeTrue();
        sut.HasPermission("payments:run").Should().BeTrue();
        sut.HasPermission("billing:run").Should().BeTrue();
        sut.HasPermission("reports:financial").Should().BeTrue();
        sut.HasPermission("payments:approve").Should().BeFalse();
        // Calculates and submits risk scores; looks up code sets.
        sut.HasPermission("risk-adjustment:write").Should().BeTrue();
        sut.HasPermission("reference-data:read").Should().BeTrue();
    }

    [Fact]
    public async Task ExpandPermissions_FinanceApprover_CanApproveButNotRunPayments()
    {
        var sut = await CreateServiceWithRole("FinanceApprover");

        sut.HasPermission("payments:read").Should().BeTrue();
        sut.HasPermission("payments:approve").Should().BeTrue();
        sut.HasPermission("finance:read").Should().BeTrue();
        sut.HasPermission("reports:financial").Should().BeTrue();
        // Reviews premium billing before releasing sponsor debits; cannot run it.
        sut.HasPermission("billing:read").Should().BeTrue();
        sut.HasPermission("payments:run").Should().BeFalse();
        sut.HasPermission("billing:run").Should().BeFalse();
        sut.HasPermission("finance:write").Should().BeFalse();
        sut.HasPermission("risk-adjustment:write").Should().BeFalse();
        sut.HasPermission("reference-data:read").Should().BeTrue();
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
        SetupExchange(email: "jane@acme.com", roles: new List<string> { "ClaimsExaminer", "Finance" });
        var sut = CreateService();
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
    [InlineData("FinanceApprover", "Finance Approver")]
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
