using System.Net;
using System.Text;
using CloudHealthOffice.Events;
using CloudHealthOffice.ManagedFhirStore;
using FhirService.Services;
using FhirService.Services.ManagedFhirStore;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudHealthOffice.FhirService.Tests.Services;

public class ManagedFhirStoreProjectorTests
{
    private const string Tenant = "plan-a";
    private const string Eob = """{"resourceType":"ExplanationOfBenefit","id":"chain-1","status":"active","outcome":"complete"}""";

    [Fact]
    public async Task Publishes_the_claims_service_projection_at_its_own_id()
    {
        var claims = new StubHandler(_ => Respond(HttpStatusCode.OK, Eob));
        var store = new InMemoryManagedFhirStore();
        var projector = CreateProjector(claims, store);

        var outcome = await projector.ProjectClaimAsync(Event(claimId: "doc-7", claimVersionId: "chain-1"), default);

        outcome.Should().Be(ManagedFhirProjectionOutcome.Published);
        (await store.ReadAsync("ExplanationOfBenefit", "chain-1")).Should().Be(Eob);
        var request = claims.Requests.Should().ContainSingle().Subject;
        request.Uri.Should().Be("http://claims-service.test/fhir/ExplanationOfBenefit/chain-1");
        request.Tenant.Should().Be(Tenant);
    }

    [Fact]
    public async Task Events_from_older_producers_fall_back_to_the_claim_id()
    {
        var claims = new StubHandler(_ => Respond(HttpStatusCode.OK, Eob));
        var projector = CreateProjector(claims, new InMemoryManagedFhirStore());

        await projector.ProjectClaimAsync(Event(claimId: "chain-1", claimVersionId: null), default);

        claims.Requests.Single().Uri.Should().EndWith("/ExplanationOfBenefit/chain-1");
    }

    [Fact]
    public async Task Replaying_an_event_overwrites_rather_than_duplicates()
    {
        var store = new InMemoryManagedFhirStore();
        var projector = CreateProjector(new StubHandler(_ => Respond(HttpStatusCode.OK, Eob)), store);

        await projector.ProjectClaimAsync(Event(), default);
        await projector.ProjectClaimAsync(Event(), default);

        store.Count.Should().Be(1);
    }

    [Fact]
    public async Task Another_tenants_claim_is_never_fetched_or_published()
    {
        var claims = new StubHandler(_ => Respond(HttpStatusCode.OK, Eob));
        var store = new InMemoryManagedFhirStore();
        var projector = CreateProjector(claims, store);

        var outcome = await projector.ProjectClaimAsync(Event(tenantId: "plan-b"), default);

        outcome.Should().Be(ManagedFhirProjectionOutcome.OtherTenant);
        claims.Requests.Should().BeEmpty();
        store.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_claim_with_no_projection_is_terminal_not_retried()
    {
        var projector = CreateProjector(new StubHandler(_ => Respond(HttpStatusCode.NotFound, "{}")), new InMemoryManagedFhirStore());

        var outcome = await projector.ProjectClaimAsync(Event(), default);

        outcome.Should().Be(ManagedFhirProjectionOutcome.NotFound);
    }

    [Fact]
    public async Task A_claims_service_outage_throws_so_the_message_is_retried()
    {
        var projector = CreateProjector(new StubHandler(_ => Respond(HttpStatusCode.ServiceUnavailable, "{}")), new InMemoryManagedFhirStore());

        var act = () => projector.ProjectClaimAsync(Event(), default);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task A_resource_the_store_rejects_is_terminal_not_retried()
    {
        var store = new RejectingStore();
        var projector = CreateProjector(new StubHandler(_ => Respond(HttpStatusCode.OK, Eob)), store);

        var outcome = await projector.ProjectClaimAsync(Event(), default);

        outcome.Should().Be(ManagedFhirProjectionOutcome.Rejected);
    }

    [Fact]
    public void Publishing_without_a_tenant_is_a_startup_error()
    {
        var act = () => new ManagedFhirStoreProjector(
            new StubFactory(new StubHandler(_ => Respond(HttpStatusCode.OK, Eob))),
            new InMemoryManagedFhirStore(),
            new ConfigurationBuilder().Build(),
            NullLogger<ManagedFhirStoreProjector>.Instance);

        act.Should().Throw<InvalidOperationException>().WithMessage("*ManagedFhirStore:TenantId*");
    }

    [Fact]
    public void Rejection_logs_carry_issue_codes_not_the_resource()
    {
        var summary = ManagedFhirStoreProjector.SummarizeIssues(
            """{"resourceType":"OperationOutcome","issue":[{"code":"processing","diagnostics":"profile mismatch"},{"code":"invalid"}]}""");

        summary.Should().Be("processing: profile mismatch; invalid");
    }

    private static ManagedFhirStoreProjector CreateProjector(StubHandler claims, IManagedFhirStore store) =>
        new(new StubFactory(claims),
            store,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { [ManagedFhirStoreProjector.TenantIdKey] = Tenant })
                .Build(),
            NullLogger<ManagedFhirStoreProjector>.Instance);

    private static ClaimFinalizedEvent Event(string tenantId = Tenant, string claimId = "doc-1", string? claimVersionId = "chain-1") =>
        new() { TenantId = tenantId, ClaimId = claimId, ClaimVersionId = claimVersionId, MemberId = "m-1" };

    private static HttpResponseMessage Respond(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/fhir+json") };

    private sealed class RejectingStore : IManagedFhirStore
    {
        public string ProviderName => "rejecting";

        public Task<ManagedFhirWriteResult> UpsertAsync(string resourceType, string id, string resourceJson, CancellationToken ct = default) =>
            Task.FromResult(new ManagedFhirWriteResult(422, null,
                """{"resourceType":"OperationOutcome","issue":[{"code":"processing"}]}"""));

        public Task<string?> ReadAsync(string resourceType, string id, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task DeleteAsync(string resourceType, string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<ManagedFhirExportJob> StartExportAsync(ManagedFhirExportRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ManagedFhirExportJob> GetExportStatusAsync(string jobId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubFactory(StubHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            name.Should().Be(UpstreamClientNames.ClaimsService);
            return new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://claims-service.test/") };
        }
    }

    private sealed record SeenRequest(string Uri, string? Tenant);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<SeenRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new SeenRequest(
                request.RequestUri!.ToString(),
                request.Headers.TryGetValues(TenantHeaderPropagationHandler.HeaderName, out var values) ? values.Single() : null));
            return Task.FromResult(respond(request));
        }
    }
}
