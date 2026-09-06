using System.Net;
using System.Text;
using FhirService.Services.Cdex;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.FhirService.Tests.Services;

/// <summary>
/// The HTTP hop between the CDex surface and rfai-service, which owns the
/// additional-information record.
///
/// The in-process acceptance harness calls the aggregate directly, so the
/// behaviour that only exists ON THE WIRE — how a refusal's meaning survives
/// serialization, and that the authenticated tenant travels with every call —
/// is proven here.
/// </summary>
public class HttpCdexAdditionalInformationStoreTests
{
    private const string Tenant = "tenant-a";
    private const string CaseId = "rfai-0123456789abcdef0123456789abcdef";

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; }
            = _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var copy = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var h in request.Headers)
                copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
            Requests.Add(copy);
            return Task.FromResult(Responder(request));
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) =>
            new(_handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://rfai-service.localhost.invalid/"),
            };
    }

    private static (HttpCdexAdditionalInformationStore Store, StubHandler Handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StubHandler { Responder = responder };
        var store = new HttpCdexAdditionalInformationStore(
            new StubFactory(handler),
            NullLogger<HttpCdexAdditionalInformationStore>.Instance);

        return (store, handler);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static readonly CdexResponseArtifact[] OneArtifact =
    [
        new() { SubmissionId = "sub-1", ContentType = "application/pdf" },
    ];

    // ── Conflicts keep their meaning across the wire ─────────────────────────

    [Fact]
    public async Task ACapacityRefusalIsNotReportedAsAClosedRequest()
    {
        // Both refusals are 409. Collapsing them would tell a provider their
        // request is no longer open when it is open and simply full — a
        // different answer, and a different thing for them to do about it.
        var (store, _) = Build(_ => Json(HttpStatusCode.Conflict,
            """{"outcome":"TooManyArtifacts","recorded":0,"resumedReview":false}"""));

        var result = await store.RecordResponseAsync(Tenant, CaseId, OneArtifact);

        result!.Outcome.Should().Be("TooManyArtifacts");
        result.Recorded.Should().Be(0);
        result.ResumedReview.Should().BeFalse();
    }

    [Fact]
    public async Task AClosedRequestKeepsItsOwnOutcome()
    {
        var (store, _) = Build(_ => Json(HttpStatusCode.Conflict,
            """{"outcome":"CaseNotOpenForResponse","recorded":0,"resumedReview":false}"""));

        (await store.RecordResponseAsync(Tenant, CaseId, OneArtifact))!
            .Outcome.Should().Be("CaseNotOpenForResponse");
    }

    [Fact]
    public async Task AnUnreadableConflictFallsBackToTheConservativeAnswer()
    {
        // "Closed" tells the caller to stop; "at capacity" invites a smaller
        // retry. When the body says nothing, stop is the safe answer.
        var (store, _) = Build(_ => Json(HttpStatusCode.Conflict, "not json at all"));

        (await store.RecordResponseAsync(Tenant, CaseId, OneArtifact))!
            .Outcome.Should().Be("CaseNotOpenForResponse");
    }

    [Fact]
    public async Task AnAcceptedResponseReportsWhatWasRecorded()
    {
        var (store, _) = Build(_ => Json(HttpStatusCode.OK,
            """{"outcome":"Accepted","recorded":2,"resumedReview":true}"""));

        var result = await store.RecordResponseAsync(Tenant, CaseId, OneArtifact);

        result!.Accepted.Should().BeTrue();
        result.Recorded.Should().Be(2);
        result.ResumedReview.Should().BeTrue();
    }

    [Fact]
    public async Task AMissingRequestIsNull()
    {
        var (store, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        (await store.RecordResponseAsync(Tenant, CaseId, OneArtifact)).Should().BeNull();
    }

    [Fact]
    public async Task AnUnexpectedFailureIsNotMistakenForARefusal()
    {
        // A 500 means CHO does not know what happened; reporting it as a
        // business refusal would tell the provider to stop retrying.
        var (store, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var act = async () => await store.RecordResponseAsync(Tenant, CaseId, OneArtifact);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    // ── Tenant travels with every call ───────────────────────────────────────

    [Fact]
    public async Task EveryCallCarriesTheAuthenticatedTenant()
    {
        var (store, handler) = Build(_ => Json(HttpStatusCode.OK, "null"));

        await store.GetByIdAsync(Tenant, CaseId);
        await store.GetByTrackingIdAsync(Tenant, "RFAI-20260906-AAAABBBBCCCC");
        await store.GetByAuthorizationNumberAsync(Tenant, "PAS-20260906-ABCD1234");
        await store.MarkDeliveredAsync(Tenant, CaseId);

        handler.Requests.Should().HaveCount(4);
        handler.Requests.Should().OnlyContain(r =>
            r.Headers.Contains("X-Tenant-ID")
            && r.Headers.GetValues("X-Tenant-ID").First() == Tenant);
    }

    [Fact]
    public async Task RecordingDeliveryNeverFailsTheRetrievalItStamps()
    {
        // Provenance matters, but losing the "delivered" stamp must not stop a
        // provider seeing what the payer needs from them.
        var (store, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var act = async () => await store.MarkDeliveredAsync(Tenant, CaseId);

        await act.Should().NotThrowAsync();
    }
}
