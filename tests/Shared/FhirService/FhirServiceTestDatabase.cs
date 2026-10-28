// Shared by every test project that boots fhir-service through
// WebApplicationFactory. Linked into each project with
// <Compile Include="..\Shared\FhirService\*.cs" />.
using CloudHealthOffice.ProviderEnrollmentService.Abstractions;
using CloudHealthOffice.ProviderEnrollmentService.Gates;
using FhirService.Services;
using FhirService.Services.PayerToPayer.Ingestion;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

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

            // The Mongo DTR store creates its indexes at construction, and
            // DtrService seeds the default questionnaire library through it.
            services.RemoveAll<IDtrResourceStore>();
            services.AddSingleton<IDtrResourceStore, InMemoryDtrResourceStore>();

            // Payer-to-Payer consents accepted from requesting payers (ADR 022).
            services.RemoveAll<global::FhirService.Services.PayerToPayer.IPayerToPayerConsentAttestationStore>();
            services.AddSingleton<global::FhirService.Services.PayerToPayer.IPayerToPayerConsentAttestationStore,
                global::FhirService.Services.PayerToPayer.InMemoryPayerToPayerConsentAttestationStore>();

            // The CMS-0057-F API usage ledger, written on every patient-context read.
            services.RemoveAll<global::FhirService.Services.ApiUsage.IApiDisclosureLedger>();
            services.AddSingleton<global::FhirService.Services.ApiUsage.IApiDisclosureLedger,
                global::FhirService.Services.ApiUsage.InMemoryApiDisclosureLedger>();
        });

        return builder;
    }
}
