using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using SmartAuthService.Services;

namespace CloudHealthOffice.SmartAuth.Tests;

/// <summary>Startup refuses to run without a safe, fixed issuer.</summary>
public class SmartIssuerConfigurationTests
{
    [Theory]
    [InlineData("Production", null)]
    [InlineData("Production", "")]
    [InlineData("Production", "http://auth.cloudhealthoffice.com")]
    [InlineData("Production", "https://auth.cloudhealthoffice.com/?x=1")]
    [InlineData("Production", "https://user@auth.cloudhealthoffice.com")]
    [InlineData("Production", "not a uri")]
    [InlineData("Development", null)]
    public void Startup_RefusesAMissingOrUnsafeIssuer(string environment, string? issuer)
    {
        var act = () => SmartIssuer.Resolve(Config(issuer), new Env(environment));
        act.Should().Throw<InvalidOperationException>().WithMessage("*SmartAuth:Issuer*");
    }

    [Theory]
    [InlineData("Production", "https://auth.cloudhealthoffice.com")]
    [InlineData("Development", "http://smart-auth-service:8080")]
    public void Startup_AcceptsAnHttpsIssuer_AndHttpOnlyInDevelopment(string environment, string issuer)
        => SmartIssuer.Resolve(Config(issuer), new Env(environment)).Should().Be(new Uri(issuer));

    private static IConfiguration Config(string? issuer) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { [SmartIssuer.ConfigKey] = issuer })
        .Build();

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "smart-auth-service";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
