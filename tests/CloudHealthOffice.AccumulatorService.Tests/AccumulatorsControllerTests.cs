using System.Net;
using System.Net.Http.Json;
using AccumulatorService.Models;
using AccumulatorService.Repositories;
using AccumulatorService.Services;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace CloudHealthOffice.AccumulatorService.Tests;

/// <summary>
/// Integration tests for AccumulatorsController via WebApplicationFactory.
/// Mongo/Cosmos/Kafka are replaced with in-memory fixtures so the test run is
/// hermetic — no external infra required.
/// </summary>
public class AccumulatorsControllerTests : IClassFixture<AccumulatorsControllerTests.Factory>
{
    private readonly Factory _factory;
    private readonly HttpClient _client;

    public AccumulatorsControllerTests(Factory f)
    {
        _factory = f;
        // The handler turns X-Tenant-ID into a development-signed token for that
        // tenant; the server takes the tenant from the token.
        _client = f.CreateDefaultClient(new ChoDevelopmentTokenHandler());
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");
    }

    [Fact]
    public async Task Health_LivenessReturnsOk()
    {
        // /health/live skips DB probes; /health aggregates the mongodb check which
        // would dial a nonexistent server in the hermetic test harness.
        var resp = await _client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Get_WithNoSnapshot_ReturnsZeroStateNot404()
    {
        var resp = await _client.GetAsync("/api/v1/accumulators/unknown-member");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<AccumulatorResponse>();
        Assert.NotNull(body);
        Assert.Equal(0m, body!.IndividualDeductibleUsed);
    }

    [Fact]
    public async Task MissingToken_Returns401()
    {
        using var naked = _factory.CreateClient();
        var resp = await naked.GetAsync("/api/v1/accumulators/m-1");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task TenantHeaderOrQueryWithoutToken_IsNotTrusted()
    {
        using var naked = _factory.CreateClient();
        naked.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");
        var byHeader = await naked.GetAsync("/api/v1/accumulators/m-1");
        Assert.Equal(HttpStatusCode.Unauthorized, byHeader.StatusCode);

        using var naked2 = _factory.CreateClient();
        var byQuery = await naked2.GetAsync("/api/v1/accumulators/m-1?tenantId=test-tenant");
        Assert.Equal(HttpStatusCode.Unauthorized, byQuery.StatusCode);
    }

    [Fact]
    public async Task TenantHeaderDisagreeingWithToken_Returns403()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", ChoDevelopmentAuth.UserToken("test-tenant", ChoRolePermissions.TenantAdmin));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "other-tenant");
        var resp = await client.GetAsync("/api/v1/accumulators/m-1");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task ReadOnlyRole_CanRead_ButCannotAdjust()
    {
        // MemberServices holds accumulators:read but not accumulators:write.
        using var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler("member-services-user", ChoRolePermissions.MemberServices));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");

        var read = await client.GetAsync("/api/v1/accumulators/m-ro");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        var adjust = await client.PostAsJsonAsync("/api/v1/accumulators/m-ro/adjust", new AccumulatorAdjustmentRequest
        {
            PlanYearStart = new DateTime(2026, 1, 1),
            PlanYearEnd = new DateTime(2026, 12, 31),
            Reason = "Should be refused",
            DeductibleDelta = 10m
        });
        Assert.Equal(HttpStatusCode.Forbidden, adjust.StatusCode);
    }

    [Fact]
    public async Task CompatAlias_ReturnsSameShapeAsCanonical()
    {
        var canonical = await _client.GetAsync("/api/v1/accumulators/m-42");
        var alias = await _client.GetAsync("/api/v1/members/m-42/accumulators");
        Assert.Equal(HttpStatusCode.OK, canonical.StatusCode);
        Assert.Equal(HttpStatusCode.OK, alias.StatusCode);

        var a = await canonical.Content.ReadFromJsonAsync<AccumulatorResponse>();
        var b = await alias.Content.ReadFromJsonAsync<AccumulatorResponse>();
        Assert.Equal(a!.MemberId, b!.MemberId);
        Assert.Equal(a.IndividualDeductibleLimit, b.IndividualDeductibleLimit);
    }

    [Fact]
    public async Task Adjust_RequiresReason()
    {
        var req = new AccumulatorAdjustmentRequest
        {
            PlanYearStart = new DateTime(2026, 1, 1),
            PlanYearEnd = new DateTime(2026, 12, 31),
            Reason = "", // invalid
            DeductibleDelta = -10m
        };
        var resp = await _client.PostAsJsonAsync("/api/v1/accumulators/m-1/adjust", req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Adjust_ActorComesFromToken_NotFromRequestBody()
    {
        // The body still names an actor (old clients, or a forger); the server
        // must record the authenticated token subject instead.
        using var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler("ops-user-7"));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");
        var body = new
        {
            planYearStart = new DateTime(2026, 1, 1),
            planYearEnd = new DateTime(2026, 12, 31),
            actorId = "attacker",
            reason = "Out-of-system payment posted manually",
            deductibleDelta = 50m,
            adjustmentId = "adj-actor-from-token"
        };

        var resp = await client.PostAsJsonAsync("/api/v1/accumulators/m-actor/adjust", body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var repo = (InMemoryAccumulatorRepository)_factory.Services.GetRequiredService<IAccumulatorRepository>();
        var audit = repo.Events.Single(e => e.MemberId == "m-actor");
        Assert.Equal("ops-user-7", audit.ActorId);
        Assert.Equal("test-tenant", audit.TenantId);

        var pub = (RecordingPublisher)_factory.Services.GetRequiredService<IAccumulatorEventPublisher>();
        var adjusted = pub.Adjusted.Single(e => e.MemberId == "m-actor");
        Assert.Equal("ops-user-7", adjusted.ActorId);
    }

    public class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                // Provide a dummy Mongo connection so Program.cs takes the Mongo branch
                // (the other branch throws when both stores are unset). The actual Mongo
                // client is never dialed — we remove the IAccumulatorRepository registration
                // before any request runs and replace it with the in-memory fake.
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MongoDb:ConnectionString"] = "mongodb://localhost:27017",
                    ["MongoDb:DatabaseName"] = "AccumulatorTests",
                    ["Kafka:BootstrapServers"] = ""
                });
            });
            builder.ConfigureServices(services =>
            {
                foreach (var t in new[]
                {
                    typeof(IAccumulatorRepository),
                    typeof(IProcessedClaimStore),
                    typeof(IAccumulatorEventPublisher)
                })
                {
                    var descriptors = services.Where(d => d.ServiceType == t).ToList();
                    foreach (var d in descriptors) services.Remove(d);
                }

                // Remove DB client registrations that would try to connect.
                var toDrop = services.Where(d =>
                    d.ServiceType.FullName?.Contains("Mongo") == true ||
                    d.ServiceType.FullName?.Contains("Cosmos") == true).ToList();
                foreach (var d in toDrop) services.Remove(d);

                services.AddSingleton<IAccumulatorRepository, InMemoryAccumulatorRepository>();
                services.AddSingleton<IProcessedClaimStore, InMemoryProcessedClaimStore>();
                services.AddSingleton<IAccumulatorEventPublisher, RecordingPublisher>();
                services.AddScoped<IAccumulatorService, global::AccumulatorService.Services.AccumulatorService>();
            });
        }
    }
}
