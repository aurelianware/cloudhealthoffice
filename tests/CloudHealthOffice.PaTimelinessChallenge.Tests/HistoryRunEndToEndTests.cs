using AuthorizationService.Repositories;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.PriorAuthRuleEngine.Persistence;
using CloudHealthOffice.Tools.PaTimelinessChallenge;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;

namespace CloudHealthOffice.PaTimelinessChallenge.Tests;

/// <summary>
/// The history phase end to end over HTTP against the real authorization-service
/// pipeline (authentication, permissions, import, facts, metrics, UM report,
/// annual draft), with an in-memory store. Proves the runner's request and
/// response contract; the deployed run proves the rest.
/// </summary>
public sealed class HistoryRunEndToEndTests : IClassFixture<HistoryRunEndToEndTests.Factory>
{
    private const string Tenant = "pa-timeliness";
    private readonly Factory _factory;

    public HistoryRunEndToEndTests(Factory factory) => _factory = factory;

    public sealed class Factory : WebApplicationFactory<AuthorizationService.Controllers.AuthorizationsController>
    {
        public InMemoryAuthorizationRepository Store { get; } = new(() => DateTime.UtcNow);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("MongoDb:ConnectionString", "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=100&connectTimeoutMS=100");
            builder.ConfigureServices(services =>
            {
                foreach (var d in services.Where(d => d.ServiceType == typeof(IAuthorizationRepository)
                    || d.ServiceType == typeof(IInpatientStayRepository) || d.ServiceType == typeof(IPaRuleRepository)
                    || d.ServiceType.FullName?.Contains("Mongo") == true || d.ImplementationType?.FullName?.Contains("Mongo") == true).ToList())
                    services.Remove(d);
                services.PostConfigure<HealthCheckServiceOptions>(o =>
                {
                    foreach (var check in o.Registrations.Where(r => r.Name == "mongodb").ToList()) o.Registrations.Remove(check);
                });
                services.AddSingleton<IAuthorizationRepository>(Store);
                services.AddSingleton(Substitute.For<IPaReportingPackageRepository>());
                services.AddSingleton(Substitute.For<IInpatientStayRepository>());
                services.AddSingleton(Substitute.For<IPaRuleRepository>());
            });
        }
    }

    [Fact]
    public async Task History_phase_scores_clean_against_the_real_service()
    {
        var asOf = DateTimeOffset.UtcNow;
        var profile = new PaCorpusProfile
        {
            Count = 600,
            Seed = 91,
            AsOf = asOf,
            PeriodStart = asOf.AddDays(-300),
            PeriodEnd = asOf.AddDays(-PaCorpusProfile.MinimumSettlingDays),
        };
        var token = ChoDevelopmentAuth.UserTokenIssuer().IssueUserToken("ptc-runner", Tenant, Array.Empty<string>(), permissions: ["*:*"]);
        var http = _factory.CreateClient();
        var baseUri = http.BaseAddress!;
        var output = Path.Combine(Path.GetTempPath(), "ptc-e2e-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runner = new PaChallengeRunner(new ChoApi(http, baseUri, baseUri, Tenant, token), new PaRunOptions
            {
                AuthorizationUrl = baseUri, FhirUrl = baseUri, TenantId = Tenant, BearerToken = token,
                OutputDirectory = output, Live = false, Parallelism = 4,
            });
            var summary = await runner.RunAsync(profile, CancellationToken.None);

            var failed = summary.Checks.Where(c => !c.Passed).Select(c => $"{c.Area} {c.Name}: expected {c.Expected}, CHO {c.Actual}");
            Assert.True(summary.Failed == 0, string.Join("\n", failed.Concat(summary.Notes)));
            Assert.Contains(summary.Checks, c => c.Area == "history/timeliness");
            Assert.Contains(summary.Checks, c => c.Area.StartsWith("history/annual/", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(output, "run-summary.md")));
            Assert.True(File.Exists(Path.Combine(output, "corpus", "answer-key.json")));

            // A rerun is idempotent: same records, same facts, same score.
            var again = await runner.RunAsync(profile, CancellationToken.None);
            Assert.Equal(0, again.Failed);
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }
}
