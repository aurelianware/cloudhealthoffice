// Shared by every test project that boots fhir-service through
// WebApplicationFactory. Linked into each project with
// <Compile Include="..\Shared\FhirService\*.cs" />.
using CloudHealthOffice.ProviderEnrollmentService.Abstractions;
using CloudHealthOffice.ProviderEnrollmentService.Gates;
using FhirService.Models;
using FhirService.Services;
using FhirService.Services.PayerToPayer.Ingestion;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Testing.FhirService;

/// <summary>
/// fhir-service requires MongoDB in every environment (ADR 017) and refuses to
/// start without <c>MongoDb:ConnectionString</c>. A test host that never
/// exercises the database gives it a connection string to a port nothing listens
/// on, and swaps the Mongo-backed registrations it would otherwise reach for
/// in-process test doubles. A test that genuinely exercises Mongo uses
/// EphemeralMongo instead (tests/Shared/EphemeralMongo).
/// </summary>
public static class FhirServiceTestDatabase
{
    /// <summary>
    /// Nothing listens on port 1, and the short timeouts make any accidental
    /// use fail in milliseconds rather than stall the test run.
    /// </summary>
    public const string UnreachableConnectionString =
        "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=100&connectTimeoutMS=100";

    /// <summary>
    /// Points the host at <see cref="UnreachableConnectionString"/> and replaces
    /// what would touch it. Registered through <c>ConfigureServices</c>, which
    /// runs after Program.cs and before any test's own <c>ConfigureServices</c>
    /// or <c>ConfigureTestServices</c>, so a test that substitutes one of these
    /// services itself still wins.
    /// </summary>
    public static IWebHostBuilder UseUnreachableMongoWithInMemoryStores(this IWebHostBuilder builder)
    {
        // Program.cs reads the connection string while building, before
        // ConfigureAppConfiguration sources apply, so it goes in as a host setting.
        builder.UseSetting("MongoDb:ConnectionString", UnreachableConnectionString);

        builder.ConfigureServices(services =>
        {
            // The PA rule seeder and the provider-enrollment workers would reach
            // for Mongo at startup.
            foreach (var hosted in services
                         .Where(d => d.ServiceType == typeof(IHostedService)
                                     && (d.ImplementationType?.Namespace?.StartsWith(
                                             "CloudHealthOffice.PriorAuthRuleEngine", StringComparison.Ordinal) == true
                                         || d.ImplementationType?.Namespace?.StartsWith(
                                             "CloudHealthOffice.ProviderEnrollmentService", StringComparison.Ordinal) == true))
                         .ToList())
            {
                services.Remove(hosted);
            }

            // PasAutoAdjudicator Rule 0: the Mongo-backed gate reads tenant
            // enrollment config; the passthrough one decides nothing.
            services.RemoveAll<IEnrollmentDecisionGate>();
            services.AddSingleton<IEnrollmentDecisionGate, PassthroughEnrollmentGate>();

            // The Payer-to-Payer import store, which is also the clinical serving
            // and backfill store (resolved through those interfaces from this one).
            services.RemoveAll<IPayerToPayerImportRepository>();
            services.AddSingleton<IPayerToPayerImportRepository, InMemoryPayerToPayerImportRepository>();

            // DtrService opens Mongo (and creates its indexes) when it sees a
            // connection string; constructed without one it keeps its seeded
            // questionnaires in process.
            services.RemoveAll<IDtrService>();
            services.AddSingleton<IDtrService>(sp => new DtrService(
                sp.GetRequiredService<IOptions<DtrConfig>>(),
                sp.GetRequiredService<ILogger<DtrService>>(),
                new ConfigurationBuilder().Build()));
        });

        return builder;
    }
}
