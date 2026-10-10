using System.Net;
using System.Text;
using BenefitPlanService.HealthChecks;
using BenefitPlanService.Services;
using CloudHealthOffice.BenefitEngine.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace BenefitPlanService.Tests.HealthChecks;

/// <summary>
/// The accumulator Redis must run with <c>maxmemory-policy noeviction</c>:
/// its hashes hold the per-claim commit journal and the void / denial
/// fences, which an eviction would lose.
/// </summary>
public class RedisAccumulatorEvictionHealthCheckTests
{
    private const string Info = "# Memory\r\nused_memory:1024\r\nmaxmemory:0\r\nmaxmemory_policy:{0}\r\nmem_fragmentation_ratio:1.2\r\n";

    [Theory]
    [InlineData("noeviction")]
    [InlineData("allkeys-lru")]
    [InlineData("volatile-ttl")]
    public void ParsePolicy_ReadsTheInfoLine(string policy) =>
        RedisAccumulatorEvictionHealthCheck.ParsePolicy(string.Format(Info, policy)).Should().Be(policy);

    [Fact]
    public void ParsePolicy_NoLine_IsNull() =>
        RedisAccumulatorEvictionHealthCheck.ParsePolicy("# Memory\r\nused_memory:1\r\n").Should().BeNull();

    [Theory]
    [InlineData("noeviction", false, HealthStatus.Healthy)]
    [InlineData("noeviction", true, HealthStatus.Healthy)]
    [InlineData("allkeys-lru", false, HealthStatus.Unhealthy)]
    [InlineData("volatile-lru", false, HealthStatus.Unhealthy)] // the keys carry a TTL: volatile-* evicts them too
    [InlineData("volatile-ttl", true, HealthStatus.Degraded)]
    [InlineData(null, false, HealthStatus.Degraded)]
    public void Evaluate_OnlyNoevictionIsHealthy(string? policy, bool allow, HealthStatus expected) =>
        RedisAccumulatorEvictionHealthCheck.Evaluate(policy, allow).Status.Should().Be(expected);

    private static RedisAccumulatorEvictionHealthCheck Check(string? policy, bool allow = false, Exception? fails = null)
    {
        var db = new Mock<IDatabase>();
        var setup = db.Setup(d => d.ExecuteAsync("INFO", It.IsAny<object[]>()));
        if (fails is not null) setup.ThrowsAsync(fails);
        else setup.ReturnsAsync(RedisResult.Create((RedisValue)string.Format(Info, policy)));
        var redis = new Mock<IConnectionMultiplexer>();
        redis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(db.Object);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [RedisAccumulatorEvictionHealthCheck.AllowEvictingPolicySetting] = allow ? "true" : "false",
        }).Build();
        return new RedisAccumulatorEvictionHealthCheck(redis.Object, config, NullLogger<RedisAccumulatorEvictionHealthCheck>.Instance);
    }

    [Fact]
    public async Task Check_Noeviction_IsHealthy() =>
        (await Check("noeviction").CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Healthy);

    [Fact]
    public async Task Check_AnEvictingPolicy_FailsReadiness_UnlessAllowed()
    {
        (await Check("allkeys-lru").CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Unhealthy);
        (await Check("allkeys-lru", allow: true).CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public async Task Check_RedisUnreachable_IsDegraded() =>
        (await Check(null, fails: new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"))
            .CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Degraded);
}

/// <summary>
/// claims-service's accumulator-totals with per-claim contributions: the
/// Redis rebuild journals each counted claim from them.
/// </summary>
public class ClaimsServiceAccumulatorSourceRebuildTests
{
    private sealed class Respond(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }

    [Fact]
    public async Task TheRebuild_CarriesEachCountedClaimsContribution()
    {
        const string json = """
            {
              "totals": [
                { "accumulatorType": "IndividualDeductible", "networkTier": "InNetwork", "accumulatedAmount": 150 },
                { "accumulatorType": "IndividualOutOfPocketMax", "networkTier": "InNetwork", "accumulatedAmount": 170 }
              ],
              "claims": [
                { "claimId": "A1", "totals": [
                  { "accumulatorType": "IndividualDeductible", "networkTier": "InNetwork", "accumulatedAmount": 100 },
                  { "accumulatorType": "IndividualOutOfPocketMax", "networkTier": "InNetwork", "accumulatedAmount": 120 } ] },
                { "claimId": "A2", "totals": [
                  { "accumulatorType": "IndividualDeductible", "networkTier": "InNetwork", "accumulatedAmount": 50 } ] }
              ]
            }
            """;
        var source = new ClaimsServiceAccumulatorSource(
            new HttpClient(new Respond(json)) { BaseAddress = new Uri("http://claims-service") },
            NullLogger<ClaimsServiceAccumulatorSource>.Instance);

        var rebuild = await source.CalculateAccumulatorsWithClaimsAsync("t1", "M1", AccumulatorScope.Individual, Guid.NewGuid(), "2026");

        rebuild.Success.Should().BeTrue();
        rebuild.Snapshots.Should().HaveCount(2);
        rebuild.Claims.Select(c => c.ClaimId).Should().BeEquivalentTo(["A1", "A2"]);
        var a1 = rebuild.Claims.Single(c => c.ClaimId == "A1").Updates;
        a1.Should().Contain(u => u.Type == AccumulatorType.IndividualDeductible && u.Amount == 100m && u.Source == "Deductible");
        a1.Should().Contain(u => u.Type == AccumulatorType.IndividualOutOfPocketMax && u.Amount == 120m && u.Source == "OOP");
    }

    [Fact]
    public async Task AnOlderClaimsService_WithoutPerClaimEntries_RebuildsTotalsOnly()
    {
        var source = new ClaimsServiceAccumulatorSource(
            new HttpClient(new Respond("""{"totals":[{"accumulatorType":"IndividualDeductible","networkTier":"InNetwork","accumulatedAmount":40}]}"""))
            { BaseAddress = new Uri("http://claims-service") },
            NullLogger<ClaimsServiceAccumulatorSource>.Instance);

        var rebuild = await source.CalculateAccumulatorsWithClaimsAsync("t1", "M1", AccumulatorScope.Individual, Guid.NewGuid(), "2026");

        rebuild.Success.Should().BeTrue();
        rebuild.Snapshots.Should().ContainSingle(s => s.AccumulatedAmountAfter == 40m);
        rebuild.Claims.Should().BeEmpty();
    }
}
