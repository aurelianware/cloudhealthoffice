using System.Text.Json;
using ArService.Gl;
using CloudHealthOffice.Finance.Contracts;

namespace ArService.Tests.Gl;

/// <summary>
/// The GL keeps one name per line of business: accrual events from older payment-service
/// builds say Marketplace where everything else says Exchange.
/// </summary>
public class GlLineOfBusinessTests
{
    [Theory]
    [InlineData("Marketplace", "Exchange")]
    [InlineData("Exchange", "Exchange")]
    [InlineData("medicaid", "Medicaid")]
    [InlineData("Dental", "Dental")] // not a line of business: kept as sent, never dropped
    [InlineData(null, null)]
    public void An_accrual_carries_the_canonical_line_of_business(string? sent, string? posted)
    {
        var envelope = GlFixtures.RunExecuted();
        var payload = JsonSerializer.Deserialize<PaymentRunExecutedEvent>(envelope.PayloadJson, GlEventTypes.Json)!;
        var withLob = payload with { LineOfBusiness = sent };

        var entry = GlEntryBuilder.Build(GlFixtures.Envelope(GlEventTypes.PaymentRunExecuted, "payment-run:run-1", withLob))!;

        entry.LineOfBusiness.Should().Be(posted);
    }
}
