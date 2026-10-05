using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using SponsorService.Models;

namespace SponsorService.Tests.Security;

/// <summary>
/// <c>PUT /api/v1/sponsors/{groupNumber}/status</c> through the real pipeline:
/// finance:write or enrollment:process; changes the status (with reason, actor
/// and time) and nothing else; only Active ↔ Suspended. The full
/// <c>PUT /sponsors/{group}</c> still needs enrollment:process.
/// </summary>
public class SponsorStatusEndpointTests : IClassFixture<SponsorPipelineAuthTests.Factory>
{
    private const string Tenant = "tenant-1";
    private const string User = "finance-user-3";
    private const string Group = "GRP-100";
    private const string SponsorId = "sponsor-id-1";
    private const string StatusPath = $"/api/v1/sponsors/{Group}/status";
    private const string FullPutPath = $"/api/v1/sponsors/{Group}";

    private readonly SponsorPipelineAuthTests.Factory _factory;
    private readonly List<SponsorStatusChange> _changes = new();

    public SponsorStatusEndpointTests(SponsorPipelineAuthTests.Factory factory)
    {
        _factory = factory;
        _factory.Repository.Reset();
        StoredStatus(SponsorStatus.Active);
        _factory.Repository.Setup(r => r.UpdateStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SponsorStatusChange>()))
            .Callback<string, string, SponsorStatusChange>((_, _, c) => _changes.Add(c))
            .ReturnsAsync(true);
        _factory.Repository.Setup(r => r.UpdateAsync(It.IsAny<Sponsor>())).ReturnsAsync((Sponsor s) => s);
    }

    private void StoredStatus(SponsorStatus status)
        => _factory.Repository.Setup(r => r.GetByGroupNumberAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((string tenant, string group) => new Sponsor
            {
                Id = SponsorId,
                TenantId = tenant,
                GroupNumber = group,
                EmployerName = "Acme Co",
                Status = status,
                BillingInfo = new BillingInfo { PremiumAmount = 1000m }
            });

    private HttpClient Client(params string[] roles)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(User, roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        return client;
    }

    private HttpClient PermissionClient(params string[] permissions)
    {
        var token = ChoDevelopmentAuth.UserTokenIssuer().IssueUserToken(User, Tenant, new[] { "Custom" }, permissions);
        var client = _factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static object Suspend => new { status = "Suspended", reason = "Premium delinquency: invoice INV-1" };

    private void NoStatusWrite()
        => _factory.Repository.Verify(r => r.UpdateStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SponsorStatusChange>()), Times.Never);

    private void NoFullWrite()
        => _factory.Repository.Verify(r => r.UpdateAsync(It.IsAny<Sponsor>()), Times.Never);

    // ── who may call it ─────────────────────────────────────────────────

    [Fact]
    public async Task Finance_SuspendsThroughTheStatusEndpoint()
    {
        var before = DateTime.UtcNow;

        var response = await Client(ChoRolePermissions.Finance).PutAsJsonAsync(StatusPath, Suspend);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var change = _changes.Should().ContainSingle().Subject;
        change.From.Should().Be(SponsorStatus.Active);
        change.To.Should().Be(SponsorStatus.Suspended);
        change.Reason.Should().Be("Premium delinquency: invoice INV-1");
        change.ChangedBy.Should().Be(User);
        change.ChangedByIsService.Should().BeFalse();
        change.ChangedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow);
        _factory.Repository.Verify(r => r.UpdateStatusAsync(Tenant, SponsorId, It.IsAny<SponsorStatusChange>()), Times.Once);
        NoFullWrite();

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("status").GetString().Should().Be("Suspended");
        body.RootElement.GetProperty("previousStatus").GetString().Should().Be("Active");
        body.RootElement.GetProperty("changed").GetBoolean().Should().BeTrue();
        body.RootElement.GetProperty("changedBy").GetString().Should().Be(User);
    }

    [Fact]
    public async Task Finance_CannotUseTheFullPut()
    {
        var response = await Client(ChoRolePermissions.Finance).PutAsJsonAsync(FullPutPath,
            new { status = "Suspended", employerName = "Renamed" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        NoFullWrite();
        NoStatusWrite();
    }

    [Fact]
    public async Task EnrollmentSpecialist_CanUseBoth()
    {
        var client = Client(ChoRolePermissions.EnrollmentSpecialist);

        (await client.PutAsJsonAsync(StatusPath, Suspend)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PutAsJsonAsync(FullPutPath, new { contactName = "Pat" })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PremiumBillingServiceToken_CanSuspend()
    {
        // A scheduled delinquency run has no user; premium-billing mints a
        // service token for the invoice's tenant.
        var token = ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken("premium-billing-service", Tenant);
        var client = _factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);

        var response = await client.PutAsJsonAsync(StatusPath, Suspend);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _changes.Should().ContainSingle(c => c.ChangedBy == "premium-billing-service" && c.ChangedByIsService);
    }

    [Theory]
    [InlineData(ChoRolePermissions.FinanceApprover)]   // finance:read, billing:read: no finance:write
    [InlineData(ChoRolePermissions.MemberServices)]
    [InlineData(ChoRolePermissions.ComplianceOfficer)] // *:read only
    [InlineData(ChoRolePermissions.ProviderRelations)]
    public async Task RolesWithoutFinanceWriteOrEnrollmentProcess_AreForbidden(string role)
    {
        var response = await Client(role).PutAsJsonAsync(StatusPath, Suspend);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task BillingRunAlone_IsForbidden()
    {
        var response = await PermissionClient("billing:read", "billing:run", "finance:read").PutAsJsonAsync(StatusPath, Suspend);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task NoToken_IsRejected()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);

        var response = await client.PutAsJsonAsync(StatusPath, Suspend);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.Repository.VerifyNoOtherCalls();
    }

    // ── transitions ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(SponsorStatus.Active, SponsorStatus.Suspended)]
    [InlineData(SponsorStatus.Suspended, SponsorStatus.Active)]
    public async Task AllowedTransitions_AreApplied(SponsorStatus from, SponsorStatus to)
    {
        StoredStatus(from);

        var response = await Client(ChoRolePermissions.Finance)
            .PutAsJsonAsync(StatusPath, new { status = to.ToString(), reason = "r" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _changes.Should().ContainSingle(c => c.From == from && c.To == to);
    }

    [Theory]
    [InlineData(SponsorStatus.Terminated, SponsorStatus.Active)]
    [InlineData(SponsorStatus.Terminated, SponsorStatus.Suspended)]
    [InlineData(SponsorStatus.Terminated, SponsorStatus.PendingActivation)]
    [InlineData(SponsorStatus.Active, SponsorStatus.Terminated)]
    [InlineData(SponsorStatus.Suspended, SponsorStatus.Terminated)]
    [InlineData(SponsorStatus.Active, SponsorStatus.PendingActivation)]
    [InlineData(SponsorStatus.Suspended, SponsorStatus.PendingActivation)]
    [InlineData(SponsorStatus.PendingActivation, SponsorStatus.Active)]
    [InlineData(SponsorStatus.PendingActivation, SponsorStatus.Suspended)]
    [InlineData(SponsorStatus.PendingActivation, SponsorStatus.Terminated)]
    public async Task DisallowedTransitions_AreRefused(SponsorStatus from, SponsorStatus to)
    {
        StoredStatus(from);

        // Enrollment's permission does not widen the transitions either.
        foreach (var role in new[] { ChoRolePermissions.Finance, ChoRolePermissions.EnrollmentSpecialist })
        {
            var response = await Client(role).PutAsJsonAsync(StatusPath, new { status = to.ToString(), reason = "r" });

            response.StatusCode.Should().Be(HttpStatusCode.Conflict, role);
            (await response.Content.ReadAsStringAsync()).Should().Contain(from.ToString());
        }
        NoStatusWrite();
        NoFullWrite();
    }

    [Fact]
    public async Task SettingTheCurrentStatus_SucceedsWithoutWriting()
    {
        // A retried suspension of an already-suspended sponsor is a success.
        StoredStatus(SponsorStatus.Suspended);

        var response = await Client(ChoRolePermissions.Finance).PutAsJsonAsync(StatusPath, Suspend);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("changed").GetBoolean().Should().BeFalse();
        NoStatusWrite();
        NoFullWrite();
    }

    [Fact]
    public async Task StatusChangedMeanwhile_IsAConflict()
    {
        _factory.Repository.Setup(r => r.UpdateStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SponsorStatusChange>()))
            .ReturnsAsync(false);

        var response = await Client(ChoRolePermissions.Finance).PutAsJsonAsync(StatusPath, Suspend);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        NoFullWrite();
    }

    [Fact]
    public async Task UnknownSponsor_Is404()
    {
        _factory.Repository.Setup(r => r.GetByGroupNumberAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((Sponsor?)null);

        var response = await Client(ChoRolePermissions.Finance).PutAsJsonAsync(StatusPath, Suspend);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        NoStatusWrite();
    }

    // ── the body ────────────────────────────────────────────────────────

    [Fact]
    public async Task ExtraFields_AreIgnored()
    {
        var response = await Client(ChoRolePermissions.Finance).PutAsJsonAsync(StatusPath, new
        {
            status = "Suspended",
            reason = "Premium delinquency",
            employerName = "Renamed Co",
            groupNumber = "GRP-OTHER",
            tenantId = "tenant-2",
            taxId = "99-9999999",
            terminationDate = "2026-01-01T00:00:00Z",
            billingInfo = new { premiumAmount = 1m },
            broker = new { name = "Someone" },
            lastUpdatedBy = "someone-else",
            statusChangedBy = "someone-else",
            changedBy = "someone-else",
            changedAt = "2000-01-01T00:00:00Z",
            statusHistory = new[] { new { from = "Terminated", to = "Active" } }
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        // Only the status change reaches the repository, and its actor and time
        // are the token's and the clock's.
        _factory.Repository.Verify(r => r.GetByGroupNumberAsync(Tenant, Group), Times.Once);
        _factory.Repository.Verify(r => r.UpdateStatusAsync(Tenant, SponsorId, It.IsAny<SponsorStatusChange>()), Times.Once);
        _factory.Repository.VerifyNoOtherCalls();
        var change = _changes.Single();
        change.To.Should().Be(SponsorStatus.Suspended);
        change.Reason.Should().Be("Premium delinquency");
        change.ChangedBy.Should().Be(User);
        change.ChangedAt.Should().BeAfter(new DateTime(2020, 1, 1));
    }

    [Theory]
    [InlineData("""{"status":"Suspended"}""")]                       // no reason
    [InlineData("""{"status":"Suspended","reason":"   "}""")]        // blank reason
    [InlineData("""{"reason":"x"}""")]                               // no status
    [InlineData("""{"status":"Bogus","reason":"x"}""")]              // unknown status
    [InlineData("""{"status":2,"reason":"x"}""")]                    // numeric status
    public async Task InvalidBody_Is400(string json)
    {
        var response = await Client(ChoRolePermissions.Finance).PutAsync(StatusPath,
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        NoStatusWrite();
        NoFullWrite();
    }

    [Fact]
    public async Task OverlongReason_Is400()
    {
        var response = await Client(ChoRolePermissions.Finance)
            .PutAsJsonAsync(StatusPath, new { status = "Suspended", reason = new string('x', 501) });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        NoStatusWrite();
    }
}
