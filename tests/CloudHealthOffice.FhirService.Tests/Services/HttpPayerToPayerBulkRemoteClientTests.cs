using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FhirService.Services.PayerToPayer.Outbound;
using FhirService.Services.PayerToPayer.Outbound.Bulk;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.FhirService.Tests.Services;

/// <summary>
/// The outbound bulk Payer-to-Payer transport (G-35): what it puts on the
/// wire, and which of the URLs a peer hands back it will follow. The job
/// above it is covered by the CMS-0057-F acceptance suite at the same seam.
/// </summary>
public class HttpPayerToPayerBulkRemoteClientTests
{
    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string? Auth, string? Prefer, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method, request.RequestUri!,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("Prefer", out var prefer) ? prefer.Single() : null,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
            return responder(request);
        }
    }

    private sealed class Tokens : IPayerToPayerCredentialProvider
    {
        public int Issued { get; private set; }
        public int Invalidated { get; private set; }

        public Task<string?> GetAccessTokenAsync(PayerToPayerEndpoint endpoint, CancellationToken ct = default)
            => Task.FromResult<string?>($"token-{++Issued}");

        public void Invalidate(PayerToPayerEndpoint endpoint) => Invalidated++;
    }

    private static readonly PayerToPayerEndpoint Endpoint = new()
    {
        PayerId = "PRIOR-PLAN",
        EndpointKey = "prior-plan-fhir",
        MemberMatchUri = new Uri("https://prior-payer.test/fhir/r4/Patient/$member-match"),
        MemberDataExportUri = new Uri("https://prior-payer.test/fhir/r4/PayerToPayer/$member-data-export"),
        FhirBaseUri = new Uri("https://prior-payer.test/fhir/r4/"),
        BulkFileHosts = ["files.prior-payer-storage.test"],
    };

    private static (HttpPayerToPayerBulkRemoteClient Client, RecordingHandler Handler, Tokens Tokens) Build(
        Func<HttpRequestMessage, HttpResponseMessage> responder, long maxFileBytes = 64L * 1024 * 1024)
    {
        var handler = new RecordingHandler(responder);
        var factory = new Moq.Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(HttpPayerToPayerRemoteClient.HttpClientName)).Returns(() => new HttpClient(handler));
        var tokens = new Tokens();
        var client = new HttpPayerToPayerBulkRemoteClient(
            factory.Object, tokens, Options.Create(new PayerToPayerBulkOutboundOptions { MaxFileBytes = maxFileBytes }),
            NullLogger<HttpPayerToPayerBulkRemoteClient>.Instance);
        return (client, handler, tokens);
    }

    private static HttpResponseMessage Accepted(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent(string.Empty) };
        response.Content.Headers.ContentLocation = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    [Fact]
    public async Task TheMatch_IsPostedAsyncToTheDirectoryBase_WithTheToken()
    {
        var (client, handler, _) = Build(_ => Accepted("https://prior-payer.test/fhir/r4/match-status/7"));

        var response = await client.SubmitMatchAsync(Endpoint, new JsonObject { ["resourceType"] = "Parameters" });

        response.State.Should().Be(BulkRemoteState.Accepted);
        response.StatusUrl.Should().Be(new Uri("https://prior-payer.test/fhir/r4/match-status/7"));
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.Should().Be(new Uri("https://prior-payer.test/fhir/r4/Group/$bulk-member-match"));
        request.Prefer.Should().Be("respond-async");
        request.Auth.Should().Be("Bearer token-1");
        request.Body.Should().Contain("Parameters");
    }

    [Fact]
    public async Task ARelativeStatusUrl_IsResolvedAgainstTheBase()
    {
        var (client, _, _) = Build(_ => Accepted("match-status/7"));

        var response = await client.SubmitMatchAsync(Endpoint, new JsonObject());

        response.StatusUrl.Should().Be(new Uri("https://prior-payer.test/fhir/r4/match-status/7"));
    }

    [Theory]
    [InlineData("https://elsewhere.test/status/7")]
    [InlineData("http://prior-payer.test/fhir/r4/status/7")]
    [InlineData("https://prior-payer.test:8443/status/7")]
    [InlineData("https://user:pw@prior-payer.test/status/7")]
    public async Task AStatusUrlOffThePayersOrigin_IsRefused(string location)
    {
        var (client, _, _) = Build(_ => Accepted(location));

        var response = await client.SubmitMatchAsync(Endpoint, new JsonObject());

        response.State.Should().Be(BulkRemoteState.Failed);
        response.Outcome.Should().Be(RemoteCallOutcome.InvalidResponse);
    }

    [Fact]
    public async Task ThePoll_IsNotMadeOffOrigin()
    {
        var (client, handler, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var response = await client.PollAsync(Endpoint, new Uri("https://elsewhere.test/status/7"));

        response.State.Should().Be(BulkRemoteState.Failed);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task TheExport_IsKickedOffOnTheGroup_ForThePayerToPayerExportType()
    {
        var (client, handler, _) = Build(_ => Accepted("https://prior-payer.test/fhir/r4/export-status/9"));

        var response = await client.KickOffExportAsync(Endpoint, "matched-1");

        response.State.Should().Be(BulkRemoteState.Accepted);
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Get);
        request.Uri.AbsoluteUri.Should().Be(
            "https://prior-payer.test/fhir/r4/Group/matched-1/$davinci-data-export?exportType=hl7.fhir.us.davinci-pdex%23payertopayer");
        request.Prefer.Should().Be("respond-async");
    }

    [Theory]
    [InlineData("../admin")]
    [InlineData("a/b")]
    [InlineData("x?y=1")]
    [InlineData("")]
    public async Task AGroupIdThatIsNotAFhirId_IsNotPutInAPath(string groupId)
    {
        var (client, handler, _) = Build(_ => Accepted("https://prior-payer.test/fhir/r4/export-status/9"));

        var response = await client.KickOffExportAsync(Endpoint, groupId);

        response.State.Should().Be(BulkRemoteState.Failed);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task APollStillRunning_CarriesTheRetryAfter()
    {
        var (client, _, _) = Build(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Accepted);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(42));
            return response;
        });

        var answer = await client.PollAsync(Endpoint, new Uri("https://prior-payer.test/fhir/r4/export-status/9"));

        answer.State.Should().Be(BulkRemoteState.InProgress);
        answer.RetryAfter.Should().Be(TimeSpan.FromSeconds(42));
    }

    [Fact]
    public async Task AFileOnThePayersOrigin_IsFetchedWithTheToken()
    {
        var (client, handler, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"resourceType":"Patient","id":"1"}"""),
        });

        var answer = await client.DownloadAsync(Endpoint, new Uri("https://prior-payer.test/files/1.ndjson"), requiresAccessToken: true);

        answer.State.Should().Be(BulkRemoteState.Completed);
        handler.Requests.Single().Auth.Should().Be("Bearer token-1");
    }

    [Fact]
    public async Task AFileOnAListedStorageHost_IsFetchedWithoutTheToken()
    {
        var (client, handler, tokens) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });

        var answer = await client.DownloadAsync(
            Endpoint, new Uri("https://files.prior-payer-storage.test/export/1.ndjson?sig=abc"), requiresAccessToken: true);

        answer.State.Should().Be(BulkRemoteState.Completed);
        handler.Requests.Single().Auth.Should().BeNull("CHO's token is for the payer, not its storage");
        tokens.Issued.Should().Be(0);
    }

    [Theory]
    [InlineData("https://attacker.test/1.ndjson")]
    [InlineData("http://files.prior-payer-storage.test/1.ndjson")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    public async Task AFileAnywhereElse_IsNotFetched(string url)
    {
        var (client, handler, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });

        var answer = await client.DownloadAsync(Endpoint, new Uri(url), requiresAccessToken: true);

        answer.State.Should().Be(BulkRemoteState.Failed);
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(4096, BulkRemoteState.Completed)]
    [InlineData(1024, BulkRemoteState.Failed)]
    public async Task AFileOverTheCap_IsRefused(long cap, BulkRemoteState expected)
    {
        var (client, _, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', 2048)),
        }, maxFileBytes: cap);

        (await client.DownloadAsync(Endpoint, new Uri("https://prior-payer.test/files/1.ndjson"), true))
            .State.Should().Be(expected);
    }

    [Fact]
    public async Task A401_DropsTheTokenAndTriesOnceMore()
    {
        var calls = 0;
        var (client, handler, tokens) = Build(_ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : Accepted("https://prior-payer.test/fhir/r4/match-status/7"));

        var response = await client.SubmitMatchAsync(Endpoint, new JsonObject());

        response.State.Should().Be(BulkRemoteState.Accepted);
        tokens.Invalidated.Should().Be(1);
        handler.Requests.Select(r => r.Auth).Should().Equal("Bearer token-1", "Bearer token-2");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, RemoteCallOutcome.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError, RemoteCallOutcome.Unavailable)]
    [InlineData(HttpStatusCode.NotFound, RemoteCallOutcome.InvalidResponse)]
    public async Task AKickOffRefusal_IsAnOutcome(HttpStatusCode status, RemoteCallOutcome outcome)
    {
        var (client, _, _) = Build(_ => new HttpResponseMessage(status));

        var response = await client.SubmitMatchAsync(Endpoint, new JsonObject());

        response.State.Should().Be(BulkRemoteState.Failed);
        response.Outcome.Should().Be(outcome);
    }
}
