using FhirService.Models;
using FhirService.Services;
using FhirService.Services.ChoNative;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CloudHealthOffice.FhirService.Tests.ChoNative;

public class FhirSyntheticSourceRegistrationTests
{
    private const string Native = FhirAdapterSources.ChoNative;
    private static IHostEnvironment Environment(string name)
    {
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(name);
        return env.Object;
    }

    [Fact]
    public void AllNative_ProductionNeedsNoSyntheticPermissionOrLegacyProvider()
    {
        var services = new ServiceCollection();
        services.AddFhirSyntheticSources(new(Native, Native, Native, Native, Native),
            new ConfigurationBuilder().Build(), Environment("Production"));
        using var provider = services.BuildServiceProvider();
        provider.GetService<MockPatientAccessDataProvider>().Should().BeNull();
        provider.GetService<IPatientAccessDataProvider>().Should().BeNull();
        provider.GetService<IChoMemberDirectory>().Should().BeNull();
    }

    [Theory]
    [InlineData("Patient")]
    [InlineData("Coverage")]
    [InlineData("Encounter")]
    [InlineData("Claim")]
    [InlineData("PayerToPayer")]
    public void EachSelectedMock_StillBlocksProduction(string resource)
    {
        var options = new FhirAdapterOptions();
        foreach (var name in new[] { "Patient", "Coverage", "Encounter", "Claim", "PayerToPayer" })
            options.Sources[name] = name == resource ? FhirAdapterSources.Mock : Native;
        var act = () => new ServiceCollection().AddFhirSyntheticSources(FhirDataBindings.From(options),
            new ConfigurationBuilder().Build(), Environment("Production"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*" + resource + "*SyntheticData:Allowed*");
    }

    [Theory]
    [InlineData("Development", false)]
    [InlineData("Testing", false)]
    [InlineData("Production", true)]
    public void ExplicitDemoOrDevelopment_KeepsLegacyMockPayerSource(string environment, bool allow)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["SyntheticData:Allowed"] = allow.ToString() }).Build();
        var services = new ServiceCollection();
        services.AddFhirSyntheticSources(new("Mock", "Mock", "Mock", "Mock", "Mock"), config, Environment(environment));
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IPatientAccessDataProvider>().Should().BeSameAs(provider.GetRequiredService<IChoMemberDirectory>());
    }
}
