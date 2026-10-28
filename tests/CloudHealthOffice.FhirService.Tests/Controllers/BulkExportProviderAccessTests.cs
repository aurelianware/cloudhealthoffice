using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FhirService.Services.BulkExport;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CloudHealthOffice.FhirService.Tests.Controllers;

/// <summary>
/// Bulk <c>$export</c> for Provider Access (G-34) through the real pipeline:
/// SMART scope, tenant, the requester check, the worker and the token-gated
/// download. The test subject <c>test-user</c> is attributed pat-001..003 with
/// Provider Access consent in <see cref="FhirTestWebAppFactory"/>.
/// </summary>
public class BulkExportProviderAccessTests : IClassFixture<BulkExportProviderAccessTests.Host>
{
    private const string Scopes = "system/Group.read system/$export.read system/*.read";

    public sealed class Host : FhirTestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // Host settings: Program.cs reads BulkExport:Enabled while building.
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
                services.RemoveAll<IBulkExportResourceSource>();
                services.AddSingleton<IBulkExportResourceSource, PatientOnlySource>();
            });
        }

        public BulkExportWorker Worker() => ActivatorUtilities.CreateInstance<BulkExportWorker>(Services);
    }

    private readonly Host _host;

    public BulkExportProviderAccessTests(Host host) => _host = host;

    private HttpClient Client(string? subject = null)
    {
        var client = _host.CreateClient();
        var token = _host.IssueToken(Scopes, rawPayload: subject is null
            ? null
            : new Dictionary<string, object> { ["sub"] = subject });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static HttpRequestMessage KickOff(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("Prefer", "respond-async");
        return request;
    }

    private async Task<string> StartAsync(HttpClient client, string query = "?_type=Patient")
    {
        var response = await client.SendAsync(KickOff("/fhir/r4/Group/test-user/$export" + query));
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        return response.Content.Headers.ContentLocation?.ToString()
               ?? response.Headers.GetValues("Content-Location").Single();
    }

    [Fact]
    public async Task TheCapabilityStatement_DeclaresPatientAndGroupExport_AndNoSystemExport()
    {
        // Inferno's Bulk Data suite reads these before it kicks anything off.
        var json = await _host.CreateClient().GetStringAsync("/fhir/r4/metadata");
        var rest = JsonDocument.Parse(json).RootElement.GetProperty("rest")[0];

        static IEnumerable<string?> Ops(JsonElement element)
            => element.TryGetProperty("operation", out var ops)
                ? ops.EnumerateArray().Select(o => o.GetProperty("definition").GetString())
                : [];
        JsonElement Resource(string type)
            => rest.GetProperty("resource").EnumerateArray().Single(r => r.GetProperty("type").GetString() == type);

        Ops(Resource("Patient")).Should().Contain("http://hl7.org/fhir/uv/bulkdata/OperationDefinition/patient-export");
        Ops(Resource("Group")).Should().Contain("http://hl7.org/fhir/uv/bulkdata/OperationDefinition/group-export");
        Ops(rest).Should().NotContain("http://hl7.org/fhir/uv/bulkdata/OperationDefinition/export");
    }

    [Fact]
    public async Task ProviderExportsItsOwnPanel_AndDownloadsTheFile()
    {
        var client = Client();
        var status = await StartAsync(client);

        (await client.GetAsync(status)).StatusCode.Should().Be(HttpStatusCode.Accepted);

        await RunWorkerUntilIdleAsync();

        var complete = await client.GetAsync(status);
        complete.StatusCode.Should().Be(HttpStatusCode.OK);
        complete.Content.Headers.Expires.Should().NotBeNull();
        using var manifest = JsonDocument.Parse(await complete.Content.ReadAsStringAsync());
        manifest.RootElement.GetProperty("requiresAccessToken").GetBoolean().Should().BeTrue();
        manifest.RootElement.GetProperty("request").GetString().Should().EndWith("/Group/test-user/$export?_type=Patient");
        var output = manifest.RootElement.GetProperty("output").EnumerateArray().Single();
        output.GetProperty("type").GetString().Should().Be("Patient");
        output.GetProperty("count").GetInt32().Should().Be(3);

        var fileUrl = output.GetProperty("url").GetString()!;
        fileUrl.Should().Contain("/fhir/r4/$export-file/");

        var file = await client.GetAsync(fileUrl);
        file.StatusCode.Should().Be(HttpStatusCode.OK);
        file.Content.Headers.ContentType!.MediaType.Should().Be("application/fhir+ndjson");
        var lines = (await file.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(3);
        lines.Should().OnlyContain(l => l.Contains("\"resourceType\":\"Patient\""));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task PatientExport_IsTheProvidersPanel(string method)
    {
        // Patient/$export is every patient the caller may export: for a
        // provider, the same attributed panel as its own Group.
        var client = Client();
        var request = new HttpRequestMessage(new HttpMethod(method), "/fhir/r4/Patient/$export?_type=Patient");
        request.Headers.Add("Prefer", "respond-async");
        var kickOff = await client.SendAsync(request);
        kickOff.StatusCode.Should().Be(HttpStatusCode.Accepted, await kickOff.Content.ReadAsStringAsync());
        var status = kickOff.Content.Headers.ContentLocation!.ToString();

        await RunWorkerUntilIdleAsync();

        using var manifest = JsonDocument.Parse(await (await client.GetAsync(status)).Content.ReadAsStringAsync());
        manifest.RootElement.GetProperty("request").GetString().Should().EndWith("/Patient/$export?_type=Patient");
        var output = manifest.RootElement.GetProperty("output").EnumerateArray().Single();
        output.GetProperty("type").GetString().Should().Be("Patient");
        output.GetProperty("count").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task GroupExport_CanBeStartedWithGet()
    {
        // The Bulk Data IG's kick-off is a GET; POST is the optional form.
        var request = new HttpRequestMessage(HttpMethod.Get, "/fhir/r4/Group/test-user/$export?_type=Patient");
        request.Headers.Add("Prefer", "respond-async");
        (await Client().SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task AnotherCaller_CannotPollCancelOrDownload()
    {
        var owner = Client();
        var status = await StartAsync(owner);
        await RunWorkerUntilIdleAsync();
        using var manifest = JsonDocument.Parse(await (await owner.GetAsync(status)).Content.ReadAsStringAsync());
        var fileUrl = manifest.RootElement.GetProperty("output")[0].GetProperty("url").GetString()!;

        var other = Client(subject: "another-provider");
        (await other.GetAsync(status)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.GetAsync(fileUrl)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.DeleteAsync(status)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await owner.GetAsync(fileUrl)).StatusCode.Should().Be(HttpStatusCode.OK, "the other caller's cancel did nothing");
    }

    [Fact]
    public async Task AnotherProvidersGroup_IsNotFound()
    {
        var response = await Client().SendAsync(KickOff("/fhir/r4/Group/someone-else/$export"));
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UnsupportedType_IsBadRequest()
    {
        var response = await Client().SendAsync(KickOff("/fhir/r4/Group/test-user/$export?_type=Observation"));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SystemLevelExport_StillNotImplemented()
    {
        var response = await Client().SendAsync(KickOff("/fhir/r4/$export"));
        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
    }

    [Fact]
    public async Task PatientBoundToken_CannotStartAnExport()
    {
        var client = _host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _host.IssueToken("patient/*.read", patientId: "pat-001"));
        var response = await client.SendAsync(KickOff("/fhir/r4/Group/test-user/$export"));
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.SendAsync(KickOff("/fhir/r4/Patient/$export"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PatientExport_NeedsPatientReadNotJustGroupRead()
    {
        // Patient/$export is scoped as a read of Patient (system/Patient.read
        // or a wildcard), as the Bulk Data IG scopes it.
        var client = _host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _host.IssueToken("system/Group.read system/$export.read"));
        (await client.SendAsync(KickOff("/fhir/r4/Patient/$export"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Cancel_RemovesTheExport()
    {
        var client = Client();
        var status = await StartAsync(client);
        await RunWorkerUntilIdleAsync();

        (await client.DeleteAsync(status)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await client.GetAsync(status)).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "Bulk Data 2.0: a cancelled export's status URL is 404");

        await _host.Worker().PurgeAsync();
        (await client.GetAsync(status)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task RunWorkerUntilIdleAsync()
    {
        var worker = _host.Worker();
        while (await worker.RunNextAsync())
        {
        }
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
