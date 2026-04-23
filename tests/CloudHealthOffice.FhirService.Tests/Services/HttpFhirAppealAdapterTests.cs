using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CloudHealthOffice.Appeals.Contracts;
using FhirService.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.FhirService.Tests.Services;

/// <summary>
/// Verifies the HttpFhirAppealAdapter's correlation-id propagation
/// (every sub-request of a Submit carries the same X-Correlation-Id),
/// failure classification (processing vs transient), and the short-
/// circuit behaviour when the top-level appeal create fails.
/// </summary>
public class HttpFhirAppealAdapterTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; }
            = _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Buffer headers/content before the caller disposes — tests
            // inspect them later.
            var copy = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var h in request.Headers)
                copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
            Requests.Add(copy);
            return Task.FromResult(Responder(request));
        }
    }

    private static (HttpFhirAppealAdapter Adapter, RecordingHandler Handler, CorrelationIdAccessor Correlation)
        Build(Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
    {
        var handler = new RecordingHandler();
        if (responder is not null) handler.Responder = responder;

        // Wire the correlation + tenant handlers as DelegatingHandlers in
        // front of the capture handler, so the adapter's calls flow
        // through them exactly as in production.
        var correlation = new CorrelationIdAccessor();
        var correlationHandler = new CorrelationIdPropagationHandler(correlation)
        {
            InnerHandler = handler
        };

        var client = new HttpClient(correlationHandler)
        {
            BaseAddress = new Uri("http://appeals-service.test/")
        };

        var factoryMock = new Moq.Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(HttpFhirAppealAdapter.HttpClientName))
            .Returns(client);

        var adapter = new HttpFhirAppealAdapter(
            factoryMock.Object,
            new HttpContextAccessor(),
            NullLogger<HttpFhirAppealAdapter>.Instance);

        return (adapter, handler, correlation);
    }

    private static AppealDto SampleAppeal() => new()
    {
        Id = "", // appeals-service assigns
        TenantId = "t1",
        ClaimId = "c1",
        ClaimNumber = "C1",
        MemberId = "m1",
        PatientName = "Test",
        ProviderNPI = "1234567890",
        AppealReason = "medically necessary",
        AppealType = AppealType.Reconsideration,
        AppealLevel = AppealLevel.FirstLevel,
        LineOfBusiness = LineOfBusiness.Commercial,
        Status = AppealStatus.Draft
    };

    [Fact]
    public async Task SubmitAppealAsync_propagates_same_CorrelationId_across_all_sub_requests()
    {
        var (adapter, handler, correlation) = Build(req =>
        {
            // Respond with a minimally-valid AppealDto body.
            var body = JsonSerializer.Serialize(new AppealDto
            {
                Id = "apl-xyz",
                MemberId = "m1",
                ClaimId = "c1",
                ProviderNPI = "1234567890",
                AppealReason = "x",
                AppealNumber = "APL-1",
                ClaimNumber = "C1",
                PatientName = "Test",
                Status = AppealStatus.Draft,
                Notes = new List<AppealNoteDto> { new AppealNoteDto { NoteId = "n1" } },
                Attachments = new List<AppealAttachmentDto> { new AppealAttachmentDto { AttachmentId = "a1", AttachmentTypeCode = "OZ" } }
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };
        });

        // Anchor correlation id explicitly.
        correlation.Set("submit-001");

        var bundle = new AppealSubmitBundleDto
        {
            Appeal = SampleAppeal(),
            Notes =
            {
                new AppealNoteDto { NoteId = "n1", CreatedBy = "u", NoteText = "x", CreatedAt = DateTime.UtcNow },
                new AppealNoteDto { NoteId = "n2", CreatedBy = "u", NoteText = "y", CreatedAt = DateTime.UtcNow }
            },
            Attachments =
            {
                new AppealAttachmentDto { AttachmentId = "a1", AttachmentTypeCode = "OZ" }
            }
        };

        var outcomes = await adapter.SubmitAppealAsync(bundle, "t1");

        // 1 appeal + 2 notes + 1 attachment = 4 outbound requests, 4 outcomes.
        handler.Requests.Should().HaveCount(4);
        outcomes.Should().HaveCount(4);

        // Every outbound request carries the same correlation id.
        foreach (var req in handler.Requests)
        {
            req.Headers.TryGetValues(CorrelationIdPropagationHandler.HeaderName, out var values)
                .Should().BeTrue($"every request must carry the {CorrelationIdPropagationHandler.HeaderName} header");
            values!.Should().ContainSingle().Which.Should().Be("submit-001");
        }

        outcomes.All(o => o.Success).Should().BeTrue();
    }

    [Fact]
    public async Task SubmitAppealAsync_short_circuits_when_appeal_create_fails()
    {
        var (adapter, handler, _) = Build(req =>
        {
            // 400 on the appeal create, nothing else.
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    "{\"status\":400,\"title\":\"Validation error\",\"type\":\"https://example/problems/x\"}",
                    System.Text.Encoding.UTF8,
                    "application/problem+json")
            };
        });

        var bundle = new AppealSubmitBundleDto
        {
            Appeal = SampleAppeal(),
            Notes = { new AppealNoteDto { NoteId = "n1" } },
            Attachments = { new AppealAttachmentDto { AttachmentId = "a1", AttachmentTypeCode = "OZ" } }
        };

        var outcomes = await adapter.SubmitAppealAsync(bundle, "t1");

        handler.Requests.Should().ContainSingle(
            "Notes and attachments must not be submitted when the top-level appeal create fails.");
        outcomes.Should().ContainSingle();
        outcomes[0].Success.Should().BeFalse();
        outcomes[0].HttpStatus.Should().Be(400);
        outcomes[0].FailureKind.Should().Be(AppealSubmitFailureKind.Processing);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, AppealSubmitFailureKind.Processing)]
    [InlineData(HttpStatusCode.UnprocessableEntity, AppealSubmitFailureKind.Processing)]
    [InlineData(HttpStatusCode.Conflict, AppealSubmitFailureKind.Processing)]
    [InlineData(HttpStatusCode.InternalServerError, AppealSubmitFailureKind.Transient)]
    [InlineData(HttpStatusCode.BadGateway, AppealSubmitFailureKind.Transient)]
    [InlineData(HttpStatusCode.ServiceUnavailable, AppealSubmitFailureKind.Transient)]
    public void ClassifyHttpFailure_splits_4xx_from_5xx(HttpStatusCode status, AppealSubmitFailureKind expected)
    {
        HttpFhirAppealAdapter.ClassifyHttpFailure(status).Should().Be(expected);
    }

    [Fact]
    public async Task BuildRedactedDiagnostics_keeps_structural_fields_drops_detail_and_errors()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent(
                // `detail` carries a PHI echo; `errors` carries free-text;
                // both must be dropped. `title` and `type` must survive.
                "{\"status\":409,\"title\":\"Invalid appeal transition\",\"type\":\"https://example/problems/appeal-transition\"," +
                "\"detail\":\"patient John Doe cannot …\",\"fromStatus\":\"Closed\",\"toStatus\":\"InReview\"," +
                "\"errors\":{\"noteText\":[\"John Doe had …\"]}}",
                System.Text.Encoding.UTF8,
                "application/problem+json")
        };

        var diag = await HttpFhirAppealAdapter.BuildRedactedDiagnosticsAsync(response, CancellationToken.None);

        diag.Should().Contain("HTTP 409");
        diag.Should().Contain("title=");
        diag.Should().Contain("type=");
        diag.Should().Contain("fromStatus=");
        diag.Should().Contain("toStatus=");
        diag.Should().NotContain("John Doe",
            "PHI-adjacent fields (detail, errors values) must be redacted from the diagnostic excerpt");
    }
}
