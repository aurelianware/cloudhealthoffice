using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FhirService.Models;
using FhirService.Services;
using FhirService.Services.BulkExport;
using FhirService.Services.Identity;
using FluentAssertions;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CloudHealthOffice.FhirService.Tests.Controllers;

/// <summary>
/// System-level <c>/$export</c>: every member in the tenant, for the backend
/// clients named in <c>BulkExport:SystemExport:Clients</c> only, through the
/// real pipeline (SMART scope, caller resolution, the worker and the download).
/// </summary>
public class BulkExportSystemLevelTests : IClassFixture<BulkExportSystemLevelTests.Host>
{
    private const string Exporter = "plan-warehouse";
    private const string OtherIssuer = "https://other-idp.test.local";
    private const string Scopes = "system/$export.read system/*.read";

    public sealed class Host : FhirTestWebAppFactory
    {
        protected override IReadOnlyList<string> AdditionalValidIssuers => [OtherIssuer];

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // Host settings: Program.cs reads BulkExport while building.
            builder.UseSetting("BulkExport:Enabled", "true");
            builder.UseSetting("BulkExport:DocumentStore:Provider", "InMemory");
            builder.UseSetting("BulkExport:SystemExport:Clients:0:Issuer", TestIssuer);
            builder.UseSetting("BulkExport:SystemExport:Clients:0:ClientId", Exporter);
            builder.ConfigureServices(services =>
            {
                foreach (var hosted in services
                             .Where(d => d.ServiceType == typeof(IHostedService)
                                         && d.ImplementationType == typeof(BulkExportWorker))
                             .ToList())
                    services.Remove(hosted);
                services.RemoveAll<IBulkExportResourceSource>();
                services.AddSingleton<IBulkExportResourceSource, PatientOnlySource>();
            });

            // Both issuers resolve, so the caller (its issuer and client id) is known.
            builder.ConfigureTestServices(services =>
            {
                var trust = new SmartTrustOptions
                {
                    Mode = SmartTrustMode.Demo,
                    TrustedIssuers =
                    [
                        new TrustedIssuerOptions
                        {
                            Issuer = TestIssuer, Audiences = ["fhir-api"], RequireHttpsMetadata = false,
                            IssuesChoClientRegistrations = true,
                        },
                        new TrustedIssuerOptions
                        {
                            Issuer = OtherIssuer, Audiences = ["fhir-api"], RequireHttpsMetadata = false,
                        },
                    ],
                };
                trust.Validate(isDevelopmentHost: true);
                services.RemoveAll<SmartTrustOptions>();
                services.RemoveAll<TrustedIssuerRegistry>();
                services.AddSingleton(trust);
                services.AddSingleton(new TrustedIssuerRegistry(trust));
            });
        }

        public BulkExportWorker Worker() => ActivatorUtilities.CreateInstance<BulkExportWorker>(Services);
    }

    private readonly Host _host;

    public BulkExportSystemLevelTests(Host host) => _host = host;

    private HttpClient Client(string clientId = Exporter, string? issuer = null, string scopes = Scopes)
    {
        var client = _host.CreateClient();
        var token = _host.IssueToken(scopes, issuer: issuer,
            rawPayload: new Dictionary<string, object> { ["client_id"] = clientId, ["sub"] = clientId });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static HttpRequestMessage KickOff(string query = "?_type=Patient")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/fhir/r4/$export" + query);
        request.Headers.Add("Prefer", "respond-async");
        return request;
    }

    private async Task RunWorkerUntilIdleAsync()
    {
        var worker = _host.Worker();
        while (await worker.RunNextAsync())
        {
        }
    }

    [Fact]
    public async Task ANamedExporter_ExportsEveryMember_AndDownloadsTheFile()
    {
        var client = Client();
        var kickOff = await client.SendAsync(KickOff());
        kickOff.StatusCode.Should().Be(HttpStatusCode.Accepted, await kickOff.Content.ReadAsStringAsync());
        var status = kickOff.Content.Headers.ContentLocation!.ToString();

        await RunWorkerUntilIdleAsync();

        var complete = await client.GetAsync(status);
        complete.StatusCode.Should().Be(HttpStatusCode.OK);
        using var manifest = JsonDocument.Parse(await complete.Content.ReadAsStringAsync());
        manifest.RootElement.GetProperty("request").GetString().Should().EndWith("/$export?_type=Patient");
        var output = manifest.RootElement.GetProperty("output").EnumerateArray().Single();
        output.GetProperty("type").GetString().Should().Be("Patient");

        var file = await client.GetAsync(output.GetProperty("url").GetString()!);
        file.StatusCode.Should().Be(HttpStatusCode.OK);
        var lines = (await file.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(output.GetProperty("count").GetInt32());
        // Not a provider panel or a consent list: every member, attributed or not.
        lines.Should().Contain(l => l.Contains("\"id\":\"pat-001\""))
            .And.Contain(l => l.Contains("\"id\":\"pat-002\""))
            .And.Contain(l => l.Contains("\"id\":\"pat-003\""));
    }

    [Fact]
    public async Task AnotherClient_IsRefused()
        => (await Client("some-other-backend").SendAsync(KickOff()))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

    [Fact]
    public async Task TheSameClientId_FromAnotherIssuer_IsRefused()
        => (await Client(Exporter, issuer: OtherIssuer).SendAsync(KickOff()))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

    [Fact]
    public async Task AUserContextToken_ForTheNamedClient_IsRefused()
        => (await Client(Exporter, scopes: "user/*.read").SendAsync(KickOff()))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

    [Fact]
    public async Task OnlyTheExporter_CanPollItsJob()
    {
        var kickOff = await Client().SendAsync(KickOff());
        var status = kickOff.Content.Headers.ContentLocation!.ToString();

        (await Client("some-other-backend").GetAsync(status)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TheCapabilityStatement_DeclaresSystemExport()
    {
        var json = await _host.CreateClient().GetStringAsync("/fhir/r4/metadata");
        var rest = JsonDocument.Parse(json).RootElement.GetProperty("rest")[0];
        rest.GetProperty("operation").EnumerateArray()
            .Select(o => o.GetProperty("definition").GetString())
            .Should().Contain("http://hl7.org/fhir/uv/bulkdata/OperationDefinition/export");
    }

    // ── The member listing ───────────────────────────────────────────────────

    private static BulkExportJobRecord Job() => new()
    {
        TenantId = "t", JobId = "j", RequestedBy = Exporter, Level = BulkExportLevel.System,
        Types = ["Patient"], RequestUrl = "", CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Listing_ReadsEveryPage_EvenWhenTheAdapterServesSmallerPages()
    {
        // 250 members served 100 at a time, whatever page size was asked for.
        var all = Enumerable.Range(1, 250).Select(i => new Patient { Id = $"m{i:D3}" }).ToList();
        var adapter = new Mock<IFhirDataAdapter>();
        adapter.Setup(a => a.SearchPatientsAsync(It.IsAny<PatientSearchParams>(), "t", It.IsAny<CancellationToken>()))
            .ReturnsAsync((PatientSearchParams p, string _, CancellationToken _) =>
                ((IReadOnlyList<Patient>)all.Skip((p.Page - 1) * 100).Take(100).ToList(), all.Count));

        var members = await BulkExportWorker.AllMembersAsync(Job(), adapter.Object, CancellationToken.None);

        members.Should().HaveCount(250).And.BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Fact]
    public async Task Listing_Stops_WhenTheAdapterIgnoresThePageNumber()
    {
        var page = new List<Patient> { new() { Id = "m1" }, new() { Id = "m2" } };
        var adapter = new Mock<IFhirDataAdapter>();
        adapter.Setup(a => a.SearchPatientsAsync(It.IsAny<PatientSearchParams>(), "t", It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IReadOnlyList<Patient>)page, 1000));

        var members = await BulkExportWorker.AllMembersAsync(Job(), adapter.Object, CancellationToken.None);

        members.Should().Equal("m1", "m2");
    }

    private sealed class PatientOnlySource : IBulkExportResourceSource
    {
        public async IAsyncEnumerable<string> ReadAsync(
            string tenantId, string memberId, string resourceType, DateTimeOffset? since,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            if (resourceType == "Patient")
                yield return $$"""{"resourceType":"Patient","id":"{{memberId}}"}""";
        }
    }
}
