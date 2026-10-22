using EligibilityService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace CloudHealthOffice.EligibilityService.Tests;

public class TemporalAccumulatorRegistrationTests
{
    private static ServiceProvider Build(string environment, string? source = null, bool allow = false)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(environment);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TemporalEligibility:AccumulatorSource"] = source,
            ["SyntheticData:Allowed"] = allow.ToString(),
        }).Build();
        return new ServiceCollection().AddTemporalAccumulatorSource(config, env).BuildServiceProvider();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task ProductionDefault_IsExplicitlyUnavailableRatherThanSyntheticZeroes(string environment)
    {
        using var provider = Build(environment);
        var client = provider.GetRequiredService<IAccumulatorClient>();
        Assert.IsType<UnavailableAccumulatorClient>(client);
        var date = new DateTime(2024, 3, 15);
        var snapshot = await client.GetSnapshotAsync("tenant-a", "member-a", "plan-a", date);
        Assert.Equal("unavailable", snapshot.Source);
        Assert.Equal(date, snapshot.AsOfDate);
        Assert.Null(snapshot.Deductible);
        Assert.Null(snapshot.OutOfPocket);
        Assert.NotEmpty(snapshot.UnavailableReason!);
    }

    [Fact]
    public void ProductionExplicitStub_StillRequiresSyntheticPermission()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Build("Production", "Stub"));
        Assert.Contains("SyntheticData:Allowed", error.Message);
        using var provider = Build("Production", "Stub", allow: true);
        Assert.IsType<StubAccumulatorClient>(provider.GetRequiredService<IAccumulatorClient>());
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void DevelopmentDefault_PreservesLabelledStub(string environment)
    {
        using var provider = Build(environment);
        Assert.IsType<StubAccumulatorClient>(provider.GetRequiredService<IAccumulatorClient>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Http")]
    [InlineData("Native")]
    public void UnknownOrUnimplementedSources_AreNotSilentlySubstituted(string source)
        => Assert.Throws<InvalidOperationException>(() => Build("Production", source));

    [Fact]
    public async Task UnavailableSource_PreservesCancellation()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new UnavailableAccumulatorClient()
            .GetSnapshotAsync("t", "m", "p", DateTime.UtcNow, cts.Token));
    }
}
