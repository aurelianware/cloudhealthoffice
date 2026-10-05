using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BenefitPlanService.Models;
using BenefitPlanService.Models.Estimate;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.NcciEngine.Models;
using NSubstitute;

namespace CloudHealthOffice.AdjudicationController.Tests;

/// <summary>
/// Real-pipeline (WebApplicationFactory) checks of benefit-plan-service's
/// authentication, tenant resolution and permissions: CHO token required,
/// tenant from the token only, actor from the token only, plan writes need
/// settings:manage, pure calculations need claims:work or benefits:read, and
/// accumulator-writing adjudication needs claims:work.
/// </summary>
public class AuthPipelineTests : IClassFixture<AdjudicationControllerTests.Factory>
{
    private const string Tenant = "auth-tenant";
    private static readonly Guid PlanGuid = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private readonly AdjudicationControllerTests.Factory _factory;

    public AuthPipelineTests(AdjudicationControllerTests.Factory factory)
    {
        _factory = factory;

        _factory.EstimateService
            .EstimateAsync(Arg.Any<string>(), Arg.Any<PaymentEstimateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PaymentEstimateResponse { RequestId = "est-1" });
        _factory.NcciEngine
            .ScrubAsync(Arg.Any<NcciScrubRequest>(), Arg.Any<CancellationToken>())
            .Returns(new NcciScrubResult { ClaimId = "CLM-AUTH" });
        _factory.RateEngine
            .ResolveBatchAsync(Arg.Any<IReadOnlyList<PricingRequest>>(), Arg.Any<CancellationToken>())
            .Returns(new PricingResultSet());
        _factory.PlanService
            .CreateDraftAsync(Arg.Any<BenefitPlan>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => ci.Arg<BenefitPlan>());
        _factory.PlanService
            .CreatePlanAsync(Arg.Any<BenefitPlan>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => ci.Arg<BenefitPlan>());
        _factory.PlanService
            .UpdatePlanAsync(Arg.Any<BenefitPlan>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => ci.Arg<BenefitPlan>());
        _factory.PlanService
            .GetPlansAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>())
            .Returns(Array.Empty<BenefitPlan>());
    }

    // ── clients ──────────────────────────────────────────────────────────

    /// <summary>Token minted for the X-Tenant-ID header by the development handler.</summary>
    private HttpClient TokenClient(string subject = "dev-user", params string[] roles)
    {
        // Always pass a subject: a single string would bind to the roles overload.
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(subject, roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        return client;
    }

    /// <summary>A bearer token for <paramref name="tenant"/> and no tenant header at all.</summary>
    private HttpClient BearerOnlyClient(string tenant, string subject = "dev-user", params string[] roles)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            ChoDevelopmentAuth.UserTokenIssuer().IssueUserToken(
                subject, tenant, roles.Length > 0 ? roles : new[] { ChoRolePermissions.TenantAdmin }));
        return client;
    }

    // ── request bodies ───────────────────────────────────────────────────

    private static PaymentEstimateRequest EstimateBody() => new()
    {
        MemberId = "MBR-1",
        BenefitPlanId = PlanGuid,
        ProviderNpi = "1234567893",
        ServiceDate = new DateOnly(2026, 3, 1),
        Lines = [new PaymentEstimateLineRequest { LineNumber = 1, ProcedureCode = "99213", ChargeAmount = 150m }]
    };

    private static NcciScrubRequest NcciBody(string bodyTenant = "victim-tenant") => new()
    {
        TenantId = bodyTenant,
        ClaimId = "CLM-AUTH",
        ClaimType = "837P",
        ServiceLines =
        [
            new ClaimServiceLine { LineNumber = 1, ProcedureCode = "99213", Units = 1, ServiceDate = new DateOnly(2026, 1, 15) }
        ]
    };

    private static BenefitResolutionRequest BenefitsBody() => new()
    {
        MemberId = "MBR-1",
        SubscriberId = "SUB-1",
        BenefitPlanId = PlanGuid,
        ServiceDate = new DateOnly(2026, 1, 15),
        ClaimId = "CLM-AUTH",
        NetworkTier = CloudHealthOffice.BenefitEngine.Domain.NetworkTier.InNetwork,
        Lines = [new ClaimLineInput { LineNumber = 1, ProcedureCode = "99213", PlaceOfService = "11", BilledAmount = 100m }]
    };

    private static object PlanBody(string id = "plan-row-1") => new
    {
        id,
        planId = "plan-auth",
        versionId = "v-" + id,
        planName = "Auth Plan",
        payer = "Acme",
        effectiveDate = new DateTime(2026, 1, 1),
        createdBy = "attacker",
        publishedBy = "attacker",
        tenantId = "victim-tenant"
    };

    // ── authentication and tenant ────────────────────────────────────────

    [Fact]
    public async Task NoToken_Returns401()
    {
        using var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/plans")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/v1/adjudication/estimate", EstimateBody())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/v1/plans", PlanBody())).StatusCode);
    }

    [Fact]
    public async Task TenantHeaderWithoutToken_IsNotTrusted_Returns401()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/plans")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/ncci/version")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/v1/adjudication/estimate", EstimateBody())).StatusCode);

        // The old local middleware also honoured X-Dev-Tenant-ID.
        using var dev = _factory.CreateClient();
        dev.DefaultRequestHeaders.Add("X-Dev-Tenant-ID", Tenant);
        Assert.Equal(HttpStatusCode.Unauthorized, (await dev.GetAsync("/api/v1/plans")).StatusCode);
    }

    [Fact]
    public async Task TenantHeaderDisagreeingWithToken_Returns403()
    {
        using var client = BearerOnlyClient(Tenant);
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "other-tenant");

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/plans")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/v1/adjudication/estimate", EstimateBody())).StatusCode);
    }

    [Fact]
    public async Task Estimate_UsesTokenTenant()
    {
        using var client = BearerOnlyClient("estimate-tenant");

        var resp = await client.PostAsJsonAsync("/api/v1/adjudication/estimate", EstimateBody());

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        await _factory.EstimateService.Received().EstimateAsync(
            "estimate-tenant", Arg.Any<PaymentEstimateRequest>(), Arg.Any<CancellationToken>());
    }

    // ── permissions ──────────────────────────────────────────────────────

    [Fact]
    public async Task ClaimsExaminer_CanRunCalculations_ButGets403OnPlanCreateAndUpdate()
    {
        using var client = TokenClient("examiner-1", ChoRolePermissions.ClaimsExaminer);

        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/api/v1/adjudication/estimate", EstimateBody())).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/api/v1/adjudication/ncci-check", NcciBody())).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/api/v1/ncci/scrub", NcciBody())).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/api/v1/adjudication/resolve-rates",
                new List<PricingRequest> { new() { ProcedureCode = "99213", BilledAmount = 100m } })).StatusCode);
        // claims:work covers the accumulator-writing calculation as well.
        Assert.NotEqual(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/v1/adjudication/calculate-benefits", BenefitsBody())).StatusCode);
        // benefits:read covers plan reads.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/plans")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/v1/plans", PlanBody())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PutAsJsonAsync("/api/v1/plans/plan-row-1", PlanBody())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/v1/plans/drafts", PlanBody())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsync("/api/v1/plans/plan-auth/versions/v1/publish", JsonContent.Create(new { }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/v1/ncci/import", new { quarter = "2026Q1" })).StatusCode);
    }

    [Fact]
    public async Task RoleWithoutBenefitsReadOrClaimsWork_Gets403OnCalculations()
    {
        // Finance holds neither benefits:read nor claims:work.
        using var client = TokenClient("finance-1", ChoRolePermissions.Finance);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/v1/adjudication/estimate", EstimateBody())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/v1/adjudication/ncci-check", NcciBody())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/v1/ncci/scrub", NcciBody())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/v1/adjudication/calculate-benefits", BenefitsBody())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/plans")).StatusCode);
    }

    [Fact]
    public async Task BenefitsReaderWithoutClaimsWork_CanEstimate_ButCannotRunAccumulatorWritingAdjudication()
    {
        // MemberServices holds benefits:read but not claims:work or settings:manage.
        using var client = TokenClient("member-services-1", ChoRolePermissions.MemberServices);

        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/api/v1/adjudication/estimate", EstimateBody())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/v1/adjudication/calculate-benefits", BenefitsBody())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/v1/adjudication/adjudicate", new { claimId = "CLM-AUTH" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/v1/plans", PlanBody())).StatusCode);
    }

    // ── actor and tenant from the token, not the request ─────────────────

    [Fact]
    public async Task PlanWrites_ActorComesFromToken_NotFromXUserIdHeaderOrBody()
    {
        using var client = TokenClient("planner-7", ChoRolePermissions.TenantAdmin);
        client.DefaultRequestHeaders.Add("X-User-Id", "attacker");

        var draft = await client.PostAsJsonAsync("/api/v1/plans/drafts", PlanBody("row-draft"));
        Assert.Equal(HttpStatusCode.Created, draft.StatusCode);
        await _factory.PlanService.Received().CreateDraftAsync(
            Arg.Is<BenefitPlan>(p => p.Id == "row-draft"), Tenant, "planner-7");

        var created = await client.PostAsJsonAsync("/api/v1/plans", PlanBody("row-create"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await _factory.PlanService.Received().CreatePlanAsync(
            Arg.Is<BenefitPlan>(p => p.Id == "row-create"), Tenant, "planner-7");

        var updated = await client.PutAsJsonAsync("/api/v1/plans/row-update", PlanBody("row-update"));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        await _factory.PlanService.Received().UpdatePlanAsync(
            Arg.Is<BenefitPlan>(p => p.Id == "row-update"), Tenant, "planner-7");

        await client.PostAsync("/api/v1/plans/plan-auth/versions/v-auth/publish", JsonContent.Create(new { }));
        await _factory.PlanService.Received().PublishVersionAsync(
            "plan-auth", "v-auth", Tenant, "planner-7", Arg.Any<DateTime?>());

        await _factory.PlanService.DidNotReceive().CreateDraftAsync(Arg.Any<BenefitPlan>(), Arg.Any<string>(), "attacker");
        await _factory.PlanService.DidNotReceive().PublishVersionAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), "attacker", Arg.Any<DateTime?>());
    }

    [Fact]
    public async Task Ncci_TenantComesFromToken_NotFromHeaderOrBody()
    {
        using var client = BearerOnlyClient("ncci-tenant");

        var version = await client.GetAsync("/api/v1/ncci/version");
        Assert.NotEqual(HttpStatusCode.BadRequest, version.StatusCode);
        await _factory.NcciEngine.Received().GetTableVersionAsync("ncci-tenant", Arg.Any<CancellationToken>());

        var import = await client.PostAsJsonAsync("/api/v1/ncci/import",
            new { tenantId = "victim-tenant", quarter = "2026Q1" });
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);
        await _factory.NcciEngine.Received().ImportQuarterlyUpdateAsync(
            "ncci-tenant", "2026Q1",
            Arg.Any<IReadOnlyList<CloudHealthOffice.NcciEngine.Domain.NcciEditPair>>(),
            Arg.Any<IReadOnlyList<CloudHealthOffice.NcciEngine.Domain.MueEntry>>(),
            Arg.Any<CancellationToken>());

        var scrub = await client.PostAsJsonAsync("/api/v1/ncci/scrub", NcciBody("victim-tenant"));
        Assert.Equal(HttpStatusCode.OK, scrub.StatusCode);
        await _factory.NcciEngine.Received().ScrubAsync(
            Arg.Is<NcciScrubRequest>(r => r.ClaimId == "CLM-AUTH" && r.TenantId == "ncci-tenant"),
            Arg.Any<CancellationToken>());

        await _factory.NcciEngine.DidNotReceive().ImportQuarterlyUpdateAsync(
            "victim-tenant", Arg.Any<string>(),
            Arg.Any<IReadOnlyList<CloudHealthOffice.NcciEngine.Domain.NcciEditPair>>(),
            Arg.Any<IReadOnlyList<CloudHealthOffice.NcciEngine.Domain.MueEntry>>(),
            Arg.Any<CancellationToken>());
        await _factory.NcciEngine.DidNotReceive().ScrubAsync(
            Arg.Is<NcciScrubRequest>(r => r.TenantId == "victim-tenant"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HealthProbe_NeedsNoToken()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/health/live");
        Assert.NotEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}
