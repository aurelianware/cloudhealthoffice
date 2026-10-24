using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AuthorizationService.Controllers;
using AuthorizationService.Models;
using NSubstitute;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudHealthOffice.AuthorizationService.Tests;

public class PaReportingAuthenticationTests(AuthorizationApiFactory factory) : IClassFixture<AuthorizationApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };
    [Theory]
    [InlineData("")]
    [InlineData("authorizations:write")]
    [InlineData("authorizations:decide")]
    [InlineData(PaReportingController.RecordPermission)]
    public async Task RecordingRequiresBothEvidenceAndDecisionPermissions(string permission)
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            factory.IssueTokenWithPermissions("tenant-a", "reviewer", permission));
        var response = await client.PostAsJsonAsync("/api/authorizations/cms-0057/pa-1/facts", new RecordPaReportingFact());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RecordingUsesTokenActor_AndDoesNotRequireAnUnrelatedRole()
    {
        factory.AuthorizationRepository.RecordReportingFactAsync("pa-1", Arg.Any<RecordPaReportingFact>(),
            "reviewer-7", Arg.Any<CancellationToken>()).Returns(new PaReportingFact
            { RecordedBy = "reviewer-7", Kind = PaReportingFactKind.AppealDecision });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            factory.IssueTokenWithPermissions("tenant-a", "reviewer-7", PaReportingController.RecordPermission,
                AuthorizationsController.DecidePermission));
        var response = await client.PostAsJsonAsync("/api/authorizations/cms-0057/pa-1/facts", new RecordPaReportingFact
        {
            EventId = "appeal-1", SourceSystem = "pa-review", EvidenceReference = "case:1", Kind = PaReportingFactKind.AppealDecision,
            EffectiveAt = DateTimeOffset.UtcNow.AddDays(-1), Outcome = AuthorizationStatus.Approved,
        }, Json);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        await factory.AuthorizationRepository.Received().RecordReportingFactAsync("pa-1", Arg.Any<RecordPaReportingFact>(),
            "reviewer-7", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnnualRead_StreamsEvidence_AndReturnsADraftWithExplicitCutoffs()
    {
        var received = new DateTime(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc);
        async IAsyncEnumerable<global::AuthorizationService.Models.Authorization> Rows()
        {
            await Task.CompletedTask;
            yield return new()
            {
                Status = AuthorizationStatus.Denied,
                ReportingLedger = new()
                {
                    ReceivedAt = received, RecordedAt = received, LineOfBusiness = LineOfBusiness.Medicaid,
                    Facts = new() { new()
                    {
                        Kind = PaReportingFactKind.Determination, Outcome = AuthorizationStatus.Approved,
                        EffectiveAt = received.AddDays(1), RecordedAt = received.AddDays(1), DecisionAt = received.AddDays(1),
                    } },
                },
            };
        }
        factory.AuthorizationRepository.StreamReportingCohortAsync(Arg.Any<DateTime>(), Arg.Any<DateTime>(),
            LineOfBusiness.Medicaid, Arg.Any<CancellationToken>()).Returns(_ => Rows());
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            factory.IssueTokenWithPermissions("tenant-a", "report-reader", "authorizations:read"));
        var response = await client.GetAsync("/api/authorizations/cms-0057/annual-draft?year=2026&timeZone=America%2FLos_Angeles&lineOfBusiness=Medicaid&outcomeCutoff=2026-03-01T00:00:00Z&knowledgeCutoff=2026-03-01T00:00:00Z");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement Property(JsonElement node, string name) => node.EnumerateObject()
            .Single(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        Assert.False(Property(body.RootElement, "PublicationReady").GetBoolean());
        var standard = Property(body.RootElement, "Standard");
        Assert.Equal(1, Property(standard, "Approved").GetInt32());
        Assert.Equal(0, Property(standard, "Denied").GetInt32());
        _ = factory.AuthorizationRepository.Received().StreamReportingCohortAsync(
            new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc), new DateTime(2027, 1, 1, 8, 0, 0, DateTimeKind.Utc),
            LineOfBusiness.Medicaid, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnonymousAnnualReport_IsUnauthorized()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/authorizations/cms-0057/annual-draft?year=2026&timeZone=UTC&lineOfBusiness=Medicaid");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
