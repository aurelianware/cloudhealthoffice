using System.Net;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Portal.Tests.Services;

/// <summary>
/// A member's access log merges member-service (the member record) and
/// claims-service (their claims), newest first; a source that fails is named
/// instead of hiding the other's entries.
/// </summary>
public class RecordAccessLogServiceTests
{
    private sealed class ByHostHandler(Dictionary<string, (HttpStatusCode Code, string Body)> responses) : HttpMessageHandler
    {
        public List<string> Paths { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.PathAndQuery);
            var (code, body) = responses[request.RequestUri.Host];
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    private static RecordAccessLogService Sut(ByHostHandler handler) => new(
        new HttpClient(handler),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Services:MemberService"] = "http://member-service/api/v1",
            ["Services:ClaimsService"] = "http://claims-service/api",
            ["Services:PaymentService"] = "http://payment-service/api",
        }).Build(),
        Mock.Of<ILogger<RecordAccessLogService>>());

    private const string MemberLog = """[{"id":"1","recordType":"Member","recordId":"M 1","action":"View","outcome":"Allowed","actorId":"csr","occurredAt":"2026-10-01T10:00:00Z"}]""";
    private const string PaymentLog = """[{"id":"3","recordType":"Payment","recordId":"pay-1","action":"Era","outcome":"Allowed","actorId":"fin","occurredAt":"2026-10-03T10:00:00Z"}]""";
    private const string ClaimLog = """[{"id":"2","claimId":"c1","action":"View","outcome":"Allowed","actorId":"ex","occurredAt":"2026-10-02T10:00:00Z"}]""";

    [Fact]
    public async Task Merges_member_and_claim_reads_newest_first()
    {
        var handler = new ByHostHandler(new()
        {
            ["member-service"] = (HttpStatusCode.OK, MemberLog),
            ["claims-service"] = (HttpStatusCode.OK, ClaimLog),
            ["payment-service"] = (HttpStatusCode.OK, PaymentLog),
        });

        var log = await Sut(handler).GetMemberAccessLogAsync("M 1");

        handler.Paths.Should().BeEquivalentTo([
            "/api/v1/members/M%201/access-log?limit=200",
            "/api/claims/access-log?memberId=M%201&limit=200",
            "/api/payments/access-log?memberId=M%201&limit=200"]);
        log.Entries.Select(e => (e.RecordType, e.RecordId)).Should().Equal(("Payment", "pay-1"), ("Claim", "c1"), ("Member", "M 1"));
        log.Unavailable.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failing_source_is_named_and_the_rest_still_shows()
    {
        var handler = new ByHostHandler(new()
        {
            ["member-service"] = (HttpStatusCode.OK, MemberLog),
            ["claims-service"] = (HttpStatusCode.ServiceUnavailable, ""),
            ["payment-service"] = (HttpStatusCode.OK, "[]"),
        });

        var log = await Sut(handler).GetMemberAccessLogAsync("M 1");

        log.Entries.Should().ContainSingle(e => e.RecordType == "Member");
        log.Unavailable.Should().Equal("Claims Service");
    }
}
