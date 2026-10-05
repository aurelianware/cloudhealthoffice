using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.Infrastructure.Tests.Security;

public class ChoCorsTests
{
    private static CorsResult Evaluate(string environment, string origin, Dictionary<string, string?>? config = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config ?? new()).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddChoBrowserCors(configuration, new Env(environment));
        using var provider = services.BuildServiceProvider();

        var http = new DefaultHttpContext { RequestServices = provider };
        http.Request.Method = "OPTIONS";
        http.Request.Headers.Origin = origin;
        http.Request.Headers.AccessControlRequestMethod = "GET";

        var policy = provider.GetRequiredService<ICorsPolicyProvider>().GetPolicyAsync(http, ChoCors.PolicyName).Result!;
        return provider.GetRequiredService<ICorsService>().EvaluatePolicy(http, policy);
    }

    [Fact]
    public void Production_NothingConfigured_AllowsNoOrigin()
    {
        var result = Evaluate("Production", "https://evil.example");
        result.IsOriginAllowed.Should().BeFalse();
    }

    [Fact]
    public void Production_ConfiguredOrigin_IsAllowed_OthersAreNot_AndNeverWithCredentials()
    {
        var config = new Dictionary<string, string?> { ["Cors:AllowedOrigins:0"] = "https://portal.cloudhealthoffice.com" };

        var allowed = Evaluate("Production", "https://portal.cloudhealthoffice.com", config);
        allowed.IsOriginAllowed.Should().BeTrue();
        allowed.SupportsCredentials.Should().BeFalse();

        Evaluate("Production", "https://evil.example", config).IsOriginAllowed.Should().BeFalse();
    }

    [Fact]
    public void DefaultsToThePortalOrigin()
    {
        var config = new Dictionary<string, string?> { ["Portal:BaseUrl"] = "https://portal.example.com/" };

        Evaluate("Production", "https://portal.example.com", config).IsOriginAllowed.Should().BeTrue();
        Evaluate("Production", "https://other.example.com", config).IsOriginAllowed.Should().BeFalse();
    }

    [Fact]
    public void Wildcard_OutsideDevelopment_RefusesToStart()
    {
        var config = new Dictionary<string, string?> { ["Cors:AllowedOrigins"] = "*" };
        var act = () => Evaluate("Production", "https://x.example", config);
        act.Should().Throw<InvalidOperationException>().WithMessage("*'*'*");
    }

    [Fact]
    public void PlainHttpOrigin_OutsideDevelopment_RefusesToStart()
    {
        var config = new Dictionary<string, string?> { ["Cors:AllowedOrigins"] = "http://portal.example.com" };
        var act = () => Evaluate("Staging", "http://portal.example.com", config);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Development_NothingConfigured_AllowsLocalhostOnly()
    {
        Evaluate("Development", "http://localhost:5173").IsOriginAllowed.Should().BeTrue();
        Evaluate("Development", "https://evil.example").IsOriginAllowed.Should().BeFalse();
    }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
