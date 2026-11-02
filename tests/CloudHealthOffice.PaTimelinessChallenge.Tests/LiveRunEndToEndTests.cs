using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AuthorizationService.Models;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.Tools.PaTimelinessChallenge;

namespace CloudHealthOffice.PaTimelinessChallenge.Tests;

/// <summary>
/// The live phase over HTTP: PAS <c>$submit</c> is stood in for (it records a
/// pended request the way fhir-service's recorder does), and everything after it
/// (read by number, decision extension, the reviewer's 278 response with line
/// decisions, the read back and the live window's metrics) goes through the real
/// authorization-service pipeline.
/// </summary>
public sealed class LiveRunEndToEndTests : IClassFixture<HistoryRunEndToEndTests.Factory>
{
    private const string Tenant = "pa-timeliness";
    private readonly HistoryRunEndToEndTests.Factory _factory;

    public LiveRunEndToEndTests(HistoryRunEndToEndTests.Factory factory) => _factory = factory;

    private sealed class Router(HttpMessageHandler service, InMemoryAuthorizationRepository store) : DelegatingHandler(service)
    {
        public int Submitted;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath != "/fhir/r4/Claim/$submit") return await base.SendAsync(request, ct);
            Interlocked.Increment(ref Submitted);
            var bundle = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var resources = (bundle["entry"] as JsonArray)!.Select(e => e!["resource"]!).ToList();
            JsonNode Of(string type) => resources.Single(r => r["resourceType"]!.GetValue<string>() == type);
            var claim = Of("Claim");
            var filing = Of("Coverage")["type"]!["coding"]![0]!["code"]!.GetValue<string>();
            var caseId = claim["identifier"]![0]!["value"]!.GetValue<string>();
            var number = "PAS-" + caseId + "-" + Guid.NewGuid().ToString("N")[..6];
            await store.CreateAsync(new AuthorizationService.Models.Authorization
            {
                TenantId = Tenant,
                AuthorizationNumber = number,
                MemberId = Of("Patient")["identifier"]![0]!["value"]!.GetValue<string>(),
                RequestingProviderNPI = Of("Practitioner")["identifier"]![0]!["value"]!.GetValue<string>(),
                Status = AuthorizationStatus.Pended,
                ReviewDecision = "A4",
                LevelOfService = claim["priority"]!["coding"]![0]!["code"]!.GetValue<string>() == "stat" ? "U" : "E",
                LineOfBusiness = filing switch { "16" => LineOfBusiness.Medicare, "MC" => LineOfBusiness.Medicaid, _ => LineOfBusiness.Commercial },
                SubmittedDate = DateTime.UtcNow,
                RequestedServices = (claim["item"] as JsonArray)!.Select(i => new RequestedService
                {
                    LineNumber = i!["sequence"]!.GetValue<int>(),
                    ProcedureCode = i["productOrService"]!["coding"]![0]!["code"]!.GetValue<string>(),
                    ProductOrServiceSystem = i["productOrService"]!["coding"]![0]!["system"]!.GetValue<string>(),
                    RequestedUnits = i["quantity"]!["value"]!.GetValue<int>(),
                }).ToList(),
            });
            var response = new JsonObject
            {
                ["resourceType"] = "Bundle",
                ["type"] = "collection",
                ["entry"] = new JsonArray(new JsonObject
                {
                    ["resource"] = new JsonObject { ["resourceType"] = "ClaimResponse", ["outcome"] = "queued", ["preAuthRef"] = number },
                }),
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response.ToJsonString(), Encoding.UTF8, "application/fhir+json"),
            };
        }
    }

    [Fact]
    public async Task Live_phase_decides_extends_and_scores_through_the_real_service()
    {
        var profile = new PaCorpusProfile { Count = 400, Seed = 23, AsOf = DateTimeOffset.UtcNow, LiveFraction = 0.25 };
        var token = ChoDevelopmentAuth.UserTokenIssuer().IssueUserToken("ptc-runner", Tenant, Array.Empty<string>(), permissions: ["*:*"]);
        var router = new Router(_factory.Server.CreateHandler(), _factory.Store);
        var baseUri = new Uri("http://localhost/");
        using var http = new HttpClient(router) { BaseAddress = baseUri };
        var output = Path.Combine(Path.GetTempPath(), "ptc-live-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runner = new PaChallengeRunner(new ChoApi(http, baseUri, baseUri, Tenant, token), new PaRunOptions
            {
                AuthorizationUrl = baseUri, FhirUrl = baseUri, TenantId = Tenant, BearerToken = token,
                OutputDirectory = output, History = false, Parallelism = 4,
            });
            var summary = await runner.RunAsync(profile, CancellationToken.None);

            Assert.Equal(100, router.Submitted);
            var failed = summary.Checks.Where(c => !c.Passed).Select(c => $"{c.Area} {c.Name}: expected {c.Expected}, CHO {c.Actual}");
            Assert.True(summary.Failed == 0, string.Join("\n", failed.Concat(summary.Notes)));
            // The refusal scenario is scored, not skipped: CHO answered 409 for Commercial/Exchange.
            Assert.Contains(summary.Checks, c => c.Name == "extension" && c.Expected == "409" && c.Passed);
            Assert.Contains(summary.Checks, c => c.Name == "extension" && c.Expected == "200" && c.Passed);
            Assert.Contains(summary.Checks, c => c.Name == "lineDenialReasons" && c.Passed);
            Assert.Contains(summary.Checks, c => c.Area == "live/metrics/all" && c.Name == "totalRequests");
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }
}
