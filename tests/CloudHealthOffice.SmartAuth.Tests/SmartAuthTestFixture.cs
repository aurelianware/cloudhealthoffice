using EphemeralMongo;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace CloudHealthOffice.SmartAuth.Tests;

/// <summary>
/// xUnit class fixture that starts an EphemeralMongo runner (in-process MongoDB 7)
/// and provides a WebApplicationFactory for smart-auth-service configured to use it.
///
/// Using EphemeralMongo instead of mocking OpenIddict stores lets the real
/// OpenIddictSeedWorker seed scopes and clients, so integration tests can exercise
/// the full SMART on FHIR authorization flow without a live MongoDB deployment.
/// </summary>
public sealed class SmartAuthTestFixture : IDisposable
{
    private readonly IMongoRunner _runner;

    public WebApplicationFactory<SmartAuthService.Program> Factory { get; }

    public SmartAuthTestFixture()
    {
        _runner = MongoRunner.Run(new MongoRunnerOptions
        {
            // Keep connection timeout reasonable for CI
            ConnectionTimeout = TimeSpan.FromSeconds(30)
        });

        Factory = new WebApplicationFactory<SmartAuthService.Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Development");

                // Inject the ephemeral MongoDB connection string so smart-auth-service
                // picks up the MongoDB path in Program.cs (and OpenIddict uses real stores).
                b.ConfigureAppConfiguration(config =>
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["MongoDb:ConnectionString"] = _runner.ConnectionString
                    }));
            });
    }

    public void Dispose()
    {
        Factory.Dispose();
        _runner.Dispose();
    }
}
