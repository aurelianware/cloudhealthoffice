using System.Net;
using EnrollmentImportService.Clients;
using Microsoft.Extensions.Logging.Abstractions;

namespace EnrollmentImportService.Tests.Services;

/// <summary>
/// Pins which downstream endpoints the importer's write clients call — the
/// mocks in the import-service tests can't show that.
/// </summary>
public class HttpEnrollmentWriteClientTests
{
    private sealed class CapturingHandler(HttpStatusCode status, string body = "") : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private static Mock<IHttpClientFactory> Factory(string name, HttpMessageHandler handler, string baseAddress)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(name))
            .Returns(() => new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(baseAddress) });
        return factory;
    }

    [Fact]
    public async Task MemberTerminate_UsesTheMemberOnlyDeleteEndpoint_NotTheCoverageBulkTerminatePost()
    {
        var handler = new CapturingHandler(HttpStatusCode.NoContent);
        var client = new HttpMemberServiceClient(
            Factory(HttpMemberServiceClient.HttpClientName, handler, "http://member-service").Object,
            NullLogger<HttpMemberServiceClient>.Instance);

        await client.TerminateAsync("t1", "SUB1-DABC", new TerminateMemberRequestDto
        {
            MemberId = "SUB1-DABC",
            TerminationDate = new DateTime(2026, 1, 31),
            ReasonCode = "834"
        });

        var request = handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Delete);
        request.RequestUri!.AbsolutePath.Should().Be("/api/v1/members/SUB1-DABC");
        request.RequestUri.Query.Should().Contain("terminationDate=2026-01-31").And.Contain("reasonCode=834");
        request.Headers.GetValues("X-Tenant-ID").Should().ContainSingle("t1");
    }

    [Fact]
    public async Task CoverageCreate_ReturnsTheIdCoverageServiceAssigned()
    {
        var handler = new CapturingHandler(HttpStatusCode.Created, "{\"id\":\"cov-123\",\"memberId\":\"M1\"}");
        var client = new HttpCoverageServiceClient(
            Factory(HttpCoverageServiceClient.HttpClientName, handler, "http://coverage-service").Object,
            NullLogger<HttpCoverageServiceClient>.Instance);

        var id = await client.CreateAsync("t1", new CreateCoverageRequestDto { MemberId = "M1" });

        id.Should().Be("cov-123");
    }

    [Fact]
    public async Task CoverageTerminate_SetsTheTerminationDateOnThatCoverageOnly()
    {
        var handler = new CapturingHandler(HttpStatusCode.NoContent);
        var client = new HttpCoverageServiceClient(
            Factory(HttpCoverageServiceClient.HttpClientName, handler, "http://coverage-service").Object,
            NullLogger<HttpCoverageServiceClient>.Instance);

        await client.TerminateAsync("t1", "cov-123", new DateTime(2026, 3, 31), "07");

        var request = handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Delete);
        request.RequestUri!.AbsolutePath.Should().Be("/api/v1/coverage/cov-123");
        request.RequestUri.Query.Should().Contain("terminationDate=2026-03-31");
    }
}
