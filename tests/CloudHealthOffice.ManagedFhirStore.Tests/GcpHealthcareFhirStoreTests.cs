using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CloudHealthOffice.ManagedFhirStore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudHealthOffice.ManagedFhirStore.Tests;

public class GcpHealthcareFhirStoreTests
{
    private const string Base =
        "https://healthcare.googleapis.com/v1/projects/demo/locations/us-central1/datasets/cho/fhirStores/cho-fhir-r4";

    private const string Eob = """{"resourceType":"ExplanationOfBenefit","id":"clm-1","status":"active"}""";

    [Fact]
    public async Task Upsert_puts_the_resource_at_its_cho_id_with_a_bearer_token()
    {
        var handler = new RecordingHandler(_ => Respond(HttpStatusCode.Created,
            """{"resourceType":"ExplanationOfBenefit","id":"clm-1","meta":{"versionId":"v7"}}"""));
        var store = CreateStore(handler);

        var result = await store.UpsertAsync("ExplanationOfBenefit", "clm-1", Eob);

        Assert.True(result.Succeeded);
        Assert.Equal("v7", result.VersionId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal($"{Base}/fhir/ExplanationOfBenefit/clm-1", request.Uri);
        Assert.Equal("Bearer test-token", request.Authorization);
        Assert.Equal("application/fhir+json", request.ContentType);
        Assert.Equal(Eob, request.Body);
    }

    [Fact]
    public async Task A_rejected_resource_is_returned_with_its_operation_outcome_not_thrown()
    {
        const string outcome = """{"resourceType":"OperationOutcome","issue":[{"severity":"error","code":"processing"}]}""";
        var store = CreateStore(new RecordingHandler(_ => Respond(HttpStatusCode.UnprocessableEntity, outcome)));

        var result = await store.UpsertAsync("ExplanationOfBenefit", "clm-1", Eob);

        Assert.False(result.Succeeded);
        Assert.Equal(422, result.StatusCode);
        Assert.Equal(outcome, result.OperationOutcomeJson);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    public async Task Store_failures_throw_and_say_whether_a_retry_can_help(HttpStatusCode status, bool transient)
    {
        var store = CreateStore(new RecordingHandler(_ => Respond(status, "{}")));

        var ex = await Assert.ThrowsAsync<ManagedFhirStoreException>(
            () => store.UpsertAsync("ExplanationOfBenefit", "clm-1", Eob));

        Assert.Equal((int)status, ex.StatusCode);
        Assert.Equal(transient, ex.IsTransient);
    }

    [Theory]
    [InlineData("Patient", "clm-1")]                           // body is an EOB
    [InlineData("ExplanationOfBenefit", "clm-2")]              // id mismatch
    [InlineData("ExplanationOfBenefit", "../../datasets/x")]   // not a FHIR id
    [InlineData("explanationOfBenefit", "clm-1")]              // not a resource type
    public async Task Upsert_refuses_a_key_that_does_not_match_the_body(string resourceType, string id)
    {
        var handler = new RecordingHandler(_ => Respond(HttpStatusCode.OK, Eob));
        var store = CreateStore(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertAsync(resourceType, id, Eob));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Read_of_a_missing_resource_is_null()
    {
        var store = CreateStore(new RecordingHandler(_ => Respond(HttpStatusCode.NotFound, "{}")));

        Assert.Null(await store.ReadAsync("Patient", "m-1"));
    }

    [Fact]
    public async Task Delete_of_a_missing_resource_succeeds()
    {
        var store = CreateStore(new RecordingHandler(_ => Respond(HttpStatusCode.NotFound, "{}")));

        await store.DeleteAsync("Patient", "m-1");
    }

    [Fact]
    public async Task Export_posts_to_the_store_and_returns_the_operation_as_the_job()
    {
        var handler = new RecordingHandler(_ => Respond(HttpStatusCode.OK,
            """{"name":"projects/demo/locations/us-central1/datasets/cho/operations/123"}"""));
        var store = CreateStore(handler);

        var job = await store.StartExportAsync(new ManagedFhirExportRequest(
            "gs://demo-export/run-1",
            ["ExplanationOfBenefit", "Patient"],
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)));

        Assert.Equal("projects/demo/locations/us-central1/datasets/cho/operations/123", job.JobId);
        Assert.Equal(ManagedFhirExportState.Running, job.State);
        var request = Assert.Single(handler.Requests);
        Assert.Equal($"{Base}:export", request.Uri);
        var body = JsonNode.Parse(request.Body!)!;
        Assert.Equal("gs://demo-export/run-1", body["gcsDestination"]!["uriPrefix"]!.GetValue<string>());
        Assert.Equal("ExplanationOfBenefit,Patient", body["_type"]!.GetValue<string>());
        Assert.Equal("2026-10-01T00:00:00Z", body["_since"]!.GetValue<string>());
    }

    [Fact]
    public async Task Export_refuses_a_destination_outside_cloud_storage()
    {
        var store = CreateStore(new RecordingHandler(_ => Respond(HttpStatusCode.OK, "{}")));

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.StartExportAsync(new ManagedFhirExportRequest("https://example.com/out")));
    }

    [Fact]
    public async Task Export_status_reads_a_finished_operation()
    {
        var store = CreateStore(new RecordingHandler(_ => Respond(HttpStatusCode.OK,
            """{"name":"x","done":true,"metadata":{"counter":{"success":"42"}}}""")));

        var job = await store.GetExportStatusAsync("projects/demo/locations/us-central1/datasets/cho/operations/123");

        Assert.Equal(ManagedFhirExportState.Succeeded, job.State);
        Assert.Equal(42, job.ResourceCount);
    }

    [Fact]
    public async Task Export_status_reports_a_failed_operation()
    {
        var store = CreateStore(new RecordingHandler(_ => Respond(HttpStatusCode.OK,
            """{"name":"x","done":true,"error":{"code":7,"message":"permission denied on bucket"}}""")));

        var job = await store.GetExportStatusAsync("projects/demo/locations/us-central1/datasets/cho/operations/123");

        Assert.Equal(ManagedFhirExportState.Failed, job.State);
        Assert.Equal("permission denied on bucket", job.Error);
    }

    [Theory]
    [InlineData("projects/other/locations/us-central1/datasets/cho/operations/1")]
    [InlineData("projects/demo/locations/us-central1/datasets/cho/operations/../../../x")]
    public async Task Export_status_refuses_an_operation_from_another_dataset(string jobId)
    {
        var handler = new RecordingHandler(_ => Respond(HttpStatusCode.OK, "{}"));
        var store = CreateStore(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => store.GetExportStatusAsync(jobId));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Missing_store_coordinates_fail_at_construction()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new GcpHealthcareFhirStore(
            new HttpClient(),
            new FixedTokenSource(),
            new GcpHealthcareFhirStoreOptions { ProjectId = "demo" },
            NullLogger<GcpHealthcareFhirStore>.Instance));

        Assert.Contains("Location", ex.Message);
        Assert.Contains("FhirStoreId", ex.Message);
    }

    private static GcpHealthcareFhirStore CreateStore(RecordingHandler handler) =>
        new(new HttpClient(handler),
            new FixedTokenSource(),
            new GcpHealthcareFhirStoreOptions
            {
                ProjectId = "demo",
                Location = "us-central1",
                DatasetId = "cho",
                FhirStoreId = "cho-fhir-r4",
            },
            NullLogger<GcpHealthcareFhirStore>.Instance);

    private static HttpResponseMessage Respond(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/fhir+json") };

    private sealed class FixedTokenSource : IGoogleAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult("test-token");
    }

    internal sealed record RecordedRequest(HttpMethod Method, string Uri, string? Authorization, string? ContentType, string? Body);

    internal sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(),
                request.Content?.Headers.ContentType?.MediaType,
                body));
            return respond(request);
        }
    }
}
