using System.Net;
using FhirService.Services.PayerToPayer.Outbound;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.FhirService.Tests.Services;

/// <summary>
/// Transport-level behaviour of the outbound Payer-to-Payer client (P2P-02):
/// how a peer's HTTP status becomes a structured outcome, and what the client
/// puts on the wire. The orchestration above it is covered by the CMS-0057-F
/// acceptance suite against the same <see cref="IPayerToPayerRemoteClient"/>
/// seam; these tests pin the seam's own contract.
/// </summary>
public class HttpPayerToPayerRemoteClientTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; }
            = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"resourceType\":\"Bundle\"}"),
            };

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var copy = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var h in request.Headers) copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
            Requests.Add(copy);
            return Task.FromResult(Responder(request));
        }
    }

    private static readonly PayerToPayerEndpoint Endpoint = new()
    {
        PayerId = "PRIOR-PLAN",
        EndpointKey = "prior-plan-fhir",
        MemberMatchUri = new Uri("https://prior-payer.test/fhir/r4/Patient/$member-match"),
        MemberDataExportUri = new Uri("https://prior-payer.test/fhir/r4/PayerToPayer/$member-data-export"),
    };

    private static (HttpPayerToPayerRemoteClient Client, RecordingHandler Handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage>? responder = null,
        IPayerToPayerCredentialProvider? credentials = null)
    {
        var handler = new RecordingHandler();
        if (responder is not null) handler.Responder = responder;

        var factory = new Moq.Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(HttpPayerToPayerRemoteClient.HttpClientName))
            .Returns(new HttpClient(handler));

        var client = new HttpPayerToPayerRemoteClient(
            factory.Object,
            credentials ?? new UnconfiguredPayerToPayerCredentialProvider(),
            Options.Create(new PayerToPayerTransportOptions()),
            NullLogger<HttpPayerToPayerRemoteClient>.Instance);

        return (client, handler);
    }

    private static RemoteMemberMatchRequest MatchRequest() => new()
    {
        ReceivingPayerId = "cloud-health-office",
        MemberId = "SUB-1001",
        FamilyName = "Smith",
        BirthDate = "1955-07-14",
    };

    [Theory]
    // 422 is the anti-enumeration "did not resolve to a single member" signal a
    // conformant peer returns (the convention CHO itself follows inbound).
    [InlineData(HttpStatusCode.UnprocessableEntity, RemoteCallOutcome.NoMatch)]
    [InlineData(HttpStatusCode.Conflict, RemoteCallOutcome.Ambiguous)]
    [InlineData(HttpStatusCode.Unauthorized, RemoteCallOutcome.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, RemoteCallOutcome.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError, RemoteCallOutcome.Unavailable)]
    [InlineData(HttpStatusCode.BadGateway, RemoteCallOutcome.Unavailable)]
    [InlineData(HttpStatusCode.TooManyRequests, RemoteCallOutcome.Unavailable)]
    // A 404 on a POST to an operation endpoint means the route/base URL is
    // wrong far more often than it means "no such member" — reporting it as a
    // no-match would hide a directory misconfiguration behind a plausible
    // clinical answer. A redirect is not followed, so it is not actionable either.
    [InlineData(HttpStatusCode.NotFound, RemoteCallOutcome.InvalidResponse)]
    [InlineData(HttpStatusCode.MovedPermanently, RemoteCallOutcome.InvalidResponse)]
    [InlineData(HttpStatusCode.BadRequest, RemoteCallOutcome.InvalidResponse)]
    public async Task PeerStatus_MapsToTheStructuredOutcome(HttpStatusCode status, RemoteCallOutcome expected)
    {
        var (client, _) = Build(_ => new HttpResponseMessage(status));

        var response = await client.MatchMemberAsync(Endpoint, MatchRequest());

        response.Outcome.Should().Be(expected);
        response.Payload.Should().BeNull("no payload is carried off a failed call");
    }

    [Fact]
    public async Task SuccessfulCall_CarriesThePeersPayloadThrough()
    {
        var (client, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"resourceType\":\"Bundle\",\"type\":\"collection\"}"),
        });

        var response = await client.MatchMemberAsync(Endpoint, MatchRequest());

        response.Outcome.Should().Be(RemoteCallOutcome.Success);
        response.Payload.Should().Contain("Bundle");
    }

    [Fact]
    public async Task EmptyBodyOnSuccess_IsNotUsable()
    {
        var (client, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Empty),
        });

        (await client.MatchMemberAsync(Endpoint, MatchRequest())).Outcome
            .Should().Be(RemoteCallOutcome.InvalidResponse);
    }

    [Fact]
    public async Task TransportFault_IsUnavailableNotAnException()
    {
        var (client, _) = Build(_ => throw new HttpRequestException("connection refused"));

        (await client.MatchMemberAsync(Endpoint, MatchRequest())).Outcome
            .Should().Be(RemoteCallOutcome.Unavailable);
    }

    [Fact]
    public async Task Calls_GoOnlyToTheResolvedEndpointUris()
    {
        var (client, handler) = Build();

        await client.MatchMemberAsync(Endpoint, MatchRequest());
        await client.RequestMemberDataAsync(Endpoint, new RemoteMemberDataRequest
        {
            ReceivingPayerId = "cloud-health-office",
            MemberId = "prior-1001",
        });

        handler.Requests.Select(r => r.RequestUri).Should().Equal(
            Endpoint.MemberMatchUri, Endpoint.MemberDataExportUri);
        handler.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task NoCredentialConfigured_SendsNoAuthorizationHeader()
    {
        // The default provider supplies nothing rather than fabricating a token:
        // an unonboarded peer must answer Unauthorized, not receive a bogus one.
        var (client, handler) = Build();

        await client.MatchMemberAsync(Endpoint, MatchRequest());

        handler.Requests.Should().ContainSingle().Which.Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task OversizedPayload_IsRefusedRatherThanBuffered()
    {
        // A peer must not be able to make CHO buffer an unbounded body.
        var factoryClient = new HttpPayerToPayerRemoteClient(
            StubFactory(new string('x', 4096)),
            new UnconfiguredPayerToPayerCredentialProvider(),
            Options.Create(new PayerToPayerTransportOptions { MaxResponseBytes = 128 }),
            NullLogger<HttpPayerToPayerRemoteClient>.Instance);

        (await factoryClient.MatchMemberAsync(Endpoint, MatchRequest())).Outcome
            .Should().Be(RemoteCallOutcome.InvalidResponse);
    }

    private static IHttpClientFactory StubFactory(string body)
    {
        var handler = new RecordingHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) },
        };
        var factory = new Moq.Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(HttpPayerToPayerRemoteClient.HttpClientName))
            .Returns(new HttpClient(handler));
        return factory.Object;
    }
}
