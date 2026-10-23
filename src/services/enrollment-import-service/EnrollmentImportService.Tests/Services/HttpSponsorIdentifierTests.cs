using System.Net;
using EnrollmentImportService.Clients;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EnrollmentImportService.Tests.Services;

/// <summary>
/// Reading sponsor-service's answer to "what is this REF*1L?"
/// (docs/architecture/plan-configuration.md §9b).
///
/// <para>
/// The distinction these pin is between an <em>answer</em> and a
/// <em>failure</em>. A 404 says the value is on file as neither number, and a
/// 409 says it is a policy number of more than one group; both are facts the
/// import acts on. Anything else — a 403 because the service was deployed
/// without the permission, a 500, the service being down — is the import not
/// having found out, and must not be read as "there is no such group", because
/// that is the path that creates one.
/// </para>
/// </summary>
public class HttpSponsorIdentifierTests
{
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? Requested { get; private set; }
        public string? Tenant { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requested = request.RequestUri;
            Tenant = request.Headers.TryGetValues("X-Tenant-ID", out var values) ? values.FirstOrDefault() : null;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (HttpSponsorServiceClient client, StubHandler handler) Client(HttpStatusCode status, string body = "{}")
    {
        var handler = new StubHandler(status, body);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(HttpSponsorServiceClient.HttpClientName))
            .Returns(() => new HttpClient(handler) { BaseAddress = new Uri("http://sponsor") });
        return (new HttpSponsorServiceClient(factory.Object, NullLogger<HttpSponsorServiceClient>.Instance), handler);
    }

    [Fact]
    public async Task A_group_number_comes_back_as_a_group_number()
    {
        var (client, handler) = Client(HttpStatusCode.OK,
            """{"value":"123456","kind":"GroupNumber","groupNumber":"123456"}""");

        var resolved = await client.ResolveIdentifierAsync("t1", "123456");

        resolved.Kind.Should().Be(SponsorIdentifierKind.GroupNumber);
        resolved.GroupNumber.Should().Be("123456");
        resolved.PolicyNumber.Should().BeNull();
        handler.Tenant.Should().Be("t1");
    }

    [Fact]
    public async Task A_policy_number_comes_back_with_the_group_to_file_against()
    {
        var (client, _) = Client(HttpStatusCode.OK,
            """{"value":"POL-1","kind":"PolicyNumber","groupNumber":"123456","policyNumber":"POL-1","benefitPlanId":"PPO500"}""");

        var resolved = await client.ResolveIdentifierAsync("t1", "POL-1");

        resolved.Kind.Should().Be(SponsorIdentifierKind.PolicyNumber);
        resolved.GroupNumber.Should().Be("123456");
        resolved.PolicyNumber.Should().Be("POL-1");
    }

    [Fact]
    public async Task A_conflict_is_the_ambiguous_answer_and_keeps_the_candidate_groups()
    {
        var (client, _) = Client(HttpStatusCode.Conflict,
            """{"value":"STANDARD-01","kind":"Ambiguous","candidateGroupNumbers":["111111","222222"]}""");

        var resolved = await client.ResolveIdentifierAsync("t1", "STANDARD-01");

        resolved.Kind.Should().Be(SponsorIdentifierKind.Ambiguous);
        resolved.GroupNumber.Should().BeNull();
        resolved.CandidateGroupNumbers.Should().Equal("111111", "222222");
    }

    [Fact]
    public async Task A_not_found_is_the_answer_that_it_is_neither_number()
    {
        var (client, _) = Client(HttpStatusCode.NotFound, """{"value":"NOPE","kind":"Unknown"}""");

        (await client.ResolveIdentifierAsync("t1", "NOPE")).Kind
            .Should().Be(SponsorIdentifierKind.Unknown);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Not_having_found_out_throws_rather_than_reading_as_neither_number(HttpStatusCode status)
    {
        var (client, _) = Client(status);

        var act = () => client.ResolveIdentifierAsync("t1", "123456");

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(status);
    }

    [Fact]
    public async Task A_recognised_kind_with_no_group_number_is_a_wire_mismatch()
    {
        // Filing coverage against an empty group number would be worse than
        // failing the member: it is unfindable afterwards.
        var (client, _) = Client(HttpStatusCode.OK, """{"value":"POL-1","kind":"PolicyNumber"}""");

        var act = () => client.ResolveIdentifierAsync("t1", "POL-1");

        await act.Should().ThrowAsync<SponsorServiceException>();
    }

    [Fact]
    public async Task An_empty_value_is_not_asked_about_at_all()
    {
        var (client, handler) = Client(HttpStatusCode.OK, "{}");

        (await client.ResolveIdentifierAsync("t1", "   ")).Kind
            .Should().Be(SponsorIdentifierKind.Unknown);
        handler.Requested.Should().BeNull();
    }

    [Fact]
    public async Task The_value_is_escaped_into_the_query_rather_than_pasted_in()
    {
        var (client, handler) = Client(HttpStatusCode.NotFound, """{"kind":"Unknown"}""");

        await client.ResolveIdentifierAsync("t1", "A&B 01");

        handler.Requested!.Query.Should().Contain("value=A%26B%2001");
    }
}
