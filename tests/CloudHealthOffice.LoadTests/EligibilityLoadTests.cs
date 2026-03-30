using System.Text.Json;
using NBomber.Contracts;
using NBomber.CSharp;
using NBomber.Http.CSharp;

namespace CloudHealthOffice.LoadTests;

/// <summary>
/// Load tests for the eligibility service (270/271 inquiry processing).
///
/// Scenarios:
///   1. Eligibility inquiry throughput — POST /api/eligibility/inquiry at sustained rate
///   2. Quick eligibility check — GET /api/eligibility/check (high-frequency lookup)
///   3. Mixed read/write workload — simulates real-world usage patterns
///
/// These tests validate that the eligibility service can handle production-level
/// throughput during peak enrollment periods and real-time eligibility verification
/// at point-of-care.
/// </summary>
public class EligibilityLoadTests
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
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.Add("X-Tenant-ID", LoadTestConfig.TenantId);
        return client;
    }

    private static object GenerateInquiry(int index)
    {
        return new
        {
            tenantId = LoadTestConfig.TenantId,
            payerId = "LOAD-PAYER-001",
            payerName = "Load Test Health Plan",
            providerId = $"PROV-{index % 100:D3}",
            providerNPI = $"12345{index % 99999:D5}",
            subscriberId = $"LOAD-SUB-{index:D8}",
            subscriberFirstName = "Load",
            subscriberLastName = $"Test-{index % 1000}",
            subscriberDOB = "1985-06-15",
            subscriberGender = index % 2 == 0 ? "M" : "F",
            groupNumber = $"GRP-{index % 50:D3}",
            serviceTypeCode = "30",
            serviceDateFrom = DateTime.UtcNow.Date,
            serviceDateTo = DateTime.UtcNow.Date,
            controlNumber = $"LT-{index:D10}",
            lineOfBusiness = 1
        };
    }

    /// <summary>
    /// Scenario 1: Eligibility inquiry throughput.
    /// Simulates real-time eligibility verification at sustained rate.
    /// </summary>
    [Fact]
    public void EligibilityInquiry_SustainedLoad_MeetsSlaTargets()
    {
        var counter = 0;
        using var client = CreateClient(LoadTestConfig.EligibilityServiceUrl);

        var scenario = Scenario.Create("eligibility_inquiry", async context =>
        {
            var index = Interlocked.Increment(ref counter);
            var inquiry = GenerateInquiry(index);

            var request = Http.CreateRequest("POST", "/api/eligibility/inquiry")
                .WithHeader("Content-Type", "application/json")
                .WithBody(new StringContent(
                    JsonSerializer.Serialize(inquiry, Json),
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

        Assert.True(
            scenarioStats.Ok.Latency.Percent99 < LoadTestConfig.MaxP99Latency.TotalMilliseconds,
            $"p99 latency {scenarioStats.Ok.Latency.Percent99}ms exceeds SLA of {LoadTestConfig.MaxP99Latency.TotalMilliseconds}ms");

        var totalRequests = scenarioStats.Ok.Request.Count + scenarioStats.Fail.Request.Count;
        var errorRate = scenarioStats.Fail.Request.Count / (double)Math.Max(1, totalRequests);
        Assert.True(
            errorRate <= LoadTestConfig.MaxErrorRate,
            $"Error rate {errorRate:P2} exceeds SLA of {LoadTestConfig.MaxErrorRate:P2}");
    }

    /// <summary>
    /// Scenario 2: Quick eligibility check under high concurrency.
    /// Simulates point-of-care eligibility lookups which must be fast.
    /// </summary>
    [Fact]
    public void QuickEligibilityCheck_HighConcurrency_MeetsSlaTargets()
    {
        var counter = 0;
        using var client = CreateClient(LoadTestConfig.EligibilityServiceUrl);

        var scenario = Scenario.Create("quick_eligibility_check", async context =>
        {
            var index = Interlocked.Increment(ref counter);
            var subscriberId = $"LOAD-SUB-{index % 5000:D8}";

            var request = Http.CreateRequest("GET",
                $"/api/eligibility/check?subscriberId={subscriberId}&serviceDate={DateTime.UtcNow.Date:yyyy-MM-dd}");

            var response = await Http.Send(client, request);
            return response;
        })
        .WithLoadSimulations(
            Simulation.Inject(
                rate: LoadTestConfig.TargetRps * 2,
                interval: TimeSpan.FromSeconds(1),
                during: LoadTestConfig.SustainDuration)
        );

        var stats = NBomberRunner
            .RegisterScenarios(scenario)
            .WithReportFolder(LoadTestConfig.ReportFolder)
            .WithReportFormats(ReportFormat.Html, ReportFormat.Csv)
            .Run();

        var scenarioStats = stats.ScenarioStats[0];

        // Quick checks should be faster than full inquiries
        Assert.True(
            scenarioStats.Ok.Latency.Percent99 < LoadTestConfig.MaxP99Latency.TotalMilliseconds / 2,
            $"Quick check p99 latency {scenarioStats.Ok.Latency.Percent99}ms exceeds SLA of {LoadTestConfig.MaxP99Latency.TotalMilliseconds / 2}ms");

        var totalRequests = scenarioStats.Ok.Request.Count + scenarioStats.Fail.Request.Count;
        var errorRate = scenarioStats.Fail.Request.Count / (double)Math.Max(1, totalRequests);
        Assert.True(
            errorRate <= LoadTestConfig.MaxErrorRate,
            $"Error rate {errorRate:P2} exceeds SLA of {LoadTestConfig.MaxErrorRate:P2}");
    }
}
