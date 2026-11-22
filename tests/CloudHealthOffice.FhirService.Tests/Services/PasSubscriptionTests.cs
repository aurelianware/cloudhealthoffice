using System.Net;
using System.Text.Json.Nodes;
using FhirService.Services.Subscriptions;
using FluentAssertions;
using Hl7.Fhir.Model;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CloudHealthOffice.FhirService.Tests.Services;

/// <summary>
/// PAS pended-decision Subscriptions (R4 backport): what CHO accepts, the
/// handshake that activates a Subscription, and the notification it sends.
/// </summary>
public class PasSubscriptionTests
{
    private const string Base = "http://cho-fhir-service:8080/fhir/r4";

    [Fact]
    public void ThePasTopicOnAnAllowedEndpoint_IsAccepted_AsRequested()
    {
        var (record, refusal) = Service(out _).Parse(Subscription(), "t1", "s1");

        refusal.Should().BeNull();
        record!.Status.Should().Be("requested");
        record.OrgIdentifier.Should().Be("1234567893");
        record.PayloadContent.Should().Be("full-resource");
        record.Headers.Should().HaveCount(2);
        JsonNode.Parse(record.Resource)!["status"]!.GetValue<string>().Should().Be("requested");
    }

    [Theory]
    [InlineData("criteria", "http://example.org/OtherTopic")]
    [InlineData("endpoint", "http://attacker.example/notify")]
    [InlineData("endpoint", "http://user:pw@inferno-pas:4567/notify")]
    [InlineData("type", "websocket")]
    public void AnythingElse_IsRefused(string field, string value)
    {
        var subscription = Subscription();
        switch (field)
        {
            case "criteria": subscription["criteria"] = value; break;
            case "endpoint": subscription["channel"]!["endpoint"] = value; break;
            case "type": subscription["channel"]!["type"] = value; break;
        }

        Service(out _).Parse(subscription, "t1", "s1").Refusal.Should().NotBeNull();
    }

    [Fact]
    public void AnHttpEndpoint_IsRefusedOutsideDevelopment()
        => Service(out _, development: false).Parse(Subscription(), "t1", "s1").Refusal.Should().NotBeNull();

    [Fact]
    public async System.Threading.Tasks.Task AnAcceptedHandshake_ActivatesTheSubscription()
    {
        var service = Service(out var handler);
        var (record, _) = service.Parse(Subscription(), "t1", "s1");

        await service.HandshakeAsync(record!, Base, CancellationToken.None);

        record!.Status.Should().Be("active");
        var sent = JsonNode.Parse(handler.Bodies.Single())!;
        var status = sent["entry"]![0]!;
        status["resource"]!["resourceType"]!.GetValue<string>().Should().Be("Parameters");
        status["request"]!["url"]!.GetValue<string>().Should().EndWith("Subscription/s1/$status");
        Parameter(status, "type").Should().Be("handshake");
        handler.Authorization.Single().Should().Be("Bearer demo");
    }

    [Fact]
    public async System.Threading.Tasks.Task ARefusedHandshake_LeavesTheSubscriptionInError()
    {
        var service = Service(out var handler);
        handler.Status = HttpStatusCode.InternalServerError;
        var (record, _) = service.Parse(Subscription(), "t1", "s1");

        await service.HandshakeAsync(record!, Base, CancellationToken.None);

        record!.Status.Should().Be("error");
    }

    [Fact]
    public async System.Threading.Tasks.Task ADecision_IsSentToTheProvidersSubscription_WithThePasResponseBundle()
    {
        var service = Service(out var handler);
        var (record, _) = service.Parse(Subscription(), "t1", "s1");
        record!.Status = "active";
        await service.Store.SaveAsync(record, CancellationToken.None);

        await service.NotifyAsync(new PasPendedWatch("t1", "PA-1", "1234567893", DateTimeOffset.UtcNow),
            Decision(), Base, CancellationToken.None);
        await service.NotifyAsync(new PasPendedWatch("t1", "PA-2", "9999999999", DateTimeOffset.UtcNow),
            Decision(), Base, CancellationToken.None);

        var sent = JsonNode.Parse(handler.Bodies.Single())!;
        Parameter(sent["entry"]![0]!, "type").Should().Be("event-notification");
        var focus = sent["entry"]![0]!["resource"]!["parameter"]!.AsArray()
            .Single(p => p!["name"]!.GetValue<string>() == "notification-event")!["part"]!.AsArray()
            .Single(p => p!["name"]!.GetValue<string>() == "focus")!["valueReference"]!["reference"]!.GetValue<string>();
        // PAS: the focus is the PAS Response Bundle, holding the ClaimResponse.
        focus.Should().Be($"{Base}/Bundle/decision-1");
        var focusEntry = sent["entry"]!.AsArray().Single(e => e!["fullUrl"]!.GetValue<string>() == focus)!;
        focusEntry["resource"]!["resourceType"]!.GetValue<string>().Should().Be("Bundle");
        focusEntry["resource"]!["type"]!.GetValue<string>().Should().Be("collection");
        focusEntry["resource"]!["entry"]![0]!["resource"]!["resourceType"]!.GetValue<string>().Should().Be("ClaimResponse");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? Parameter(JsonNode statusEntry, string name)
        => statusEntry["resource"]!["parameter"]!.AsArray()
            .FirstOrDefault(p => p!["name"]!.GetValue<string>() == name)?["valueCode"]?.GetValue<string>();

    private static Bundle Decision() => new()
    {
        Id = "decision-1",
        Type = Bundle.BundleType.Collection,
        Entry =
        [
            new() { FullUrl = "urn:uuid:cr-1", Resource = new ClaimResponse { Id = "cr-1" } },
            new() { FullUrl = "urn:uuid:pat-1", Resource = new Patient { Id = "pat-1" } },
        ],
    };

    private static JsonObject Subscription() => JsonNode.Parse("""
        {
          "resourceType": "Subscription",
          "status": "requested",
          "end": "2099-12-31T23:59:59Z",
          "reason": "PAS",
          "criteria": "http://hl7.org/fhir/us/davinci-pas/SubscriptionTopic/PASSubscriptionTopic",
          "_criteria": { "extension": [ {
            "url": "http://hl7.org/fhir/uv/subscriptions-backport/StructureDefinition/backport-filter-criteria",
            "valueString": "org-identifier=1234567893" } ] },
          "channel": {
            "type": "rest-hook",
            "endpoint": "http://inferno-pas:4567/custom/notify",
            "payload": "application/fhir+json",
            "_payload": { "extension": [ {
              "url": "http://hl7.org/fhir/uv/subscriptions-backport/StructureDefinition/backport-payload-content",
              "valueCode": "full-resource" } ] },
            "header": [ "Authorization: Bearer stale", "Authorization: Bearer demo" ]
          }
        }
        """)!.AsObject();

    private static PasSubscriptionService Service(out RecordingHandler handler, bool development = true)
    {
        handler = new RecordingHandler();
        var options = new PasSubscriptionOptions { Enabled = true, AllowedEndpointHosts = ["inferno-pas"] };
        return new PasSubscriptionService(
            new InMemoryPasSubscriptionStore(), new StaticOptions(options), new Factory(handler),
            new Environment(development ? Environments.Development : Environments.Production),
            NullLogger<PasSubscriptionService>.Instance);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public List<string> Bodies { get; } = [];
        public List<string> Authorization { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            Authorization.Add(request.Headers.Authorization?.ToString() ?? "");
            return new HttpResponseMessage(Status);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StaticOptions(PasSubscriptionOptions value) : IOptionsMonitor<PasSubscriptionOptions>
    {
        public PasSubscriptionOptions CurrentValue => value;
        public PasSubscriptionOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<PasSubscriptionOptions, string?> listener) => null;
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "fhir-service";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
