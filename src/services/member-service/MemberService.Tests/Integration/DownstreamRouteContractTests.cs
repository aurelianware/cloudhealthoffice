using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
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
    private sealed class CoverageFactory : WebApplicationFactory<CoverageService.Controllers.CoverageController>
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
                RemoveAll<CoverageService.Repositories.ICoverageRepository>(services);
                services.AddSingleton<CoverageService.Repositories.ICoverageRepository>(
                    new Mock<CoverageService.Repositories.ICoverageRepository>().Object);
            });
        }
    }

    private sealed class EnrollmentFactory : WebApplicationFactory<EnrollmentImportService.Controllers.EnrollmentController>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Enrollment-import only speaks Cosmos; emulator defaults
                    // keep the CosmosClient constructor happy. Repositories are
                    // replaced below so no actual I/O happens.
                    ["CosmosDb:Endpoint"] = "https://localhost:8081",
                    ["CosmosDb:Key"] = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw=="
                });
            });
            builder.ConfigureServices(services =>
            {
                RemoveAll<EnrollmentImportService.Services.IEnrollmentRepository>(services);
                RemoveAll<EnrollmentImportService.Services.IEnrollmentTransactionRepository>(services);
                services.AddSingleton<EnrollmentImportService.Services.IEnrollmentRepository>(
                    new Mock<EnrollmentImportService.Services.IEnrollmentRepository>().Object);
                services.AddSingleton<EnrollmentImportService.Services.IEnrollmentTransactionRepository>(
                    new Mock<EnrollmentImportService.Services.IEnrollmentTransactionRepository>().Object);
            });
        }

        private static void RemoveAll<T>(IServiceCollection services)
        {
            var toRemove = services.Where(d => d.ServiceType == typeof(T)).ToList();
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

        AssertRouteRegistered(endpoints, "GET", "api/v1/enrollment/transactions");
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
