extern alias CoverageSvc;
extern alias EnrollmentSvc;

using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MemberService.Tests.Integration;

/// <summary>
/// Contract tests verifying that every HTTP path embedded in member-service's
/// downstream typed clients maps to a real, registered endpoint on the
/// downstream service. Catches the class of bug that broke PR #650 (path
/// pluralization mismatch between caller and callee) at build/test time
/// instead of at runtime.
/// </summary>
public class DownstreamRouteContractTests
{
    private sealed class CoverageFactory : WebApplicationFactory<CoverageSvc::CoverageService.Controllers.CoverageController>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Force Mongo branch (no Cosmos creds required). Fake host so
                    // the MongoClient constructor succeeds; no I/O runs because
                    // we replace the repository below.
                    ["MongoDb:ConnectionString"] = "mongodb://fake-contract-host:27017",
                    ["MongoDb:DatabaseName"] = "contract"
                });
            });
            builder.ConfigureServices(services =>
            {
                RemoveAll<CoverageSvc::CoverageService.Repositories.ICoverageRepository>(services);
                services.AddSingleton<CoverageSvc::CoverageService.Repositories.ICoverageRepository>(
                    new Mock<CoverageSvc::CoverageService.Repositories.ICoverageRepository>().Object);
            });
        }
    }

    private sealed class EnrollmentFactory : WebApplicationFactory<EnrollmentSvc::EnrollmentImportService.Controllers.EnrollmentController>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Enrollment-import now speaks MongoDB (migrated off Cosmos —
                    // see PR #1005). Force the Mongo branch with a fake host so
                    // the MongoClient constructor succeeds; no I/O runs because
                    // we replace the repositories below. Same pattern as
                    // CoverageFactory above.
                    ["MongoDb:ConnectionString"] = "mongodb://fake-contract-host:27017",
                    ["MongoDb:DatabaseName"] = "contract"
                });
            });
            builder.ConfigureServices(services =>
            {
                // Coverage moved from a directly-owned Mongo repository
                // (IEnrollmentRepository) to coverage-service via
                // ICoverageServiceClient — nothing left here that needs a
                // Mongo-backed mock. See EnrollmentImportService.cs's own
                // doc comments on the Member/Sponsor/Coverage delegation.
                RemoveAll<EnrollmentSvc::EnrollmentImportService.Services.IEnrollmentTransactionRepository>(services);
                RemoveAll<EnrollmentSvc::EnrollmentImportService.Repositories.IEnrollmentEventRepository>(services);
                RemoveAll<EnrollmentSvc::EnrollmentImportService.Services.IEnrollmentImportRunRepository>(services);
                services.AddSingleton<EnrollmentSvc::EnrollmentImportService.Services.IEnrollmentTransactionRepository>(
                    new Mock<EnrollmentSvc::EnrollmentImportService.Services.IEnrollmentTransactionRepository>().Object);
                services.AddSingleton<EnrollmentSvc::EnrollmentImportService.Repositories.IEnrollmentEventRepository>(
                    new Mock<EnrollmentSvc::EnrollmentImportService.Repositories.IEnrollmentEventRepository>().Object);
                services.AddSingleton<EnrollmentSvc::EnrollmentImportService.Services.IEnrollmentImportRunRepository>(
                    new Mock<EnrollmentSvc::EnrollmentImportService.Services.IEnrollmentImportRunRepository>().Object);

                // Remove the Mongo index initializer that would try to connect to
                // the fake MongoDB host at startup. Don't use RemoveAll<IHostedService>()
                // — that would also remove the Kestrel host service and prevent the
                // test server from starting. RemoveAll checks both ServiceType and
                // ImplementationType so it catches the AddHostedService<T>
                // registration (same pattern as MemberFhirSmokeTests.Factory).
                RemoveAll<EnrollmentSvc::EnrollmentImportService.HostedServices.EnrollmentIndexInitializer>(services);
            });
        }

        private static void RemoveAll<T>(IServiceCollection services)
        {
            var toRemove = services.Where(d =>
                d.ServiceType == typeof(T) || d.ImplementationType == typeof(T)).ToList();
            foreach (var d in toRemove) services.Remove(d);
        }
    }

    private static void RemoveAll<T>(IServiceCollection services)
    {
        var toRemove = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var d in toRemove) services.Remove(d);
    }

    [Fact]
    public void HttpCoverageServiceClient_PathsMapToRegisteredEndpoints()
    {
        using var factory = new CoverageFactory();
        var endpoints = GetRegisteredPatterns(factory.Services);

        var expected = new (string Verb, string PathTemplate)[]
        {
            ("GET",  "api/v1/coverage/member/{memberId}/pcp"),
            ("PUT",  "api/v1/coverage/member/{memberId}/pcp"),
            ("GET",  "api/v1/coverage/member/{memberId}/history"),
            ("POST", "api/v1/coverage/member/{memberId}/terminate"),
        };

        foreach (var (verb, template) in expected)
        {
            AssertRouteRegistered(endpoints, verb, template);
        }
    }

    [Fact]
    public void HttpEnrollmentImportServiceClient_PathsMapToRegisteredEndpoints()
    {
        using var factory = new EnrollmentFactory();
        var endpoints = GetRegisteredPatterns(factory.Services);

        var expected = new (string Verb, string PathTemplate)[]
        {
            ("GET", "api/v1/enrollment/transactions"),
            ("GET", "api/v1/members/{memberId}/enrollment-events"),
        };

        foreach (var (verb, template) in expected)
        {
            AssertRouteRegistered(endpoints, verb, template);
        }
    }

    private static List<(string Verb, string Pattern)> GetRegisteredPatterns(IServiceProvider sp)
    {
        var dataSources = sp.GetServices<EndpointDataSource>();
        var list = new List<(string, string)>();
        foreach (var ds in dataSources)
        {
            foreach (var ep in ds.Endpoints.OfType<RouteEndpoint>())
            {
                var verbs = ep.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                            ?? new[] { "GET" };
                foreach (var v in verbs)
                    list.Add((v, ep.RoutePattern.RawText ?? string.Empty));
            }
        }
        return list;
    }

    private static void AssertRouteRegistered(
        IReadOnlyList<(string Verb, string Pattern)> endpoints,
        string verb,
        string expectedTemplate)
    {
        bool match = endpoints.Any(e =>
            string.Equals(e.Verb, verb, StringComparison.OrdinalIgnoreCase) &&
            RoutePatternEquals(e.Pattern, expectedTemplate));

        match.Should().BeTrue(
            $"downstream client calls {verb} /{expectedTemplate} but no such route is registered. " +
            $"Registered routes: {string.Join(", ", endpoints.Select(e => $"{e.Verb} {e.Pattern}"))}");
    }

    private static bool RoutePatternEquals(string a, string b)
    {
        // Normalize route parameter segments: "{memberId}" ~= "{id}" — only
        // structure + literal segments are compared, not parameter names.
        static string Normalize(string p) => Regex.Replace(p.Trim('/'), "\\{[^}]+\\}", "{}")
            .Replace("//", "/");
        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }
}
