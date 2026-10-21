using CloudHealthOffice.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CloudHealthOffice.Infrastructure.Tests.Security;

/// <summary>Mock and synthetic-data components are opt-in outside Development and Testing.</summary>
public class ChoSyntheticDataTests
{
    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration Config(bool? allowed = null) => new ConfigurationBuilder()
        .AddInMemoryCollection(allowed is null ? [] : new Dictionary<string, string?> { [ChoSyntheticData.AllowedSetting] = allowed.Value.ToString() })
        .Build();

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void DevelopmentAndTesting_MayUseMocks(string environment)
    {
        FluentActions.Invoking(() => ChoSyntheticData.EnsureAllowed(new Env(environment), Config(), "Mock X")).Should().NotThrow();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void ElsewhereAMock_RefusesToStart_ByDefault(string environment)
    {
        FluentActions.Invoking(() => ChoSyntheticData.EnsureAllowed(new Env(environment), Config(), "Mock X"))
            .Should().Throw<InvalidOperationException>().WithMessage("*Mock X*SyntheticData:Allowed*");
        FluentActions.Invoking(() => ChoSyntheticData.EnsureAllowed(new Env(environment), Config(allowed: false), "Mock X"))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ADeclaredSyntheticDataDemo_MayUseMocks()
    {
        FluentActions.Invoking(() => ChoSyntheticData.EnsureAllowed(new Env("Production"), Config(allowed: true), "Mock X")).Should().NotThrow();
    }
}
