using System.Text.Json.Nodes;
using FhirService.Models;
using FhirService.Models.PayerToPayer;
using FhirService.Services.BulkExport;
using FhirService.Services.PayerToPayer;
using FluentAssertions;
using Moq;

namespace CloudHealthOffice.FhirService.Tests.Services.BulkExport;

public class PayerToPayerBulkSourceTests
{
    private static FhirJsonResource Resource(string type, string id, string? lastUpdated = null)
    {
        var json = new JsonObject { ["resourceType"] = type, ["id"] = id };
        if (lastUpdated is not null)
            json["meta"] = new JsonObject { ["lastUpdated"] = lastUpdated };
        return new FhirJsonResource(json);
    }

    private static (PayerToPayerBulkSource Source, List<PayerToPayerExchangeRequest> Asked) Source(
        PayerToPayerOutcome outcome, params FhirResource[] resources)
    {
        var asked = new List<PayerToPayerExchangeRequest>();
        var exchange = new Mock<IPayerToPayerExchangeService>();
        exchange.Setup(e => e.RespondAsync(It.IsAny<PayerToPayerExchangeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PayerToPayerExchangeRequest r, CancellationToken _) =>
            {
                asked.Add(r);
                return new PayerToPayerExportResult
                {
                    Outcome = outcome,
                    MatchedMemberId = r.MemberId,
                    Bundle = outcome == PayerToPayerOutcome.Exported
                        ? new FhirBundle
                        {
                            Type = "collection",
                            Entry = resources.Select(x => new FhirBundleEntry { Resource = x }).ToList(),
                        }
                        : null,
                };
            });
        return (new PayerToPayerBulkSource(exchange.Object), asked);
    }

    [Fact]
    public async Task EachMemberIsAskedAsTheSingleMemberExport_ForTheBoundPayer()
    {
        var (source, asked) = Source(PayerToPayerOutcome.Exported,
            Resource("Patient", "m1"), Resource("Coverage", "c1"), Resource("ExplanationOfBenefit", "e1"));

        var lines = await ToList(source.ReadAsync("t1", "NEW-PLAN", "new-plan-backend", "m1",
            ["Patient", "ExplanationOfBenefit"], since: null));

        lines.Select(l => l.Type).Should().Equal("Patient", "ExplanationOfBenefit");
        lines[0].Line.Should().Be("""{"resourceType":"Patient","id":"m1"}""");
        var request = asked.Single();
        request.TenantId.Should().Be("t1");
        request.ReceivingPayerId.Should().Be("NEW-PLAN");
        request.MemberId.Should().Be("m1");
        request.Consent.Should().BeNull("the consent on record is what is checked");
    }

    [Fact]
    public async Task SinceKeepsWhatChangedAtOrAfterIt_AndWhatDoesNotSay()
    {
        var (source, _) = Source(PayerToPayerOutcome.Exported,
            Resource("ExplanationOfBenefit", "old", "2026-01-01T00:00:00Z"),
            Resource("ExplanationOfBenefit", "new", "2026-06-01T00:00:00Z"),
            Resource("ExplanationOfBenefit", "undated"));

        var lines = await ToList(source.ReadAsync("t1", "P", "c", "m1", ["ExplanationOfBenefit"],
            since: new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)));

        lines.Select(l => l.Line).Should().HaveCount(2).And.NotContain(l => l.Contains("\"old\""));
    }

    [Theory]
    [InlineData(PayerToPayerOutcome.NotAuthorized)]
    [InlineData(PayerToPayerOutcome.NoMatch)]
    public async Task AMemberNoLongerExportableToThePayer_WritesNothing(PayerToPayerOutcome outcome)
    {
        var (source, _) = Source(outcome);
        (await ToList(source.ReadAsync("t1", "P", "c", "m1", ["Patient"], null))).Should().BeEmpty();
    }

    private static async Task<List<(string Type, string Line)>> ToList(IAsyncEnumerable<(string Type, string Line)> lines)
    {
        var list = new List<(string Type, string Line)>();
        await foreach (var line in lines)
            list.Add(line);
        return list;
    }
}
