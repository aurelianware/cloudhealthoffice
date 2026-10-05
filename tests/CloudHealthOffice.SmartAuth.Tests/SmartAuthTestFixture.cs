using CloudHealthOffice.Testing.Mongo;
using EphemeralMongo;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Server.AspNetCore;

namespace CloudHealthOffice.SmartAuth.Tests;

/// <summary>
/// xUnit class fixture that starts an EphemeralMongo runner (in-process MongoDB 7)
/// and provides a WebApplicationFactory for smart-auth-service configured to use it.
///
/// Using EphemeralMongo instead of mocking OpenIddict stores lets the real
/// OpenIddictSeedWorker seed scopes and clients, so integration tests can exercise
/// the full SMART on FHIR authorization flow without a live MongoDB deployment.
///
/// Configuration injection note: Program.cs reads MongoDb:ConnectionString via
/// builder.Configuration as a local variable before WebApplicationFactory's
/// ConfigureAppConfiguration callbacks run, so ConfigureAppConfiguration alone
/// cannot inject it.  Setting the env var MongoDb__ConnectionString (double
/// underscore = section separator) BEFORE the factory creates the host ensures
/// WebApplicationBuilder picks it up during its own CreateBuilder() call.
/// </summary>
/// <summary>
/// Every class that drives smart-auth-service shares ONE fixture: one mongod
/// and one host. Separate class fixtures would each set the process-wide
/// MongoDb__ConnectionString variable and race each other's hosts.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SmartAuthCollection : ICollectionFixture<SmartAuthTestFixture>
{
    public const string Name = "SmartAuthService";
}

public sealed class SmartAuthTestFixture : IDisposable
{
    private const string MongoDbConnectionStringEnvVar = "MongoDb__ConnectionString";
    private readonly IMongoRunner _runner;

    public WebApplicationFactory<SmartAuthService.Program> Factory { get; }

    public SmartAuthTestFixture()
    {
        _runner = MongoRunner.Run(TestMongo.Options());

        // Must be set before the factory creates the host (which happens lazily on
        // first CreateClient() / Services access).  The env var is read by
        // WebApplicationBuilder.CreateBuilder() early enough to propagate to
        // builder.Configuration["MongoDb:ConnectionString"] in Program.cs.
        Environment.SetEnvironmentVariable(MongoDbConnectionStringEnvVar, _runner.ConnectionString);

        Factory = new WebApplicationFactory<SmartAuthService.Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Development");

                // Disable the transport-security (HTTPS) requirement so that
                // OpenIddict endpoints like /.well-known/openid-configuration and
                // /connect/authorize are reachable over plain HTTP in the test server.
                b.ConfigureServices(services =>
                    services.PostConfigure<OpenIddictServerAspNetCoreOptions>(options =>
                        options.DisableTransportSecurityRequirement = true));
            });
    }

    public void Dispose()
    {
        try
        {
            Factory.Dispose();
        }
        finally
        {
            Environment.SetEnvironmentVariable(MongoDbConnectionStringEnvVar, null);
            // No catch: a mongod that cannot be stopped must fail the run, not leak.
            _runner.Dispose();
        }
    }
}
