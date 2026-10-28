using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CloudHealthOffice.FhirService.Tests.PayerToPayer;
using CloudHealthOffice.FhirService.Tests.Security;
using FhirService.Services.BulkExport;
using FhirService.Services.PayerToPayer;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SecurityClaim = System.Security.Claims.Claim;

namespace CloudHealthOffice.FhirService.Tests.Controllers;

/// <summary>
/// Payer-to-Payer bulk transfer (G-35) through the real pipeline: the explicit
/// <c>system/PayerToPayer.read</c> grant, the client's registered payer, the
/// consents that payer recorded (ADR 022), the worker, and the token-gated
/// download. Each member goes through the single-member exchange, so what is
/// exported is what <c>$member-data-export</c> would send.
/// </summary>
public class BulkExportPayerToPayerTests : IClassFixture<BulkExportPayerToPayerTests.Host>
{
    private const string Scope = "system/PayerToPayer.read";

    public sealed class Host : PayerToPayerWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("BulkExport:Enabled", "true");
            builder.UseSetting("BulkExport:DocumentStore:Provider", "InMemory");
            builder.ConfigureServices(services =>
            {
                // The tests run the worker themselves, one pass at a time.
                foreach (var hosted in services
                             .Where(d => d.ServiceType == typeof(IHostedService)
                                         && d.ImplementationType == typeof(BulkExportWorker))
                             .ToList())
                    services.Remove(hosted);
            });
        }

        public BulkExportWorker Worker() => ActivatorUtilities.CreateInstance<BulkExportWorker>(Services);
    }

    private readonly Host _host;

    public BulkExportPayerToPayerTests(Host host) => _host = host;

    private string PayerToken(string payer = "NEW-PLAN", string client = "new-plan-backend", string scopes = Scope)
        => _host.IssueToken(scopes, patientId: null, tenantId: "test-tenant",
            extraClaims: [new SecurityClaim("client_id", client), new SecurityClaim("payer_id", payer)]);

    private HttpClient Client(string token)
    {
        var client = _host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static HttpRequestMessage KickOff(string query = "")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/fhir/r4/PayerToPayer/$export" + query);
        request.Headers.Add("Prefer", "respond-async");
        return request;
    }

    /// <summary>The requesting payer records the member's consent the way PDex has it: with an export.</summary>
    private async Task RecordConsentAsync(string token, string memberId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/fhir/r4/PayerToPayer/$member-data-export")
        {
            Content = new StringContent(P2PTestConsent.Serialize(new { memberId }, token), Encoding.UTF8, "application/json"),
        };
        (await Client(token).SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task RunWorkerUntilIdleAsync()
    {
        var worker = _host.Worker();
        while (await worker.RunNextAsync())
        {
        }
    }

    [Fact]
    public async Task APayerExportsTheMembersWhoseConsentNamesIt_AndNoOneElse()
    {
        var token = PayerToken();
        await RecordConsentAsync(token, "pat-002");
        // Another payer's consent for another member: not NEW-PLAN's to export.
        await RecordConsentAsync(PayerToken("OTHER-PLAN", "other-plan-backend"), "pat-003");

        var client = Client(token);
        var kickOff = await client.SendAsync(KickOff());
        kickOff.StatusCode.Should().Be(HttpStatusCode.Accepted, await kickOff.Content.ReadAsStringAsync());
        var status = kickOff.Content.Headers.ContentLocation!.ToString();

        await RunWorkerUntilIdleAsync();

        var complete = await client.GetAsync(status);
        complete.StatusCode.Should().Be(HttpStatusCode.OK);
        using var manifest = JsonDocument.Parse(await complete.Content.ReadAsStringAsync());
        manifest.RootElement.GetProperty("request").GetString().Should().EndWith("/PayerToPayer/$export");
        var outputs = manifest.RootElement.GetProperty("output").EnumerateArray().ToList();
        outputs.Select(o => o.GetProperty("type").GetString()).Should().StartWith("Patient")
            .And.OnlyContain(t => t == "Patient" || t == "Coverage" || t == "ExplanationOfBenefit");

        var patients = await (await client.GetAsync(outputs[0].GetProperty("url").GetString())).Content.ReadAsStringAsync();
        var lines = patients.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().ContainSingle();
        JsonDocument.Parse(lines[0]).RootElement.GetProperty("id").GetString().Should().Be("pat-002");
        lines[0].Should().NotContain(":null", "nulls are not valid FHIR");
    }

    [Fact]
    public async Task TypesOutsideThePayerToPayerExport_AreRefused()
    {
        var client = Client(PayerToken());
        (await client.SendAsync(KickOff("?_type=Encounter"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.SendAsync(KickOff("?_type=Patient,Coverage"))).StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task OnlyARegisteredPayerBackendWithTheP2PScope_CanStartOne()
    {
        // A wildcard is what a Provider Access bulk client holds: not a payer grant.
        (await Client(PayerToken(scopes: "system/*.read")).SendAsync(KickOff()))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // A member's own token.
        (await Client(_host.IssueToken("patient/*.read", "pat-002")).SendAsync(KickOff()))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // A backend client registered to no payer.
        var unregistered = _host.IssueToken(Scope, patientId: null, tenantId: "test-tenant",
            extraClaims: [new SecurityClaim("client_id", "no-payer-backend")]);
        (await Client(unregistered).SendAsync(KickOff())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AnotherPayer_CannotPollTheExport()
    {
        var kickOff = await Client(PayerToken()).SendAsync(KickOff());
        var status = kickOff.Content.Headers.ContentLocation!.ToString();

        (await Client(PayerToken("OTHER-PLAN", "other-plan-backend")).GetAsync(status))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AConsentThatHasEnded_LeavesTheMemberOut()
    {
        var store = _host.Services.GetRequiredService<IPayerToPayerConsentAttestationStore>();
        await store.RecordAsync(new PayerToPayerConsentAttestation
        {
            TenantId = "test-tenant",
            MemberId = "pat-001",
            ReceivingPayerId = "ENDED-PLAN",
            Policy = "sensitive",
            PeriodStartUtc = DateTime.UtcNow.AddYears(-2),
            PeriodEndUtc = DateTime.UtcNow.AddDays(-1),
            ConsentFingerprint = "ended",
            RecordedAtUtc = DateTime.UtcNow.AddYears(-2),
            RecordedBy = "test",
        });

        var client = Client(PayerToken("ENDED-PLAN", "ended-plan-backend"));
        var status = (await client.SendAsync(KickOff())).Content.Headers.ContentLocation!.ToString();
        await RunWorkerUntilIdleAsync();

        using var manifest = JsonDocument.Parse(await (await client.GetAsync(status)).Content.ReadAsStringAsync());
        manifest.RootElement.GetProperty("output").GetArrayLength().Should().Be(0);
    }
}
