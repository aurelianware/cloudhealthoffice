using System.Net;
using System.Net.Http.Json;
using AccumulatorService.Models;
using AccumulatorService.Repositories;
using AccumulatorService.Services;
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
        _client = f.CreateClient();
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
    public async Task MissingTenant_Returns400()
    {
        using var naked = _factory.CreateClient();
        var resp = await naked.GetAsync("/api/v1/accumulators/m-1");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
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
            ActorId = "op-1",
            Reason = "", // invalid
            DeductibleDelta = -10m
        };
        var resp = await _client.PostAsJsonAsync("/api/v1/accumulators/m-1/adjust", req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
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
