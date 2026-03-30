using System.Text.Json;
using NBomber.Contracts;
using NBomber.CSharp;
using NBomber.Http.CSharp;

namespace CloudHealthOffice.LoadTests;

/// <summary>
/// Load tests for the authorization service (278 prior auth processing).
///
/// Scenarios:
///   1. Authorization submission throughput — POST /api/authorizations at sustained rate
///   2. Authorization validation — GET /api/authorizations/{authNumber}/validate
///
/// These tests validate that the authorization service can handle production-level
/// throughput during peak prior-auth submission periods and real-time validation
/// lookups during claims adjudication.
/// </summary>
public class AuthorizationLoadTests
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

    private static object GenerateAuthRequest(int index)
    {
        var serviceDate = DateTime.UtcNow.Date.AddDays(7);
        return new
        {
            tenantId = LoadTestConfig.TenantId,
            memberId = $"LOAD-MBR-{index:D8}",
            coverageId = $"LOAD-COV-{index:D8}",
            patientFirstName = "Load",
            patientLastName = $"Test-{index % 1000}",
            patientDateOfBirth = "1978-03-22",
            requestingProviderNPI = $"12345{index % 99999:D5}",
            requestingProviderName = $"Load Test Provider {index % 100}",
            servicingProviderNPI = "9876543210",
            servicingProviderName = "Load Test Specialist",
            serviceTypeCode = "42",
            levelOfService = "E",
            requestedServiceDateFrom = serviceDate,
            requestedServiceDateTo = serviceDate,
            diagnosisCodes = new[]
            {
                new { code = "M54.5", codeQualifier = "BK", description = "Low back pain" }
            },
            requestedServices = new[]
            {
                new
                {
                    procedureCode = "72148",
                    procedureDescription = "MRI Lumbar Spine",
                    requestedUnits = 1,
                    unitType = "UN",
                    placeOfServiceCode = "22"
                }
            },
            authorizationType = 0
        };
    }

    /// <summary>
    /// Scenario 1: Authorization submission throughput.
    /// Simulates batch prior-auth submissions during peak hours.
    /// </summary>
    [Fact]
    public void AuthorizationSubmission_SustainedLoad_MeetsSlaTargets()
    {
        var counter = 0;
        using var client = CreateClient(LoadTestConfig.AuthorizationServiceUrl);

        var scenario = Scenario.Create("auth_submission", async context =>
        {
            var index = Interlocked.Increment(ref counter);
            var authRequest = GenerateAuthRequest(index);

            var request = Http.CreateRequest("POST", "/api/authorizations")
                .WithHeader("Content-Type", "application/json")
                .WithBody(new StringContent(
                    JsonSerializer.Serialize(authRequest, Json),
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
    /// Scenario 2: Authorization validation under concurrent load.
    /// Simulates claims adjudication lookups that validate auth before processing.
    /// </summary>
    [Fact]
    public void AuthorizationValidation_HighConcurrency_MeetsSlaTargets()
    {
        // Seed: create some authorizations first, then validate them at high rate
        var authNumbers = new System.Collections.Concurrent.ConcurrentBag<string>();
        var seedCounter = 0;
        using var client = CreateClient(LoadTestConfig.AuthorizationServiceUrl);

        var seedScenario = Scenario.Create("seed_authorizations", async context =>
        {
            var index = Interlocked.Increment(ref seedCounter);
            var authRequest = GenerateAuthRequest(index);

            var request = Http.CreateRequest("POST", "/api/authorizations")
                .WithHeader("Content-Type", "application/json")
                .WithBody(new StringContent(
                    JsonSerializer.Serialize(authRequest, Json),
                    System.Text.Encoding.UTF8,
                    "application/json"));

            var response = await Http.Send(client, request);

            if (response.IsError == false)
            {
                try
                {
                    var body = await response.Payload.Value.Content.ReadAsStringAsync();
                    var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("authorizationNumber", out var numProp))
                        authNumbers.Add(numProp.GetString()!);
                }
                catch { /* best effort */ }
            }

            return response;
        })
        .WithLoadSimulations(
            Simulation.Inject(rate: 10, interval: TimeSpan.FromSeconds(1),
                during: TimeSpan.FromSeconds(5))
        );

        var validateScenario = Scenario.Create("auth_validation", async context =>
        {
            string authNumber;
            if (!authNumbers.TryPeek(out authNumber!))
                authNumber = "nonexistent";

            var request = Http.CreateRequest("GET", $"/api/authorizations/{authNumber}/validate");

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
                during: LoadTestConfig.SustainDuration)
        );

        var stats = NBomberRunner
            .RegisterScenarios(seedScenario, validateScenario)
            .WithReportFolder(LoadTestConfig.ReportFolder)
            .WithReportFormats(ReportFormat.Html, ReportFormat.Csv)
            .Run();

        var valStats = stats.ScenarioStats.First(s => s.ScenarioName == "auth_validation");

        Assert.True(
            valStats.Ok.Latency.Percent99 < LoadTestConfig.MaxP99Latency.TotalMilliseconds,
            $"Validation p99 latency {valStats.Ok.Latency.Percent99}ms exceeds SLA");

        var totalRequests = valStats.Ok.Request.Count + valStats.Fail.Request.Count;
        var errorRate = valStats.Fail.Request.Count / (double)Math.Max(1, totalRequests);
        Assert.True(
            errorRate <= LoadTestConfig.MaxErrorRate,
            $"Validation error rate {errorRate:P2} exceeds SLA");
    }
}
