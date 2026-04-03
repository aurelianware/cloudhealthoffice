using NBomber.Contracts;
using NBomber.CSharp;
using NBomber.Http.CSharp;

namespace CloudHealthOffice.LoadTests;

/// <summary>
/// Load tests for the Provider Verification Service.
///
/// Scenarios:
///   1. NPPES lookup latency — GET /api/v1/providers/{npi}/nppes
///   2. Integrity score throughput — GET /api/v1/providers/{npi}/integrity-score
/// </summary>
public class ProviderVerificationLoadTests
{
    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 100
        };
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(LoadTestConfig.ProviderVerificationServiceUrl),
            Timeout = TimeSpan.FromSeconds(10)
        };
        client.DefaultRequestHeaders.Add("X-Tenant-ID", LoadTestConfig.TenantId);
        return client;
    }

    [Fact(Skip = "Load test — run manually or via CI with LOAD_TEST_PROVIDER_VERIFICATION_URL set")]
    public void NppesLookup_P99Under500ms()
    {
        var client = CreateClient();

        // Known valid NPI for testing (Luhn-valid)
        var testNpi = "1234567893";

        var scenario = Scenario.Create("nppes_lookup", async context =>
        {
            var request = Http.CreateRequest("GET", $"/api/v1/providers/{testNpi}/nppes");
            var response = await Http.Send(client, request);

            return response.StatusCode == "200" || response.StatusCode == "404"
                ? Response.Ok()
                : Response.Fail();
        })
        .WithWarmUpDuration(LoadTestConfig.WarmUpDuration)
        .WithLoadSimulations(
            Simulation.Inject(
                rate: LoadTestConfig.TargetRps,
                interval: TimeSpan.FromSeconds(1),
                during: LoadTestConfig.SustainDuration));

        var stats = NBomberRunner
            .RegisterScenarios(scenario)
            .WithReportFolder(LoadTestConfig.ReportFolder)
            .Run();

        var scenarioStats = stats.ScenarioStats[0];
        Assert.True(
            scenarioStats.Ok.Latency.Percent99 < 500,
            $"NPPES lookup p99 latency {scenarioStats.Ok.Latency.Percent99}ms exceeds 500ms target");
        Assert.True(
            scenarioStats.Fail.Request.Percent < LoadTestConfig.MaxErrorRate * 100,
            $"Error rate {scenarioStats.Fail.Request.Percent}% exceeds {LoadTestConfig.MaxErrorRate * 100}% threshold");
    }

    [Fact(Skip = "Load test — run manually or via CI with LOAD_TEST_PROVIDER_VERIFICATION_URL set")]
    public void IntegrityScore_P99Under2s()
    {
        var client = CreateClient();
        var testNpi = "1234567893";

        var scenario = Scenario.Create("integrity_score", async context =>
        {
            var request = Http.CreateRequest("GET", $"/api/v1/providers/{testNpi}/integrity-score");
            var response = await Http.Send(client, request);

            return response.StatusCode == "200" || response.StatusCode == "404"
                ? Response.Ok()
                : Response.Fail();
        })
        .WithWarmUpDuration(LoadTestConfig.WarmUpDuration)
        .WithLoadSimulations(
            Simulation.Inject(
                rate: Math.Max(1, LoadTestConfig.TargetRps / 5), // Lower RPS — heavier endpoint
                interval: TimeSpan.FromSeconds(1),
                during: LoadTestConfig.SustainDuration));

        var stats = NBomberRunner
            .RegisterScenarios(scenario)
            .WithReportFolder(LoadTestConfig.ReportFolder)
            .Run();

        var scenarioStats = stats.ScenarioStats[0];
        Assert.True(
            scenarioStats.Ok.Latency.Percent99 < 2000,
            $"Integrity score p99 latency {scenarioStats.Ok.Latency.Percent99}ms exceeds 2000ms target");
        Assert.True(
            scenarioStats.Fail.Request.Percent < LoadTestConfig.MaxErrorRate * 100,
            $"Error rate {scenarioStats.Fail.Request.Percent}% exceeds {LoadTestConfig.MaxErrorRate * 100}% threshold");
    }
}
