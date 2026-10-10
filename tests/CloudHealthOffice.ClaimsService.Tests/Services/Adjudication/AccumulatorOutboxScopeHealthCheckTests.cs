using ClaimsService.Services.Adjudication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication;

/// <summary>
/// The outbox dispatcher scans only the base claims database: with
/// tenant-scoped Mongo databases its retries would never reach a tenant's
/// entries, so the service reports Degraded (and logs at critical on start).
/// </summary>
public class AccumulatorOutboxScopeHealthCheckTests
{
    [Theory]
    [InlineData(false, true, HealthStatus.Healthy)]
    [InlineData(true, false, HealthStatus.Healthy)]
    [InlineData(true, true, HealthStatus.Degraded)]
    public void Evaluate(bool tenantScoping, bool dispatcherEnabled, HealthStatus expected) =>
        Assert.Equal(expected, AccumulatorOutboxScopeHealthCheck.Evaluate(tenantScoping, dispatcherEnabled).Status);

    [Fact]
    public async Task ReadsTheTenantScopingSetting()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AccumulatorOutboxScopeHealthCheck.TenantScopingSetting] = "true",
        }).Build();
        var check = new AccumulatorOutboxScopeHealthCheck(config, Options.Create(new AccumulatorOutboxOptions()));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("UseTenantScoping", result.Description);
    }
}
