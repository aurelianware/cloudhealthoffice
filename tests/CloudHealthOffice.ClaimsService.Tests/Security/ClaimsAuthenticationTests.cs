using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services;
using ClaimsService.Services.Migrations;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Security;

/// <summary>
/// The real claims-service pipeline: CHO token authentication, tenant from the
/// token only, default permissions claims:read / claims:work, and the stricter
/// permissions on void, adjust, override approval, work-queue assignment and
/// the cross-tenant Cosmos migration.
/// </summary>
public class ClaimsAuthenticationTests : IClassFixture<ClaimsApiFactory>
{
    private const string Tenant = "tenant-a";
    private readonly ClaimsApiFactory _factory;

    public ClaimsAuthenticationTests(ClaimsApiFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Examiner(string subject = "examiner-1")
        => _factory.CreateTenantClient(Tenant, subject, ChoRolePermissions.ClaimsExaminer);

    private HttpClient Supervisor(string subject = "supervisor-1")
        => _factory.CreateTenantClient(Tenant, subject, ChoRolePermissions.ClaimsSupervisor);

    private HttpClient ClientWithToken(string token)
    {
        var client = _factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private Claim StubClaim(ClaimStatus status = ClaimStatus.Pended)
    {
        var claim = new Claim
        {
            Id = "claim-" + Guid.NewGuid().ToString("N"),
            TenantId = Tenant,
            ClaimNumber = "CLM-AUTH",
            MemberId = "MEM-1",
            BillingProviderNPI = "1234567893",
            Status = status,
        };
        _factory.ClaimRepository.GetByIdAsync(claim.Id).Returns(claim);
        _factory.ClaimRepository.UpdateAsync(Arg.Is<Claim>(c => c.Id == claim.Id)).Returns(c => c.Arg<Claim>());
        return claim;
    }

    // ── Authentication and tenant ──────────────────────────────────────

    [Fact]
    public async Task NoToken_Returns401()
    {
        var response = await _factory.CreateClient().GetAsync("/api/claims/recent");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TenantHeaderWithoutToken_Returns401_NotServedForThatTenant()
    {
        var claim = StubClaim();
        _factory.ClaimRepository.ClearReceivedCalls();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);

        var response = await client.GetAsync($"/api/claims/{claim.Id}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await _factory.ClaimRepository.DidNotReceive().GetByIdAsync(claim.Id);
    }

    [Fact]
    public async Task TenantHeaderDisagreeingWithToken_Returns403()
    {
        var claim = StubClaim();
        var client = ClientWithToken(ChoDevelopmentAuth.UserToken(Tenant, ChoRolePermissions.ClaimsExaminer));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-b");

        var response = await client.GetAsync($"/api/claims/{claim.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TokenTenantIsUsed_WithoutAnyHeader()
    {
        var client = ClientWithToken(ChoDevelopmentAuth.UserToken(Tenant, ChoRolePermissions.ClaimsExaminer));
        _factory.ImportTransactionRepository.ListRecentAsync(Tenant, 100)
            .Returns(new List<ClaimImportTransaction>());

        var response = await client.GetAsync("/api/v1/claims/import-transactions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _factory.ImportTransactionRepository.Received().ListRecentAsync(Tenant, 100);
    }

    [Fact]
    public async Task RoleWithoutClaimsPermission_Returns403()
    {
        var claim = StubClaim();
        var client = _factory.CreateTenantClient(Tenant, "enroller", ChoRolePermissions.EnrollmentSpecialist);

        var response = await client.GetAsync($"/api/claims/{claim.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ReadOnlyRole_CanRead_ButNotSubmit()
    {
        var claim = StubClaim();
        var client = _factory.CreateTenantClient(Tenant, "member-services", ChoRolePermissions.MemberServices);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/claims/{claim.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/v1/claims", new AdapterClaim { MemberId = "MEM-1" })).StatusCode);
    }

    // ── Stricter permissions: ClaimsExaminer 403, ClaimsSupervisor succeeds ──

    [Fact]
    public async Task OverrideApproval_RequiresOverrideApprove()
    {
        var claim = StubClaim(ClaimStatus.Pended);
        var body = new { overrideReason = "documentation supports payment", pendFingerprint = claim.PendDetails?.Fingerprint };

        var asExaminer = await Examiner().PostAsJsonAsync($"/api/claims/work-queue/{claim.Id}/override", body);
        Assert.Equal(HttpStatusCode.Forbidden, asExaminer.StatusCode);
        Assert.Equal(ClaimStatus.Pended, claim.Status);

        var asSupervisor = await Supervisor().PostAsJsonAsync($"/api/claims/work-queue/{claim.Id}/override", body);
        Assert.Equal(HttpStatusCode.OK, asSupervisor.StatusCode);
        Assert.Equal(ClaimStatus.Approved, claim.Status);
        Assert.Equal("supervisor-1", claim.LastUpdatedBy);
    }

    [Fact]
    public async Task WorkQueueAssign_RequiresWorkqueueAssign()
    {
        var claim = StubClaim(ClaimStatus.Pended);
        var body = new { assignTo = "examiner-2" };

        Assert.Equal(HttpStatusCode.Forbidden,
            (await Examiner().PostAsJsonAsync($"/api/claims/work-queue/{claim.Id}/assign", body)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await Supervisor().PostAsJsonAsync($"/api/claims/work-queue/{claim.Id}/assign", body)).StatusCode);
    }

    [Fact]
    public async Task VoidEndpoint_RequiresClaimsVoid()
    {
        var claim = StubClaim(ClaimStatus.Paid);
        _factory.FinalizationService
            .VoidAsync(claim.Id, Arg.Any<ClaimVoidRequest>(), Tenant, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ClaimVoidResult.Voided(claim));
        var body = new { reason = "duplicate" };

        Assert.Equal(HttpStatusCode.Forbidden,
            (await Examiner().PostAsJsonAsync($"/api/claims/{claim.Id}/void", body)).StatusCode);
        await _factory.FinalizationService.DidNotReceive().VoidAsync(
            claim.Id, Arg.Any<ClaimVoidRequest>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());

        Assert.Equal(HttpStatusCode.OK,
            (await Supervisor().PostAsJsonAsync($"/api/claims/{claim.Id}/void", body)).StatusCode);
        await _factory.FinalizationService.Received(1).VoidAsync(
            claim.Id, Arg.Any<ClaimVoidRequest>(), Tenant, "supervisor-1", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteVoid_RequiresClaimsVoid()
    {
        var claim = StubClaim(ClaimStatus.Approved);

        Assert.Equal(HttpStatusCode.Forbidden, (await Examiner().DeleteAsync($"/api/claims/{claim.Id}")).StatusCode);
        Assert.Equal(ClaimStatus.Approved, claim.Status);

        Assert.Equal(HttpStatusCode.NoContent, (await Supervisor().DeleteAsync($"/api/claims/{claim.Id}")).StatusCode);
        Assert.Equal(ClaimStatus.Voided, claim.Status);
        Assert.Equal("supervisor-1", claim.LastUpdatedBy);
    }

    [Fact]
    public async Task GenericStatusEndpoint_CannotVoid_WithoutClaimsVoid()
    {
        var claim = StubClaim(ClaimStatus.Approved);
        _factory.ClaimRepository
            .TryTransitionStatusAsync(Tenant, claim.Id, Arg.Any<ClaimStatus>(), Arg.Any<CancellationToken>())
            .Returns(ci => new StatusWriteResult(StatusWriteOutcome.Applied, ci.ArgAt<ClaimStatus>(2)));

        var asExaminer = await Examiner().PutAsJsonAsync($"/api/claims/{claim.Id}/status", new { status = (int)ClaimStatus.Voided });
        Assert.Equal(HttpStatusCode.Forbidden, asExaminer.StatusCode);
        await _factory.ClaimRepository.DidNotReceive().TryTransitionStatusAsync(
            Tenant, claim.Id, ClaimStatus.Voided, Arg.Any<CancellationToken>());

        var asSupervisor = await Supervisor().PutAsJsonAsync($"/api/claims/{claim.Id}/status", new { status = (int)ClaimStatus.Voided });
        Assert.Equal(HttpStatusCode.OK, asSupervisor.StatusCode);
        Assert.Equal(ClaimStatus.Voided, claim.Status);
    }

    [Fact]
    public async Task GenericStatusEndpoint_NonVoidTransition_StillClaimsWork()
    {
        var claim = StubClaim(ClaimStatus.Submitted);
        _factory.ClaimRepository
            .TryTransitionStatusAsync(Tenant, claim.Id, Arg.Any<ClaimStatus>(), Arg.Any<CancellationToken>())
            .Returns(ci => new StatusWriteResult(StatusWriteOutcome.Applied, ci.ArgAt<ClaimStatus>(2)));

        var response = await Examiner().PutAsJsonAsync($"/api/claims/{claim.Id}/status", new { status = (int)ClaimStatus.Received });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Adjustment_RequiresClaimsAdjust()
    {
        var predecessorId = "pred-" + Guid.NewGuid().ToString("N");
        _factory.AdjustmentService
            .CreateAdjustmentAsync(predecessorId, Arg.Any<ClaimAdjustmentRequest>(), Arg.Any<string>(), Tenant,
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ClaimAdjustmentResult.Created(new ClaimAdjustment { Id = "adj-1" }, new AdapterClaim()));
        var body = new { adjustmentReason = "corrected units", correctedClaim = new { memberId = "MEM-1" } };

        HttpRequestMessage Request() => new(HttpMethod.Post, $"/api/v1/claims/{predecessorId}/adjustments")
        {
            Content = JsonContent.Create(body),
            Headers = { { "Idempotency-Key", "key-1" } },
        };

        Assert.Equal(HttpStatusCode.Forbidden, (await Examiner().SendAsync(Request())).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Supervisor().SendAsync(Request())).StatusCode);
        await _factory.AdjustmentService.Received(1).CreateAdjustmentAsync(
            predecessorId, Arg.Any<ClaimAdjustmentRequest>(), "key-1", Tenant, "supervisor-1",
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CosmosMigration_RequiresPlatformAdmin()
    {
        // The migration copies every tenant's claims; a tenant-scoped claims
        // permission (even a supervisor's) is not enough.
        using var factory = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddSingleton(Substitute.For<IClaimMigrationService>())));

        var supervisor = factory.CreateDefaultClient(
            new ChoDevelopmentTokenHandler("supervisor-1", ChoRolePermissions.ClaimsSupervisor));
        supervisor.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await supervisor.PostAsJsonAsync("/api/v1/admin/claims/cosmos-migration/run", new { dryRun = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await supervisor.GetAsync("/api/v1/admin/claims/cosmos-migration/status")).StatusCode);

        var platformAdmin = factory.CreateDefaultClient(
            new ChoDevelopmentTokenHandler("platform-admin", ChoRolePermissions.PlatformAdmin));
        platformAdmin.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        // Authorized; the feature flag is off in tests, so the action answers 503.
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await platformAdmin.GetAsync("/api/v1/admin/claims/cosmos-migration/status")).StatusCode);
    }

    // ── Service callers ────────────────────────────────────────────────

    [Fact]
    public async Task AiExamination_AcceptsClaimsExaminerServiceToken()
    {
        var claim = StubClaim(ClaimStatus.Pended);
        var token = ChoDevelopmentAuth.ServiceTokenIssuer()
            .IssueServiceToken("claims-examiner-service", Tenant);

        var response = await ClientWithToken(token).PutAsJsonAsync(
            $"/api/claims/{claim.Id}/ai-examination",
            new AiExamination { RecommendedDisposition = "Approve", ConfidenceScore = 0.9 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Approve", claim.AiExamination?.RecommendedDisposition);
    }

    [Fact]
    public async Task AiExamination_ReadOnlyUser_Returns403()
    {
        var claim = StubClaim(ClaimStatus.Pended);
        var client = _factory.CreateTenantClient(Tenant, "member-services", ChoRolePermissions.MemberServices);

        var response = await client.PutAsJsonAsync(
            $"/api/claims/{claim.Id}/ai-examination",
            new AiExamination { RecommendedDisposition = "Approve", ConfidenceScore = 0.9 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(claim.AiExamination);
    }

    // ── Actor and server-owned fields ──────────────────────────────────

    [Fact]
    public async Task V1Submit_ClientCannotSetLifecycleOrAuditFields()
    {
        AdapterClaim? submitted = null;
        string? submittedActor = null;
        _factory.SubmissionService
            .SubmitAsync(Arg.Is<AdapterClaim>(c => c.ClaimNumber == "CLM-FORGED"), Tenant,
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                submitted = ci.Arg<AdapterClaim>();
                submittedActor = ci.ArgAt<string>(2);
                return ClaimSubmissionResult.Ok(submitted);
            });

        var forged = new AdapterClaim
        {
            ClaimNumber = "CLM-FORGED",
            MemberId = "MEM-1",
            TenantId = "tenant-b",
            Status = ClaimStatus.Paid,
            VersionState = ClaimVersionState.Paid,
            ClaimVersionId = "someone-elses-chain",
            VersionNumber = 7,
            PredecessorVersionId = "prior-version",
            AdjudicationResult = new AdapterAdjudicationResult { PayerPayment = 10_000m },
            PendDetails = new PendDetails { PendCode = "NCCI" },
            AiExamination = new AiExamination { RecommendedDisposition = "Approve" },
            PaidDate = DateTime.UtcNow,
            CreatedBy = "someone-else",
            LastUpdatedBy = "someone-else",
        };

        var response = await Examiner("examiner-9").PostAsJsonAsync("/api/v1/claims", forged);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(submitted);
        Assert.Equal("examiner-9", submittedActor);
        Assert.Equal(ClaimStatus.Submitted, submitted!.Status);
        Assert.Equal(ClaimVersionState.Submitted, submitted.VersionState);
        Assert.Equal(string.Empty, submitted.ClaimVersionId);
        Assert.Equal(0, submitted.VersionNumber);
        Assert.Null(submitted.PredecessorVersionId);
        Assert.Null(submitted.AdjudicationResult);
        Assert.Null(submitted.PendDetails);
        Assert.Null(submitted.AiExamination);
        Assert.Null(submitted.PaidDate);
        Assert.Equal("examiner-9", submitted.CreatedBy);
        Assert.Equal("examiner-9", submitted.LastUpdatedBy);
    }

#pragma warning disable CS0618 // legacy POST /api/claims is obsolete but still served
    [Fact]
    public async Task LegacySubmit_ClientCannotSetLifecycleOrAuditFields()
    {
        AdapterClaim? submitted = null;
        _factory.SubmissionService
            .SubmitAsync(Arg.Is<AdapterClaim>(c => c.ClaimNumber == "CLM-LEGACY-FORGED"), Tenant,
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                submitted = ci.Arg<AdapterClaim>();
                return ClaimSubmissionResult.Ok(submitted);
            });

        var serviceDate = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var forged = new Claim
        {
            TenantId = "tenant-b",
            ClaimNumber = "CLM-LEGACY-FORGED",
            MemberId = "MEM-1",
            BillingProviderNPI = "1234567890",
            LineOfBusiness = LineOfBusiness.Commercial,
            ClaimType = ClaimType.Professional,
            PlaceOfServiceCode = "11",
            ServiceDateFrom = serviceDate,
            ServiceDateTo = serviceDate,
            ClaimLines = new List<ClaimLine>
            {
                new()
                {
                    LineNumber = 1,
                    ProcedureCode = "99213",
                    ChargeAmount = 150m,
                    Units = 1,
                    ServiceDateFrom = serviceDate,
                    ServiceDateTo = serviceDate,
                }
            },
            Status = ClaimStatus.Approved,
            AdjudicationResult = new AdjudicationResult { PayerPayment = 5_000m },
            CreatedBy = "someone-else",
        };

        var response = await Examiner("examiner-9").PostAsJsonAsync("/api/claims", forged);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(ClaimStatus.Submitted, submitted!.Status);
        Assert.Null(submitted.AdjudicationResult);
        Assert.Equal("examiner-9", submitted.CreatedBy);
    }
#pragma warning restore CS0618

    [Fact]
    public async Task Resolve_RecordsTokenActor_NotBodyExaminer()
    {
        var claim = StubClaim(ClaimStatus.Pended);

        var response = await Examiner("examiner-3").PostAsJsonAsync(
            $"/api/claims/work-queue/{claim.Id}/resolve",
            new { disposition = "Denied", reason = "not covered", examinerUserId = "spoofed" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("examiner-3", claim.LastUpdatedBy);
        await _factory.VersionEventPublisher.Received().PublishVersionResolvedAsync(
            Arg.Is<Claim>(c => c.Id == claim.Id), "Denied", "not covered", "examiner-3",
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
