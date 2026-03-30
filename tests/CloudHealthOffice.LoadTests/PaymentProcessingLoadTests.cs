using System.Text.Json;
using NBomber.Contracts;
using NBomber.CSharp;
using NBomber.Http.CSharp;

namespace CloudHealthOffice.LoadTests;

/// <summary>
/// Load tests for the payment processing pipeline (835 ERA generation).
///
/// Scenarios:
///   1. Payment creation throughput — POST /api/payments at sustained rate
///   2. ERA (835) download under load — GET /api/payments/{id}/835
///   3. Payment run execution — POST /api/payment-runs (batch processing)
///
/// These tests validate that the payment service can handle production-level
/// throughput for batch payment runs and ERA file generation, which are
/// critical during end-of-month payment cycles.
/// </summary>
public class PaymentProcessingLoadTests
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

    private static object GeneratePayment(int index)
    {
        var providerNpi = $"12345{index % 99999:D5}";
        return new
        {
            tenantId = LoadTestConfig.TenantId,
            checkNumber = $"LOAD-CHK-{index:D8}",
            paymentMethod = "EFT",
            totalPaymentAmount = 500.00m + (index % 1000),
            paymentDate = DateTime.UtcNow,
            payerName = "Load Test Health Plan",
            payerId = "LOAD-PAYER-001",
            payeeName = $"Provider Clinic {index % 100}",
            payeeNPI = providerNpi,
            claimPayments = new[]
            {
                new
                {
                    claimId = $"LOAD-CLM-{index:D8}",
                    patientControlNumber = $"PCN-{index:D8}",
                    claimStatusCode = "1",
                    chargeAmount = 750.00m + (index % 500),
                    paymentAmount = 500.00m + (index % 1000),
                    patientResponsibilityAmount = 250.00m,
                    memberId = $"MBR-{index % 2000:D4}",
                    renderingProviderNPI = providerNpi,
                    claimReceivedDate = DateTime.UtcNow,
                    serviceLines = new[]
                    {
                        new
                        {
                            lineNumber = 1,
                            procedureCode = "99213",
                            chargeAmount = 750.00m + (index % 500),
                            paymentAmount = 500.00m + (index % 1000),
                            units = 1m,
                            serviceDateFrom = DateTime.UtcNow.Date,
                            serviceDateTo = DateTime.UtcNow.Date,
                            adjustments = Array.Empty<object>()
                        }
                    },
                    claimAdjustments = Array.Empty<object>()
                }
            },
            status = 0
        };
    }

    /// <summary>
    /// Scenario 1: Payment creation throughput.
    /// Simulates batch payment processing during end-of-month cycles.
    /// </summary>
    [Fact]
    public void PaymentCreation_BatchLoad_MeetsSlaTargets()
    {
        var counter = 0;
        using var client = CreateClient(LoadTestConfig.PaymentServiceUrl);

        var scenario = Scenario.Create("payment_creation", async context =>
        {
            var index = Interlocked.Increment(ref counter);
            var payment = GeneratePayment(index);

            var request = Http.CreateRequest("POST", "/api/payments")
                .WithHeader("Content-Type", "application/json")
                .WithBody(new StringContent(
                    JsonSerializer.Serialize(payment, Json),
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
    /// Scenario 2: 835 ERA download under concurrent load.
    /// Simulates multiple providers downloading ERA files simultaneously,
    /// which is common after payment batch runs complete.
    /// </summary>
    [Fact]
    public void EraDownload_ConcurrentAccess_MeetsSlaTargets()
    {
        // Seed: create a few payments first, then hammer ERA downloads
        var paymentIds = new System.Collections.Concurrent.ConcurrentBag<string>();
        var seedCounter = 0;
        using var client = CreateClient(LoadTestConfig.PaymentServiceUrl);

        var seedScenario = Scenario.Create("seed_payments", async context =>
        {
            var index = Interlocked.Increment(ref seedCounter);
            var payment = GeneratePayment(index);

            var request = Http.CreateRequest("POST", "/api/payments")
                .WithHeader("Content-Type", "application/json")
                .WithBody(new StringContent(
                    JsonSerializer.Serialize(payment, Json),
                    System.Text.Encoding.UTF8,
                    "application/json"));

            var response = await Http.Send(client, request);

            if (response.IsError == false)
            {
                try
                {
                    var body = await response.Payload.Value.Content.ReadAsStringAsync();
                    var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("id", out var idProp))
                        paymentIds.Add(idProp.GetString()!);
                }
                catch { /* best effort */ }
            }

            return response;
        })
        .WithLoadSimulations(
            Simulation.Inject(rate: 10, interval: TimeSpan.FromSeconds(1),
                during: TimeSpan.FromSeconds(5))
        );

        var eraScenario = Scenario.Create("era_download", async context =>
        {
            string paymentId;
            if (!paymentIds.TryPeek(out paymentId!))
                paymentId = "nonexistent";

            var request = Http.CreateRequest("GET", $"/api/payments/{paymentId}/835");

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
            .RegisterScenarios(seedScenario, eraScenario)
            .WithReportFolder(LoadTestConfig.ReportFolder)
            .WithReportFormats(ReportFormat.Html, ReportFormat.Csv)
            .Run();

        // Assert on the ERA download scenario (index 1)
        var eraStats = stats.ScenarioStats.First(s => s.ScenarioName == "era_download");

        Assert.True(
            eraStats.Ok.Latency.Percent99 < LoadTestConfig.MaxP99Latency.TotalMilliseconds,
            $"ERA p99 latency {eraStats.Ok.Latency.Percent99}ms exceeds SLA");

        var totalRequests = eraStats.Ok.Request.Count + eraStats.Fail.Request.Count;
        var errorRate = eraStats.Fail.Request.Count / (double)Math.Max(1, totalRequests);
        Assert.True(
            errorRate <= LoadTestConfig.MaxErrorRate,
            $"ERA error rate {errorRate:P2} exceeds SLA");
    }

    /// <summary>
    /// Scenario 3: Health endpoint baseline.
    /// Establishes a baseline latency for the health endpoint under load,
    /// which helps detect infrastructure degradation separate from
    /// application logic issues.
    /// </summary>
    [Fact]
    public void HealthEndpoint_HighConcurrency_Under100msP50()
    {
        using var client = CreateClient(LoadTestConfig.PaymentServiceUrl);

        var scenario = Scenario.Create("health_check", async context =>
        {
            var request = Http.CreateRequest("GET", "/health/live");
            var response = await Http.Send(client, request);
            return response;
        })
        .WithLoadSimulations(
            Simulation.Inject(
                rate: LoadTestConfig.TargetRps * 2,
                interval: TimeSpan.FromSeconds(1),
                during: TimeSpan.FromSeconds(15))
        );

        var stats = NBomberRunner
            .RegisterScenarios(scenario)
            .WithReportFolder(LoadTestConfig.ReportFolder)
            .WithReportFormats(ReportFormat.Html, ReportFormat.Csv)
            .Run();

        var scenarioStats = stats.ScenarioStats[0];

        // Health endpoint should be very fast
        Assert.True(
            scenarioStats.Ok.Latency.Percent50 < 100,
            $"Health p50 latency {scenarioStats.Ok.Latency.Percent50}ms — expected <100ms");

        var totalRequests = scenarioStats.Ok.Request.Count + scenarioStats.Fail.Request.Count;
        var errorRate = scenarioStats.Fail.Request.Count / (double)Math.Max(1, totalRequests);
        Assert.True(errorRate < 0.001, $"Health endpoint error rate {errorRate:P2} — expected near zero");
    }
}
