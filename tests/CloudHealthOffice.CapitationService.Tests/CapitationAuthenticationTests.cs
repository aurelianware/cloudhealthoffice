using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CapitationService.Models;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.CapitationService.Tests;

/// <summary>
/// The real capitation-service pipeline: CHO token authentication, tenant from
/// the token only, default permissions payments:read / payments:run, and
/// payments:approve for approving, voiding and releasing payments.
/// </summary>
public class CapitationAuthenticationTests : IClassFixture<CapitationApiFactory>
{
    private const string Tenant = "tenant-a";
    private readonly CapitationApiFactory _factory;

    public CapitationAuthenticationTests(CapitationApiFactory factory)
    {
        _factory = factory;
        _factory.RunService.GetRunsAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<LineOfBusiness?>())
            .Returns(new List<CapitationRun>());
        _factory.RunService.CreateRunAsync(Arg.Any<CreateCapitationRunRequest>(), Arg.Any<string?>())
            .Returns(new CapitationRun { Id = "run-1" });
        _factory.RunService.ExecuteRunAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns(new CapitationRun { Id = "run-1" });
        _factory.RunService.ApproveStatementAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(new CapitationStatement());
        _factory.RunService.VoidStatementAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(new CapitationStatement());
        _factory.RunService.HoldStatementAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(new CapitationStatement());
        _factory.DisbursementService.InitiateDisbursementAsync(Arg.Any<InitiateDisbursementRequest>())
            .Returns(new CapitationDisbursement { Id = "d-1" });
        _factory.DisbursementService.InitiateBatchDisbursementAsync(Arg.Any<InitiateBatchDisbursementRequest>())
            .Returns(new BatchDisbursementResult());
        _factory.DisbursementService.GenerateNachaCreditFileAsync(Arg.Any<string>()).Returns(new NachaCreditFileResult());
        _factory.DisbursementService.CancelDisbursementAsync(Arg.Any<string>()).Returns(new CapitationDisbursement());
    }

    /// <summary>payments:read and payments:run, but not payments:approve. No built-in role is exactly this.</summary>
    private HttpClient RunnerClient(string subject = "runner")
        => ClientWithToken(ChoDevelopmentAuth.UserTokenIssuer().IssueUserToken(
            subject, Tenant, roles: [], permissions: ["payments:read", "payments:run"]));

    private HttpClient ClientWithToken(string token)
    {
        var client = _factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static readonly object RunBody = new
    {
        runType = "Monthly",
        capitationPeriod = "2026-03-01T00:00:00Z",
        criteria = new { lineOfBusiness = "Commercial" },
        createdBy = "someone-else"
    };

    // ── Authentication and tenant ──────────────────────────────────────

    [Fact]
    public async Task NoToken_Returns401()
    {
        var response = await _factory.CreateClient().GetAsync("/api/v1/capitation/runs");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TenantHeaderWithoutToken_Returns401_NotServedForThatTenant()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);

        var response = await client.GetAsync("/api/v1/capitation/runs");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TenantHeaderDisagreeingWithToken_Returns403()
    {
        var client = ClientWithToken(ChoDevelopmentAuth.UserToken(Tenant, ChoRolePermissions.Finance));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-b");

        var response = await client.GetAsync("/api/v1/capitation/runs");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RoleWithoutPaymentsPermission_Returns403()
    {
        var client = _factory.CreateTenantClient(Tenant, "member-services", ChoRolePermissions.MemberServices);

        var response = await client.GetAsync("/api/v1/capitation/runs");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ReadOnlyCaller_CannotCreateRun()
    {
        var client = ClientWithToken(ChoDevelopmentAuth.UserTokenIssuer().IssueUserToken(
            "reader", Tenant, roles: [], permissions: ["payments:read"]));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/capitation/runs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/capitation/runs", RunBody)).StatusCode);
    }

    [Fact]
    public async Task TenantComesFromToken()
    {
        string? seenTenant = null;
        var accessor = _factory.Services.GetRequiredService<IHttpContextAccessor>();
        _factory.StatementRepository.GetByRunIdAsync("run-tenant-check").Returns(_ =>
        {
            seenTenant = accessor.HttpContext?.Items["TenantId"] as string;
            return new List<CapitationStatement>();
        });

        var client = _factory.CreateTenantClient(Tenant, "finance-user", ChoRolePermissions.Finance);
        var response = await client.GetAsync("/api/v1/capitation/runs/run-tenant-check/statements");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Tenant, seenTenant);
    }

    // ── Actor from token ───────────────────────────────────────────────

    [Fact]
    public async Task CreateRun_RecordsTokenSubject_NotBodyCreatedBy()
    {
        var client = RunnerClient("runner-42");

        var response = await client.PostAsJsonAsync("/api/v1/capitation/runs", RunBody);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await _factory.RunService.Received().CreateRunAsync(Arg.Any<CreateCapitationRunRequest>(), "runner-42");
        await _factory.RunService.DidNotReceive().CreateRunAsync(Arg.Any<CreateCapitationRunRequest>(), "someone-else");
    }

    // ── payments:approve ───────────────────────────────────────────────

    [Fact]
    public async Task RunWithoutApprove_CanCreateExecuteAndHold()
    {
        var client = RunnerClient();

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/capitation/runs", RunBody)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/v1/capitation/runs/run-1/execute", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/v1/capitation/statements/s-1/hold", new { reason = "review" })).StatusCode);
    }

    public static TheoryData<string, string> ApproveOnlyEndpoints => new()
    {
        { "PUT", "/api/v1/capitation/statements/s-1/approve" },
        { "PUT", "/api/v1/capitation/statements/s-1/void" },
        { "POST", "/api/v1/capitation/disbursements" },
        { "POST", "/api/v1/capitation/disbursements/batch" },
        { "POST", "/api/v1/capitation/disbursements/nacha-file" },
        { "DELETE", "/api/v1/capitation/disbursements/d-1" },
    };

    private static HttpRequestMessage Request(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method != "DELETE")
        {
            request.Content = JsonContent.Create(new
            {
                reason = "test",
                statementId = "s-1",
                statementIds = new[] { "s-1" }
            });
        }
        return request;
    }

    [Theory]
    [MemberData(nameof(ApproveOnlyEndpoints))]
    public async Task RunWithoutApprove_CannotApproveVoidOrReleasePayments(string method, string path)
    {
        var response = await RunnerClient().SendAsync(Request(method, path));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(ApproveOnlyEndpoints))]
    public async Task FinanceApproverRole_CanApproveVoidAndReleasePayments(string method, string path)
    {
        var client = _factory.CreateTenantClient(Tenant, "finance-approver", ChoRolePermissions.FinanceApprover);

        var response = await client.SendAsync(Request(method, path));

        Assert.True(response.IsSuccessStatusCode, $"{method} {path} returned {(int)response.StatusCode}");
    }

    /// <summary>Finance prepares payments; approving and releasing moved to FinanceApprover.</summary>
    [Theory]
    [MemberData(nameof(ApproveOnlyEndpoints))]
    public async Task FinanceRole_CannotApproveVoidOrReleasePayments(string method, string path)
    {
        var client = _factory.CreateTenantClient(Tenant, "finance-user", ChoRolePermissions.Finance);

        var response = await client.SendAsync(Request(method, path));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task FinanceRole_CanStillCreateAndExecuteRuns()
    {
        var client = _factory.CreateTenantClient(Tenant, "finance-user", ChoRolePermissions.Finance);

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/capitation/runs", RunBody)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/v1/capitation/runs/run-1/execute", null)).StatusCode);
    }

    [Fact]
    public async Task ApproveStatement_RecordsTokenSubject()
    {
        var client = _factory.CreateTenantClient(Tenant, "approver-9", ChoRolePermissions.FinanceApprover);

        var response = await client.PutAsync("/api/v1/capitation/statements/s-approve/approve", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _factory.RunService.Received().ApproveStatementAsync("s-approve", "approver-9");
    }

    [Fact]
    public async Task InitiateDisbursement_RecordsTokenSubject_NotBodyInitiatedBy()
    {
        var client = _factory.CreateTenantClient(Tenant, "releaser-3", ChoRolePermissions.FinanceApprover);

        var response = await client.PostAsJsonAsync("/api/v1/capitation/disbursements",
            new { statementId = "s-release", initiatedBy = "someone-else" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await _factory.DisbursementService.Received().InitiateDisbursementAsync(
            Arg.Is<InitiateDisbursementRequest>(r => r.StatementId == "s-release" && r.InitiatedBy == "releaser-3"));
    }

    // ── External caller: Stripe ────────────────────────────────────────

    [Fact]
    public async Task StripeWebhook_IsReachableWithoutCHOToken_AndStillRequiresSignature()
    {
        var client = _factory.CreateClient();

        var unsigned = await client.PostAsync("/api/v1/capitation/disbursements/stripe-webhook",
            new StringContent("{}"));
        Assert.Equal(HttpStatusCode.BadRequest, unsigned.StatusCode);

        var signed = new HttpRequestMessage(HttpMethod.Post, "/api/v1/capitation/disbursements/stripe-webhook")
        {
            Content = new StringContent("{}")
        };
        signed.Headers.Add("Stripe-Signature", "t=1,v1=abc");
        var response = await client.SendAsync(signed);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
