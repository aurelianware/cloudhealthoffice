using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
/// PDex bulk Payer-to-Payer (G-35) through the real pipeline:
/// <c>Group/$bulk-member-match</c> sorting members into the PDex Groups, the
/// Groups read back by the payer they were made for, and the matched Group
/// exported with <c>Group/{id}/$davinci-data-export</c>.
/// </summary>
public class PayerToPayerBulkMemberMatchTests : IClassFixture<PayerToPayerBulkMemberMatchTests.Host>
{
    private const string Scope = "system/PayerToPayer.read";
    private const string MatchPath = "/fhir/r4/Group/$bulk-member-match";

    public sealed class Host : PayerToPayerWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("BulkExport:Enabled", "true");
            builder.UseSetting("BulkExport:DocumentStore:Provider", "InMemory");
            builder.UseSetting("BulkExport:MaxMembersPerMatch", "3");
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

    public PayerToPayerBulkMemberMatchTests(Host host) => _host = host;

    private string PayerToken(string payer, string? client = null, string scopes = Scope)
        => _host.IssueToken(scopes, patientId: null, tenantId: "test-tenant",
            extraClaims: [new SecurityClaim("client_id", client ?? $"{payer}-backend"), new SecurityClaim("payer_id", payer)]);

    private HttpClient Client(string token)
    {
        var client = _host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>One MemberBundle, the member named by its member (MB) identifier.</summary>
    private static JsonObject MemberBundle(string memberId, JsonObject? consent)
    {
        var parts = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "MemberPatient",
                ["resource"] = new JsonObject
                {
                    ["resourceType"] = "Patient",
                    ["id"] = "submitted",
                    ["meta"] = new JsonObject { ["lastUpdated"] = "2026-10-01T00:00:00Z" },
                    ["identifier"] = new JsonArray(new JsonObject
                    {
                        ["type"] = new JsonObject
                        {
                            ["coding"] = new JsonArray(new JsonObject
                            {
                                ["system"] = "http://terminology.hl7.org/CodeSystem/v2-0203", ["code"] = "MB",
                            }),
                        },
                        ["value"] = memberId,
                    }),
                },
            },
            new JsonObject
            {
                ["name"] = "CoverageToMatch",
                ["resource"] = new JsonObject
                {
                    ["resourceType"] = "Coverage",
                    ["status"] = "active",
                    ["beneficiary"] = new JsonObject { ["reference"] = "Patient/submitted" },
                    ["payor"] = new JsonArray(new JsonObject { ["display"] = "Cloud Health Office Plan" }),
                },
            },
        };
        if (consent is not null)
            parts.Add(new JsonObject { ["name"] = "Consent", ["resource"] = consent.DeepClone() });
        return new JsonObject { ["name"] = "MemberBundle", ["part"] = parts };
    }

    private static JsonObject Request(params JsonObject[] bundles)
        => new() { ["resourceType"] = "Parameters", ["parameter"] = new JsonArray(bundles) };

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, JsonObject body, bool async = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, MatchPath)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/fhir+json"),
        };
        if (async) request.Headers.Add("Prefer", "respond-async");
        return await client.SendAsync(request);
    }

    private static JsonObject? Group(JsonNode parameters, string name)
        => (parameters["parameter"] as JsonArray)!.OfType<JsonObject>()
            .FirstOrDefault(p => (string?)p["name"] == name)?["resource"] as JsonObject;

    private static IEnumerable<string?> References(JsonObject group)
        => (group["member"] as JsonArray ?? []).OfType<JsonObject>().Select(m => (string?)m["entity"]!["reference"]);

    private async Task<JsonNode> MatchAsync(string payer, params JsonObject[] bundles)
    {
        var response = await PostAsync(Client(PayerToken(payer)), Request(bundles));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    [Fact]
    public async Task EachMember_IsSortedIntoMatched_NotMatched_OrConsentConstrained()
    {
        var answer = await MatchAsync("BULK-PLAN",
            MemberBundle("pat-002", P2PTestConsent.For("BULK-PLAN", "pat-002")),
            MemberBundle("no-such-member", P2PTestConsent.For("BULK-PLAN", "no-such-member")),
            // Matched, but the consent names another payer as recipient.
            MemberBundle("pat-001", P2PTestConsent.For("SOMEONE-ELSE", "pat-001")));

        var matched = Group(answer, "MatchedMembers")!;
        ((string?)matched["code"]!["coding"]![0]!["code"]).Should().Be("match");
        ((string?)matched["characteristic"]![0]!["valueReference"]!["identifier"]!["value"]).Should().Be("BULK-PLAN");
        References(matched).Should().Equal("Patient/pat-002");
        var contained = matched["contained"]![0]!;
        ((string?)contained["identifier"]![0]!["value"]).Should().Be("pat-002", "the submitted Patient is echoed");
        contained["meta"].Should().BeNull("a contained resource carries no lastUpdated");

        var notMatched = Group(answer, "NonMatchedMembers")!;
        ((string?)notMatched["code"]!["coding"]![0]!["code"]).Should().Be("nomatch");
        References(notMatched).Should().ContainSingle().Which.Should().StartWith("#");

        var constrained = Group(answer, "ConsentConstrainedMembers")!;
        ((string?)constrained["code"]!["coding"]![0]!["code"]).Should().Be("consentconstraint");
        References(constrained).Should().ContainSingle().Which.Should().StartWith("#");
        constrained.ToJsonString().Should().NotContain("RecipientNotBound", "why the consent failed is not said");

        // Only the accepted consent was recorded for the payer.
        var store = _host.Services.GetRequiredService<IPayerToPayerConsentAttestationStore>();
        (await store.ListMembersInForceAsync("test-tenant", "BULK-PLAN", DateTime.UtcNow)).Should().Equal("pat-002");
    }

    [Fact]
    public async Task OnlyMatchedMembers_AlwaysAnswered_EvenWhenEmpty()
    {
        var answer = await MatchAsync("EMPTY-PLAN", MemberBundle("no-such-member", P2PTestConsent.For("EMPTY-PLAN", "x")));

        References(Group(answer, "MatchedMembers")!).Should().BeEmpty();
        Group(answer, "ConsentConstrainedMembers").Should().BeNull();
    }

    [Fact]
    public async Task AMissingConsent_ConstrainsThatMember_NotTheRequest()
    {
        var answer = await MatchAsync("PARTIAL-PLAN",
            MemberBundle("pat-002", P2PTestConsent.For("PARTIAL-PLAN", "pat-002")),
            MemberBundle("pat-003", consent: null));

        References(Group(answer, "MatchedMembers")!).Should().Equal("Patient/pat-002");
        References(Group(answer, "ConsentConstrainedMembers")!).Should().ContainSingle();
    }

    [Fact]
    public async Task AsyncRequest_IsAnsweredAtItsStatusUrl_ToTheCallerOnly()
    {
        var client = Client(PayerToken("ASYNC-PLAN"));
        var kickOff = await PostAsync(client,
            Request(MemberBundle("pat-002", P2PTestConsent.For("ASYNC-PLAN", "pat-002"))), async: true);
        kickOff.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var status = kickOff.Headers.TryGetValues("Content-Location", out var values)
            ? values.Single()
            : kickOff.Content.Headers.ContentLocation!.ToString();

        var answer = await client.GetAsync(status);
        answer.StatusCode.Should().Be(HttpStatusCode.OK);
        References(Group(JsonNode.Parse(await answer.Content.ReadAsStringAsync())!, "MatchedMembers")!)
            .Should().Equal("Patient/pat-002");

        (await Client(PayerToken("OTHER-PLAN")).GetAsync(status))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AGroup_IsReadOnlyByThePayerClientItWasMadeFor()
    {
        var answer = await MatchAsync("READ-PLAN", MemberBundle("pat-002", P2PTestConsent.For("READ-PLAN", "pat-002")));
        var groupId = (string)Group(answer, "MatchedMembers")!["id"]!;

        var read = await Client(PayerToken("READ-PLAN")).GetAsync($"/fhir/r4/Group/{groupId}");
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        ((string?)JsonNode.Parse(await read.Content.ReadAsStringAsync())!["id"]).Should().Be(groupId);

        (await Client(PayerToken("OTHER-PLAN")).GetAsync($"/fhir/r4/Group/{groupId}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        // A Provider Access bulk client's wildcard is not the payer grant.
        (await Client(PayerToken("READ-PLAN", scopes: "system/*.read")).GetAsync($"/fhir/r4/Group/{groupId}"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task TheMatchedGroup_ExportsItsMembers()
    {
        var token = PayerToken("EXPORT-PLAN");
        var answer = await MatchAsync("EXPORT-PLAN",
            MemberBundle("pat-002", P2PTestConsent.For("EXPORT-PLAN", "pat-002")),
            MemberBundle("no-such-member", P2PTestConsent.For("EXPORT-PLAN", "x")));
        var matched = (string)Group(answer, "MatchedMembers")!["id"]!;
        var notMatched = (string)Group(answer, "NonMatchedMembers")!["id"]!;

        var client = Client(token);
        var kickOff = await client.SendAsync(Export(matched, "?exportType=hl7.fhir.us.davinci-pdex%23payertopayer"));
        kickOff.StatusCode.Should().Be(HttpStatusCode.Accepted, await kickOff.Content.ReadAsStringAsync());
        var status = kickOff.Content.Headers.ContentLocation!.ToString();

        var worker = _host.Worker();
        while (await worker.RunNextAsync())
        {
        }

        using var manifest = JsonDocument.Parse(await (await client.GetAsync(status)).Content.ReadAsStringAsync());
        manifest.RootElement.GetProperty("request").GetString().Should().Contain($"/Group/{matched}/$davinci-data-export");
        var patients = manifest.RootElement.GetProperty("output").EnumerateArray()
            .Single(o => o.GetProperty("type").GetString() == "Patient");
        var lines = (await (await client.GetAsync(patients.GetProperty("url").GetString())).Content.ReadAsStringAsync())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().ContainSingle();
        JsonDocument.Parse(lines[0]).RootElement.GetProperty("id").GetString().Should().Be("pat-002");

        // Only the matched Group exports.
        (await client.SendAsync(Export(notMatched, "?exportType=hl7.fhir.us.davinci-pdex%23payertopayer")))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?exportType=hl7.fhir.us.davinci-pdex%23provider-delta")]
    [InlineData("?exportType=hl7.fhir.us.davinci-pdex%23payertopayer&_typeFilter=ExplanationOfBenefit%3Fservice-date%3Dge2024-01-01")]
    [InlineData("?exportType=hl7.fhir.us.davinci-pdex%23payertopayer&_until=2026-01-01T00:00:00Z")]
    public async Task AnExportItCannotHonour_IsRefused(string query)
    {
        var answer = await MatchAsync("REFUSE-PLAN", MemberBundle("pat-002", P2PTestConsent.For("REFUSE-PLAN", "pat-002")));
        var matched = (string)Group(answer, "MatchedMembers")!["id"]!;

        (await Client(PayerToken("REFUSE-PLAN")).SendAsync(Export(matched, query)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AnotherPayer_CannotExportTheGroup()
    {
        var answer = await MatchAsync("OWNER-PLAN", MemberBundle("pat-002", P2PTestConsent.For("OWNER-PLAN", "pat-002")));
        var matched = (string)Group(answer, "MatchedMembers")!["id"]!;

        (await Client(PayerToken("THIEF-PLAN")).SendAsync(Export(matched, "?exportType=hl7.fhir.us.davinci-pdex%23payertopayer")))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AMalformedOrOversizedRequest_IsRefusedWhole()
    {
        var client = Client(PayerToken("SHAPE-PLAN"));
        (await PostAsync(client, new JsonObject { ["resourceType"] = "Patient" }))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await PostAsync(client, Request(new JsonObject { ["name"] = "MemberBundle", ["part"] = new JsonArray() })))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var consent = P2PTestConsent.For("SHAPE-PLAN", "pat-002");
        (await PostAsync(client, Request(Enumerable.Range(0, 4).Select(_ => MemberBundle("pat-002", consent)).ToArray())))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task OnlyARegisteredPayerBackendWithTheP2PScope_MayMatch()
    {
        var body = Request(MemberBundle("pat-002", P2PTestConsent.For("NEW-PLAN", "pat-002")));
        (await PostAsync(Client(PayerToken("NEW-PLAN", scopes: "system/*.read")), body))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PostAsync(Client(_host.IssueToken("patient/*.read", "pat-002")), body))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var unregistered = _host.IssueToken(Scope, patientId: null, tenantId: "test-tenant",
            extraClaims: [new SecurityClaim("client_id", "no-payer-backend")]);
        (await PostAsync(Client(unregistered), body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task TheStatement_AdvertisesGroupWithTheBulkOperations()
    {
        using var statement = JsonDocument.Parse(await _host.CreateClient().GetStringAsync("/fhir/r4/metadata"));
        var group = statement.RootElement.GetProperty("rest")[0].GetProperty("resource").EnumerateArray()
            .Single(r => r.GetProperty("type").GetString() == "Group");
        group.GetProperty("interaction").EnumerateArray().Select(i => i.GetProperty("code").GetString())
            .Should().Equal("read");
        group.GetProperty("operation").EnumerateArray().Select(o => o.GetProperty("name").GetString())
            .Should().BeEquivalentTo(["export", "bulk-member-match", "davinci-data-export"]);
    }

    private static HttpRequestMessage Export(string groupId, string query)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/fhir/r4/Group/{groupId}/$davinci-data-export{query}");
        request.Headers.Add("Prefer", "respond-async");
        return request;
    }
}
