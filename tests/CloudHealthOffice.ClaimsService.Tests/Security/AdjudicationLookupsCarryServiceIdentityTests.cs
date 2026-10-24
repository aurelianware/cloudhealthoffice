using System.Net;
using System.Security.Claims;
using System.Text;
using ClaimsService.Adapters;
using global::ClaimsService.Models.Adjudication;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using CloudHealthOffice.CobEngine.Services;
using CloudHealthOffice.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using ClaimsService.Services;
using ClaimsService.Services.Resolution;
using CloudHealthOffice.Infrastructure.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace CloudHealthOffice.ClaimsService.Tests.Security;

/// <summary>D-36/D-40: real factory client + outbound handler inside an examiner request.</summary>
public sealed class AdjudicationLookupsCarryServiceIdentityTests
{
    private const string Tenant = "tenant-claims";
    private const string ClientId = "claims-service";

    private static NoCallerHost Host(string host = "coverage-service", bool tokenUnavailable = false)
        => new(ClientId, (services, outbound) =>
        {
            services.AddHttpClient(UpstreamClientNames.CoverageService,
                    c => c.BaseAddress = new Uri("http://" + host))
                .ConfigurePrimaryHttpMessageHandler(() => outbound);
            services.AddHttpClient(HttpBenefitPlanResolver.HttpClientName,
                    c => c.BaseAddress = new Uri(host == "coverage-service" ? "http://benefit-plan-service" : "http://" + host))
                .ConfigurePrimaryHttpMessageHandler(() => outbound);
            services.AddScoped<HttpCoverageClient>();
            services.AddScoped<HttpCoverageResolver>();
            services.AddScoped<HttpBenefitPlanResolver>();
            if (tokenUnavailable) services.AddSingleton<IChoServiceTokenSource>(new UnavailableTokens());
        });

    private static DefaultHttpContext Examiner(string? tenant, string role = ChoRolePermissions.ClaimsExaminer)
    {
        // Explicit permissions model a stored tenant role with no downstream
        // coverage:read/benefits:read, even if the code catalogue grants more.
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "examiner-1")], "Bearer")),
        };
        http.Request.Headers.Authorization = "Bearer " + ChoDevelopmentAuth.UserTokenIssuer()
            .IssueUserToken("examiner-1", tenant ?? Tenant, [role], permissions: ["claims:work"]);
        if (tenant != null) http.Items["TenantId"] = tenant;
        return http;
    }

    private static string Path(string lookup) => lookup switch
    {
        "cob" => "/api/v1/coverage/member/MEM-1/cob",
        "active" => "/api/v1/coverage/member/MEM-1/active",
        _ => "/api/v1/plans/PLAN-1",
    };

    private static HttpResponseMessage Reply(string lookup, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(lookup switch
            {
                "cob" => "[]",
                "active" => """[{"planId":"PLAN-1"}]""",
                _ => """{"id":"PLAN-1","planName":"Test plan","benefits":[{"isCovered":true,"priorAuthRequired":true,"cptCodes":["99213"]}]}""",
            }, Encoding.UTF8, "application/json"),
        };

    private static async Task<bool> Lookup(NoCallerHost host, string lookup)
    {
        using var scope = host.Services.CreateScope();
        return lookup switch
        {
            "cob" => await scope.ServiceProvider.GetRequiredService<HttpCoverageClient>()
                .GetCobEntriesAsync(Tenant, "MEM-1", DateTime.UtcNow.Date) is not null,
            "active" => await scope.ServiceProvider.GetRequiredService<HttpCoverageResolver>()
                .ResolveCoverageAsync(Tenant, "MEM-1", DateTime.UtcNow.Date) is { PlanId: "PLAN-1" },
            _ => await scope.ServiceProvider.GetRequiredService<HttpBenefitPlanResolver>()
                .GetPlanAsync(Tenant, "PLAN-1") is { PriorAuthRequiredProcedureCodes: var codes } && codes.Contains("99213"),
        };
    }

    private static async Task AssertUnavailable(NoCallerHost host, string lookup)
    {
        if (lookup == "plan")
            await FluentActions.Awaiting(() => Lookup(host, lookup)).Should().ThrowAsync<BenefitPlanUnavailableException>();
        else
            (await Lookup(host, lookup)).Should().BeFalse();
    }

    [Theory]
    [InlineData("cob", ChoRolePermissions.ClaimsExaminer)]
    [InlineData("active", ChoRolePermissions.ClaimsExaminer)]
    [InlineData("plan", ChoRolePermissions.ClaimsExaminer)]
    [InlineData("cob", ChoRolePermissions.ClaimsSupervisor)]
    [InlineData("active", ChoRolePermissions.ClaimsSupervisor)]
    [InlineData("plan", ChoRolePermissions.ClaimsSupervisor)]
    public async Task Examiner_rerun_sends_the_claims_service_identity_for_the_resolved_tenant(string lookup, string role)
    {
        using var host = Host();
        host.Services.GetRequiredService<IHttpContextAccessor>().HttpContext = Examiner(Tenant, role);
        host.Outbound.Respond = request => NoCallerHost.TokenOf(request) == (Tenant, ClientId)
            ? Reply(lookup) : Reply(lookup, HttpStatusCode.Forbidden);

        (await Lookup(host, lookup)).Should().BeTrue();
        NoCallerHost.TokenOf(host.Outbound.Last).Should().Be((Tenant, ClientId));
        host.Outbound.Last!.RequestUri!.AbsolutePath.Should().Be(Path(lookup));
        host.Outbound.Last.Headers.GetValues("X-Tenant-ID").Should().ContainSingle().Which.Should().Be(Tenant);
    }

    [Theory]
    [InlineData("cob")]
    [InlineData("active")]
    [InlineData("plan")]
    public async Task Background_lookup_keeps_the_service_identity(string lookup)
    {
        using var host = Host();
        host.Outbound.Respond = _ => Reply(lookup);
        (await Lookup(host, lookup)).Should().BeTrue();
        NoCallerHost.TokenOf(host.Outbound.Last).Should().Be((Tenant, ClientId));
    }

    [Theory]
    [InlineData("cob", "tenant-other")]
    [InlineData("active", "tenant-other")]
    [InlineData("plan", "tenant-other")]
    [InlineData("cob", null)]
    [InlineData("active", null)]
    [InlineData("plan", null)]
    public async Task A_foreign_or_unresolved_caller_tenant_gets_no_token_and_no_success(string lookup, string? callerTenant)
    {
        using var host = Host();
        host.Services.GetRequiredService<IHttpContextAccessor>().HttpContext = Examiner(callerTenant);
        host.Outbound.Respond = _ => Reply(lookup, HttpStatusCode.Unauthorized);
        await AssertUnavailable(host, lookup);
        host.Outbound.Last!.Headers.Authorization.Should().BeNull();
    }

    [Theory]
    [InlineData("cob")]
    [InlineData("active")]
    [InlineData("plan")]
    public async Task An_untrusted_destination_receives_no_credentials(string lookup)
    {
        using var host = Host("untrusted.example.invalid");
        host.Services.GetRequiredService<IHttpContextAccessor>().HttpContext = Examiner(Tenant);
        host.Outbound.Respond = _ => Reply(lookup, HttpStatusCode.Unauthorized);
        await AssertUnavailable(host, lookup);
        host.Outbound.Last!.Headers.Authorization.Should().BeNull();
    }

    [Theory]
    [InlineData("cob")]
    [InlineData("active")]
    [InlineData("plan")]
    public async Task A_token_source_outage_does_not_fall_back_to_the_examiner_token(string lookup)
    {
        using var host = Host(tokenUnavailable: true);
        host.Services.GetRequiredService<IHttpContextAccessor>().HttpContext = Examiner(Tenant);
        await AssertUnavailable(host, lookup);
        host.Outbound.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("cob", 403)]
    [InlineData("active", 403)]
    [InlineData("plan", 403)]
    [InlineData("cob", 503)]
    [InlineData("active", 503)]
    [InlineData("plan", 503)]
    public async Task An_upstream_refusal_or_outage_remains_unavailable(string lookup, int status)
    {
        using var host = Host();
        host.Services.GetRequiredService<IHttpContextAccessor>().HttpContext = Examiner(Tenant);
        host.Outbound.Respond = _ => Reply(lookup, (HttpStatusCode)status);
        await AssertUnavailable(host, lookup);
        NoCallerHost.TokenOf(host.Outbound.Last).Should().Be((Tenant, ClientId));
    }

    [Theory]
    [InlineData("cob")]
    [InlineData("active")]
    [InlineData("plan")]
    public async Task Definitive_not_found_keeps_its_existing_domain_meaning(string lookup)
    {
        using var host = Host();
        host.Services.GetRequiredService<IHttpContextAccessor>().HttpContext = Examiner(Tenant);
        host.Outbound.Respond = _ => Reply(lookup, HttpStatusCode.NotFound);
        // COB 404 means confirmed no other coverage; active coverage/plan 404
        // mean not found. Neither is reclassified as an outage by this fix.
        (await Lookup(host, lookup)).Should().Be(lookup == "cob");
        NoCallerHost.TokenOf(host.Outbound.Last).Should().Be((Tenant, ClientId));
    }

    [Theory]
    [InlineData(ChoRolePermissions.ClaimsExaminer)]
    [InlineData(ChoRolePermissions.ClaimsSupervisor)]
    public async Task Approval_readjudication_resolves_the_plan_and_passes_COB_through_real_outbound_handlers(string role)
    {
        using var host = Host();
        host.Services.GetRequiredService<IHttpContextAccessor>().HttpContext = Examiner(Tenant, role);
        host.Outbound.Respond = request => NoCallerHost.TokenOf(request) != (Tenant, ClientId)
            ? Reply("cob", HttpStatusCode.Forbidden)
            : Reply(request.RequestUri!.AbsolutePath.EndsWith("/active", StringComparison.Ordinal) ? "active"
                : request.RequestUri.AbsolutePath.EndsWith("/cob", StringComparison.Ordinal) ? "cob" : "plan");
        using var scope = host.Services.CreateScope();
        var adapter = Substitute.For<IClaimAdapter>();
        adapter.Platform.Returns("cho");
        adapter.GetClaimAsync(Arg.Any<global::ClaimsService.Models.ClaimAdapterRequest>(), Arg.Any<CancellationToken>())
            .Returns(new global::ClaimsService.Models.ClaimAdapterResponse
            {
                Platform = "cho", Claim = new global::ClaimsService.Models.AdapterClaim
                {
                    Id = "claim-rerun", ClaimVersionId = "claim-rerun", MemberId = "MEM-1",
                    // EDI claims can arrive without a plan id: exercise /active too.
                    BenefitPlanId = null, ServiceDateFrom = DateTime.UtcNow.Date,
                    ServiceDateTo = DateTime.UtcNow.Date, PayerResponsibilityCode = "P",
                },
            });
        var cache = new ClaimTenantConfigCache(Substitute.For<IHttpClientFactory>(), Substitute.For<IConfiguration>(),
            NullLogger<ClaimTenantConfigCache>.Instance);
        var factory = new ClaimAdapterFactory([adapter], cache, NullLogger<ClaimAdapterFactory>.Instance);
        var cob = new CoordinationOfBenefitsStage(scope.ServiceProvider.GetRequiredService<HttpCoverageClient>(),
            Substitute.For<IPayerOrderService>(), Options.Create(new TenantEnforcementPolicyOptions()),
            NullLogger<CoordinationOfBenefitsStage>.Instance);
        ClaimAdjudicationContext? prepared = null;
        var observe = Substitute.For<IClaimAdjudicationStage>();
        observe.Name.Returns("ObservePreparedContext");
        observe.Order.Returns(300);
        observe.ExecuteAsync(Arg.Any<ClaimAdjudicationContext>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                prepared = call.Arg<ClaimAdjudicationContext>();
                return ClaimAdjudicationStageResult.Pass("ObservePreparedContext");
            });
        var orchestrator = new ClaimAdjudicationOrchestrator(factory,
            scope.ServiceProvider.GetRequiredService<HttpBenefitPlanResolver>(), Substitute.For<IMemberResolver>(),
            scope.ServiceProvider.GetRequiredService<HttpCoverageResolver>(), [cob, observe],
            Substitute.For<IClaimVersionEventPublisher>(), Substitute.For<IMessageBus>(), new AdjudicationTenantContext(),
            Substitute.For<IClaimAdjustmentService>(), Options.Create(new AdjudicationPipelineOptions()),
            NullLogger<ClaimAdjudicationOrchestrator>.Instance);

        var result = await orchestrator.ReadjudicateForApprovalAsync(Tenant, "claim-rerun",
            new ExaminerApproval { ExaminerId = "examiner-1" }, CancellationToken.None);

        result.Outcome.Should().Be(ClaimAdjudicationOutcome.Pass);
        result.CheckFailed.Should().BeFalse();
        prepared.Should().NotBeNull();
        prepared!.PlanResolutionFailed.Should().BeFalse();
        prepared.Claim.BenefitPlanId.Should().Be("PLAN-1");
        prepared.ResolvedPlan!.PriorAuthRequiredProcedureCodes.Should().Contain("99213");
        prepared.ExaminerApproval!.ExaminerId.Should().Be("examiner-1");
        host.Outbound.Requests.Select(r => r.RequestUri!.AbsolutePath).Should().BeEquivalentTo(
            Path("active"), Path("plan"), Path("cob"));
        foreach (var sent in host.Outbound.Requests)
            NoCallerHost.TokenOf(sent).Should().Be((Tenant, ClientId));
    }

    private sealed class UnavailableTokens : IChoServiceTokenSource
    {
        public string ClientId => AdjudicationLookupsCarryServiceIdentityTests.ClientId;
        public ValueTask<string> GetTokenAsync(string tenantId, CancellationToken cancellationToken = default)
            => ValueTask.FromException<string>(new ChoServiceTokenUnavailableException("test issuer unavailable"));
    }
}
