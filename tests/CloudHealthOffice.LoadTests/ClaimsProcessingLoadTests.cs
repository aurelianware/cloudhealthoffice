using System.Text.Json;
using NBomber.Contracts;
using NBomber.CSharp;
using NBomber.Http.CSharp;

namespace CloudHealthOffice.LoadTests;

/// <summary>
/// Load tests for the claims processing pipeline.
///
/// Scenarios:
///   1. Claim submission throughput — POST /api/claims at sustained rate
///   2. Claim retrieval latency — GET /api/claims/{id} under load
///   3. Mixed workload — realistic blend of reads and writes
///
/// Pattern: This test class demonstrates the load testing pattern used
/// throughout the application.  To add load tests for other modules:
///   1. Create a new *LoadTests.cs file in this project
///   2. Use LoadTestConfig for endpoints, durations, and SLA thresholds
///   3. Define NBomber scenarios with inject/ramp-up profiles
///   4. Assert on p99 latency and error rate from NBomber stats
///
/// The quality-gate.yml pipeline discovers and runs all tests in this project.
/// </summary>
public class ClaimsProcessingLoadTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static HttpClient CreateClient(string baseUrl)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 100
        };
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(10)
        };
        client.DefaultRequestHeaders.Add("X-Tenant-ID", LoadTestConfig.TenantId);
        return client;
    }

    private static object GenerateClaim(int index)
    {
        var serviceDate = DateTime.UtcNow.Date;
        return new
        {
            tenantId = LoadTestConfig.TenantId,
            claimNumber = $"LOAD-{index:D8}-{Guid.NewGuid():N}"[..20],
            memberId = $"MBR-LOAD-{index % 1000:D4}",
            subscriberId = $"SUB-LOAD-{index % 500:D4}",
            billingProviderNPI = "1234567890",
            billingProviderName = "Load Test Clinic",
            placeOfServiceCode = "11",
            claimType = 1,
            lineOfBusiness = 1,
            claimFrequencyCode = "1",
            totalChargeAmount = 150.00m + (index % 100),
            serviceDateFrom = serviceDate,
            serviceDateTo = serviceDate,
            diagnosisCodes = new[]
            {
                new { code = "Z00.00", codeQualifier = "ABK", pointerNumber = 1, description = "General exam" }
            },
            claimLines = new[]
            {
                new
                {
                    lineNumber = 1,
                    procedureCode = "99213",
                    procedureDescription = "Office visit level 3",
                    modifiers = Array.Empty<string>(),
                    diagnosisPointers = new[] { 1 },
                    units = 1m,
                    chargeAmount = 150.00m + (index % 100),
                    serviceDateFrom = serviceDate,
                    serviceDateTo = serviceDate,
                    placeOfServiceCode = "11"
                }
            },
            status = 1
        };
    }

    /// <summary>
    /// Scenario 1: Sustained claim submission throughput.
    /// Ramps up to target RPS, holds, then ramps down.
    /// Validates p99 latency and error rate against SLA thresholds.
    /// </summary>
    [Fact]
    public void ClaimSubmission_SustainedLoad_MeetsSlaTargets()
    {
        var counter = 0;
        using var client = CreateClient(LoadTestConfig.ClaimsServiceUrl);

        var scenario = Scenario.Create("claim_submission", async context =>
        {
            var index = Interlocked.Increment(ref counter);
            var claim = GenerateClaim(index);

            var request = Http.CreateRequest("POST", "/api/claims")
                .WithHeader("Content-Type", "application/json")
                .WithBody(new StringContent(
                    JsonSerializer.Serialize(claim, Json),
                    System.Text.Encoding.UTF8,
                    "application/json"));

            var response = await Http.Send(client, request);
            return response;
        })
        .WithLoadSimulations(
            Simulation.RampingInject(
                rate: LoadTestConfig.TargetRps,
                interval: TimeSpan.FromSeconds(1),
                during: LoadTestConfig.WarmUpDuration),
            Simulation.Inject(
                rate: LoadTestConfig.TargetRps,
                interval: TimeSpan.FromSeconds(1),
                during: LoadTestConfig.SustainDuration),
            Simulation.RampingInject(
                rate: 0,
                interval: TimeSpan.FromSeconds(1),
                during: LoadTestConfig.CoolDownDuration)
        );

        var stats = NBomberRunner
            .RegisterScenarios(scenario)
            .WithReportFolder(LoadTestConfig.ReportFolder)
            .WithReportFormats(ReportFormat.Html, ReportFormat.Csv)
            .Run();

        var scenarioStats = stats.ScenarioStats[0];

        // SLA assertions
        Assert.True(
            scenarioStats.Ok.Latency.Percent99 < LoadTestConfig.MaxP99Latency.TotalMilliseconds,
            $"p99 latency {scenarioStats.Ok.Latency.Percent99}ms exceeds SLA of {LoadTestConfig.MaxP99Latency.TotalMilliseconds}ms");

        var errorRate = scenarioStats.Fail.Request.Count /
            (double)Math.Max(1, scenarioStats.Ok.Request.Count + scenarioStats.Fail.Request.Count);
        Assert.True(
            errorRate <= LoadTestConfig.MaxErrorRate,
            $"Error rate {errorRate:P2} exceeds SLA of {LoadTestConfig.MaxErrorRate:P2}");
    }

    /// <summary>
    /// Scenario 2: Mixed read/write workload simulating realistic traffic.
    /// 70% reads (GET /api/claims) + 30% writes (POST /api/claims).
    /// </summary>
    [Fact]
    public void ClaimsMixedWorkload_RealisticTraffic_MeetsSlaTargets()
    {
        var writeCounter = 0;
        var createdClaimIds = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var client = CreateClient(LoadTestConfig.ClaimsServiceUrl);

        var writeScenario = Scenario.Create("claim_write", async context =>
        {
            var index = Interlocked.Increment(ref writeCounter);
            var claim = GenerateClaim(index);

            var request = Http.CreateRequest("POST", "/api/claims")
                .WithHeader("Content-Type", "application/json")
                .WithBody(new StringContent(
                    JsonSerializer.Serialize(claim, Json),
                    System.Text.Encoding.UTF8,
                    "application/json"));

            var response = await Http.Send(client, request);

            // Track created IDs for the read scenario
            if (response.IsError == false)
            {
                try
                {
                    var body = response.Payload.Value.Content;
                    var content = await body.ReadAsStringAsync();
                    var doc = JsonDocument.Parse(content);
                    if (doc.RootElement.TryGetProperty("id", out var idProp))
                        createdClaimIds.Add(idProp.GetString()!);
                }
                catch { /* best effort */ }
            }

            return response;
        })
        .WithLoadSimulations(
            Simulation.Inject(
                rate: (int)(LoadTestConfig.TargetRps * 0.3),
                interval: TimeSpan.FromSeconds(1),
                during: LoadTestConfig.SustainDuration)
        );

        var readScenario = Scenario.Create("claim_read", async context =>
        {
            // Read a previously-created claim, or hit the list endpoint
            string path;
            if (createdClaimIds.TryPeek(out var claimId))
                path = $"/api/claims/{claimId}";
            else
                path = "/api/claims?page=1&pageSize=10";

            var request = Http.CreateRequest("GET", path);

            var response = await Http.Send(client, request);
            return response;
        })
        .WithLoadSimulations(
            Simulation.Inject(
                rate: (int)(LoadTestConfig.TargetRps * 0.7),
                interval: TimeSpan.FromSeconds(1),
                during: LoadTestConfig.SustainDuration)
        );

        var stats = NBomberRunner
            .RegisterScenarios(writeScenario, readScenario)
            .WithReportFolder(LoadTestConfig.ReportFolder)
            .WithReportFormats(ReportFormat.Html, ReportFormat.Csv)
            .Run();

        foreach (var scenarioStats in stats.ScenarioStats)
        {
            var totalRequests = scenarioStats.Ok.Request.Count + scenarioStats.Fail.Request.Count;
            var errorRate = scenarioStats.Fail.Request.Count / (double)Math.Max(1, totalRequests);

            Assert.True(
                scenarioStats.Ok.Latency.Percent99 < LoadTestConfig.MaxP99Latency.TotalMilliseconds,
                $"[{scenarioStats.ScenarioName}] p99 latency {scenarioStats.Ok.Latency.Percent99}ms exceeds SLA");

            Assert.True(
                errorRate <= LoadTestConfig.MaxErrorRate,
                $"[{scenarioStats.ScenarioName}] Error rate {errorRate:P2} exceeds SLA");
        }
    }
}
