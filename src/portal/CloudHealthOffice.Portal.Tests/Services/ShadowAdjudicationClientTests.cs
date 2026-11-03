using System.Net;
using System.Text;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.Portal.Tests.Services;

public class ShadowAdjudicationClientTests
{
    private readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Services:ClaimsService"] = "http://claims/api" })
        .Build();

    private ShadowAdjudicationClient Client(FakeHandler handler)
        => new(new HttpClient(handler), _configuration, NullLogger<ShadowAdjudicationClient>.Instance);

    private static FakeHandler Answer(HttpStatusCode status, string? body = null, string mediaType = "application/json") => new(_ =>
    {
        var response = new HttpResponseMessage(status);
        if (body is not null)
            response.Content = new StringContent(body, Encoding.UTF8, mediaType);
        return response;
    });

    [Fact]
    public async Task A_refused_run_returns_the_services_reason()
    {
        var handler = Answer(HttpStatusCode.Conflict, """{ "error": "Shadow adjudication is off for this tenant." }""");

        var result = await Client(handler).RunBatchAsync("batch-1");

        handler.CapturedUrls.Should().Equal("http://claims/api/v1/claims/shadow-adjudication/runs");
        result.Succeeded.Should().BeFalse();
        result.Errors.Should().Equal("Shadow adjudication is off for this tenant.");
    }

    [Fact]
    public async Task The_report_and_results_come_from_the_runs_endpoints()
    {
        var handler = Answer(HttpStatusCode.OK, """
            { "run": { "id": "r1", "claimsCompared": 3, "matchRate": 0.5 }, "comparableClaims": 3, "decidedClaims": 2,
              "undecidedByGap": [ { "key": "NOCONTRACT", "claims": 1, "corePaid": 50 } ] }
            """);

        var report = await Client(handler).GetReportAsync("r1", 500);

        handler.CapturedUrls.Should().Equal("http://claims/api/v1/claims/shadow-adjudication/runs/r1/report?top=100");
        report!.Run.MatchRate.Should().Be(0.5m);
        report.UndecidedByGap.Single().Key.Should().Be("NOCONTRACT");
    }

    [Fact]
    public async Task The_csv_is_fetched_with_or_without_undecided_claims_and_null_for_another_tenants_run()
    {
        var handler = Answer(HttpStatusCode.OK, "ClaimNumber,Kind\n", "text/csv");
        (await Client(handler).GetDiscrepanciesCsvAsync("r1", true)).Should().NotBeNull();
        handler.CapturedUrls.Should().Equal("http://claims/api/v1/claims/shadow-adjudication/runs/r1/discrepancies.csv?includeUndecided=true");

        (await Client(Answer(HttpStatusCode.NotFound)).GetDiscrepanciesCsvAsync("r2", false)).Should().BeNull();
    }
}
