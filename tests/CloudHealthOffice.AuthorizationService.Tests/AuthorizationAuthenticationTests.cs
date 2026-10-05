using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AuthorizationService.Models;
using AuthorizationService.Repositories;
using CloudHealthOffice.Infrastructure.Security;
using NSubstitute;
using Authorization = AuthorizationService.Models.Authorization;

namespace CloudHealthOffice.AuthorizationService.Tests;

/// <summary>
/// The real authorization-service pipeline: CHO tokens are required, the tenant
/// and the actor come from the token, and review decisions need
/// authorizations:decide on top of authorizations:write.
/// </summary>
public class AuthorizationAuthenticationTests : IClassFixture<AuthorizationApiFactory>
{
    private const string Tenant = "tenant-a";
    private const string OtherTenant = "tenant-b";
    private const string Reviewer = "um-reviewer-7";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly AuthorizationApiFactory _factory;
    private readonly IAuthorizationRepository _repo;

    public AuthorizationAuthenticationTests(AuthorizationApiFactory factory)
    {
        _factory = factory;
        _repo = factory.AuthorizationRepository;
        _repo.ClearReceivedCalls();
        _repo.CreateAsync(Arg.Any<Authorization>()).Returns(call => call.Arg<Authorization>());
        _repo.UpdateAsync(Arg.Any<Authorization>()).Returns(call => call.Arg<Authorization>());
    }

    private HttpClient ClientFor(string tenant, string subject, params string[] roles)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(subject, roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }

    private HttpClient ClientWithToken(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private string SeedPending(string createdBy = "original-submitter")
    {
        var id = Guid.NewGuid().ToString();
        _repo.GetByIdAsync(id).Returns(new Authorization
        {
            Id = id,
            TenantId = Tenant,
            AuthorizationNumber = "AUTH-" + id[..8],
            MemberId = "MBR-1",
            Status = AuthorizationStatus.Submitted,
            CreatedBy = createdBy,
            LastUpdatedBy = createdBy,
            RequestedServices = { new RequestedService { ProcedureCode = "72148", RequestedUnits = 1 } },
        });
        return id;
    }

    // ── Authentication and tenant ─────────────────────────────────────

    [Fact]
    public async Task NoToken_Returns401()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/authorizations/search");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Validate_WithoutToken_Returns401_NotAnonymous()
    {
        // validate returns tenant data; it used to be [AllowAnonymous] in
        // Development and read the "default-tenant" fallback.
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/authorizations/AUTH-1/validate");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await _repo.DidNotReceive().GetByAuthorizationNumberAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task HeaderOnlyTenant_Returns401()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);

        var search = await client.GetAsync("/api/authorizations/search");
        var seed = await client.PostAsJsonAsync("/api/authorizations/dev-seed", new
        {
            authorizations = new[] { new { authorizationNumber = "AUTH-SEED-1", status = "Approved" } }
        }, Json);

        Assert.Equal(HttpStatusCode.Unauthorized, search.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, seed.StatusCode);
        await _repo.DidNotReceive().CreateAsync(Arg.Any<Authorization>());
        await _repo.DidNotReceive().UpdateAsync(Arg.Any<Authorization>());
    }

    [Fact]
    public async Task MismatchedTenantHeader_Returns403()
    {
        using var client = ClientWithToken(_factory.IssueToken(Tenant));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", OtherTenant);

        var response = await client.GetAsync("/api/authorizations/search");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RoleWithoutAuthorizationsPermissions_Returns403()
    {
        // ClaimsExaminer holds no authorizations:* permission.
        using var client = ClientFor(Tenant, "examiner-1", ChoRolePermissions.ClaimsExaminer);

        var response = await client.GetAsync("/api/authorizations/search");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ReadOnlyRole_CannotSubmit_Returns403()
    {
        // MemberServices holds authorizations:read only.
        using var client = ClientFor(Tenant, "csr-1", ChoRolePermissions.MemberServices);

        var read = await client.GetAsync("/api/authorizations/search");
        var write = await client.PostAsJsonAsync("/api/authorizations", SubmitBody(), Json);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        await _repo.DidNotReceive().CreateAsync(Arg.Any<Authorization>());
    }

    // ── authorizations:decide ─────────────────────────────────────────

    [Fact]
    public async Task WriteWithoutDecide_CanSubmit_ButCannotDecide()
    {
        var token = _factory.IssueTokenWithPermissions(
            Tenant, "intake-1", "authorizations:read", "authorizations:write");
        using var client = ClientWithToken(token);
        var id = SeedPending();

        var submit = await client.PostAsJsonAsync("/api/authorizations", SubmitBody(), Json);
        var response = await client.PostAsJsonAsync($"/api/authorizations/{id}/response",
            new { controlNumber = "CTL-1", reviewDecision = "A1", approvedUnits = 1 }, Json);
        var status = await client.PutAsJsonAsync($"/api/authorizations/{id}/status",
            new { status = "Approved", reviewDecision = "A1" }, Json);
        var seed = await client.PostAsJsonAsync("/api/authorizations/dev-seed", new
        {
            authorizations = new[] { new { authorizationNumber = "AUTH-SEED-2", status = "Approved" } }
        }, Json);

        Assert.Equal(HttpStatusCode.Created, submit.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, status.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, seed.StatusCode);
        await _repo.DidNotReceive().UpdateAsync(Arg.Any<Authorization>());
    }

    [Fact]
    public async Task UMCoordinator_CanDecide()
    {
        using var client = ClientFor(Tenant, Reviewer, ChoRolePermissions.UMCoordinator);
        var approve = SeedPending();
        var deny = SeedPending();

        var approved = await client.PostAsJsonAsync($"/api/authorizations/{approve}/response",
            new { controlNumber = "CTL-2", reviewDecision = "A1", approvedUnits = 1 }, Json);
        var denied = await client.PutAsJsonAsync($"/api/authorizations/{deny}/status",
            new { status = "Denied", reviewDecision = "A3" }, Json);

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal(HttpStatusCode.OK, denied.StatusCode);
    }

    // ── Actor and tenant from the token ───────────────────────────────

    [Fact]
    public async Task Submit_IgnoresBodyTenantActorAndReviewOutcome()
    {
        Authorization? stored = null;
        _repo.CreateAsync(Arg.Do<Authorization>(a => stored = a)).Returns(call => call.Arg<Authorization>());
        using var client = ClientFor(Tenant, "intake-2", ChoRolePermissions.UMCoordinator);

        var response = await client.PostAsJsonAsync("/api/authorizations", new
        {
            tenantId = OtherTenant,
            createdBy = "attacker",
            lastUpdatedBy = "attacker",
            authorizationNumber = "AUTH-FORGED",
            memberId = "MBR-1",
            patientFirstName = "Pat",
            patientLastName = "Member",
            patientDateOfBirth = "1980-01-01",
            requestingProviderNPI = "1234567890",
            serviceTypeCode = "42",
            requestedServiceDateFrom = "2026-11-01",
            status = "Approved",
            reviewDecision = "A1",
            approvedUnits = 999,
            expirationDate = "2099-01-01",
            reviewerName = "attacker",
            statusHistory = new[] { new { status = "Approved", reviewDecision = "A1", changedBy = "attacker" } },
            requestedServices = new[]
            {
                new { procedureCode = "72148", requestedUnits = 1, approvedUnits = 999, serviceStatus = "A1" }
            },
        }, Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(stored);
        Assert.Equal(Tenant, stored!.TenantId);
        Assert.Equal("intake-2", stored.CreatedBy);
        Assert.Equal("intake-2", stored.LastUpdatedBy);
        Assert.Equal(AuthorizationStatus.Submitted, stored.Status);
        Assert.Null(stored.ReviewDecision);
        Assert.Null(stored.ApprovedUnits);
        Assert.Null(stored.ExpirationDate);
        Assert.Null(stored.ReviewerName);
        Assert.Null(stored.RequestedServices[0].ApprovedUnits);
        Assert.Null(stored.RequestedServices[0].ServiceStatus);
        var history = Assert.Single(stored.StatusHistory);
        Assert.Equal(AuthorizationStatus.Submitted, history.Status);
        Assert.Equal("intake-2", history.ChangedBy);
    }

    [Fact]
    public async Task Decision_RecordsTokenActor_NotSubmitterOrBodyReviewer()
    {
        Authorization? stored = null;
        _repo.UpdateAsync(Arg.Do<Authorization>(a => stored = a)).Returns(call => call.Arg<Authorization>());
        using var client = ClientFor(Tenant, Reviewer, ChoRolePermissions.UMCoordinator);
        var id = SeedPending(createdBy: "original-submitter");

        var response = await client.PostAsJsonAsync($"/api/authorizations/{id}/response", new
        {
            controlNumber = "CTL-3",
            reviewDecision = "A3",
            denialReasonCode = "NOTMEDNEC",
            reviewerName = "Dr. Contact",
        }, Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(stored);
        Assert.Equal(Reviewer, stored!.LastUpdatedBy);
        Assert.Equal(Reviewer, stored.StatusHistory.Last().ChangedBy);
        Assert.Equal("original-submitter", stored.CreatedBy);
    }

    [Fact]
    public async Task StatusUpdate_RecordsTokenActor()
    {
        Authorization? stored = null;
        _repo.UpdateAsync(Arg.Do<Authorization>(a => stored = a)).Returns(call => call.Arg<Authorization>());
        using var client = ClientFor(Tenant, Reviewer, ChoRolePermissions.UMCoordinator);
        var id = SeedPending();

        var response = await client.PutAsJsonAsync($"/api/authorizations/{id}/status",
            new { status = "Approved", reviewDecision = "A1" }, Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Reviewer, stored!.LastUpdatedBy);
        Assert.Equal(Reviewer, stored.StatusHistory.Last().ChangedBy);
    }

    [Fact]
    public async Task Cancel_RecordsTokenActor()
    {
        Authorization? stored = null;
        _repo.UpdateAsync(Arg.Do<Authorization>(a => stored = a)).Returns(call => call.Arg<Authorization>());
        using var client = ClientFor(Tenant, "intake-3", ChoRolePermissions.UMCoordinator);
        var id = SeedPending();

        var response = await client.DeleteAsync($"/api/authorizations/{id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("intake-3", stored!.LastUpdatedBy);
    }

    [Fact]
    public async Task AtRisk_IgnoresTenantQuery_UsesTokenTenant()
    {
        _repo.GetOpenAuthorizationsAsync(Arg.Any<string?>()).Returns(new List<Authorization>());
        using var client = ClientFor(Tenant, Reviewer, ChoRolePermissions.UMCoordinator);

        var response = await client.GetAsync($"/api/authorizations/sla/at-risk?tenantId={OtherTenant}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _repo.Received(1).GetOpenAuthorizationsAsync(Tenant);
        await _repo.DidNotReceive().GetOpenAuthorizationsAsync(OtherTenant);
    }

    [Fact]
    public async Task DevSeed_StampsTokenTenantAndActor()
    {
        Authorization? stored = null;
        _repo.GetByAuthorizationNumberAsync("AUTH-SEED-3").Returns((Authorization?)null);
        _repo.CreateAsync(Arg.Do<Authorization>(a => stored = a)).Returns(call => call.Arg<Authorization>());
        using var client = ClientFor(Tenant, Reviewer, ChoRolePermissions.UMCoordinator);

        var response = await client.PostAsJsonAsync("/api/authorizations/dev-seed", new
        {
            authorizations = new[]
            {
                new
                {
                    authorizationNumber = "AUTH-SEED-3", tenantId = OtherTenant, createdBy = "attacker", status = "Approved",
                    memberId = "MBR-1", patientFirstName = "Pat", patientLastName = "Member",
                    requestingProviderNPI = "1234567890", serviceTypeCode = "42",
                }
            }
        }, Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Tenant, stored!.TenantId);
        Assert.Equal(Reviewer, stored.CreatedBy);
    }

    [Fact]
    public async Task BackendStatus_StaysAnonymous()
    {
        // Pre-existing [AllowAnonymous]: deployment mode only, no tenant data.
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/authorizations/backend-status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task BackendStatus_Anonymous_IsStatusOnly()
    {
        using var client = _factory.CreateClient();

        var body = await client.GetStringAsync("/api/authorizations/backend-status");

        using var json = System.Text.Json.JsonDocument.Parse(body);
        Assert.Equal(["status"], json.RootElement.EnumerateObject().Select(p => p.Name).ToArray());

        // A CHO caller still sees the mode and backend.
        using var authenticated = ClientWithToken(_factory.IssueToken(Tenant));
        using var detail = System.Text.Json.JsonDocument.Parse(
            await authenticated.GetStringAsync("/api/authorizations/backend-status"));
        Assert.True(detail.RootElement.TryGetProperty("operatingMode", out _));
        Assert.True(detail.RootElement.TryGetProperty("backend", out _));
    }

    private static object SubmitBody() => new
    {
        // No tenantId: the tenant comes from the token.
        authorizationNumber = "AUTH-NEW",
        memberId = "MBR-1",
        patientFirstName = "Pat",
        patientLastName = "Member",
        patientDateOfBirth = "1980-01-01",
        requestingProviderNPI = "1234567890",
        serviceTypeCode = "42",
        requestedServiceDateFrom = "2026-11-01",
        requestedServices = new[] { new { procedureCode = "72148", requestedUnits = 1 } },
    };
}
