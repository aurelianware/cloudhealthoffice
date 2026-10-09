using PaymentService.Services;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests;

/// <summary>
/// 005010X221A1 CLP02 for a processed claim follows the payer responsibility
/// the claim was submitted to us with (837 2000B SBR01): 1 processed as
/// primary, 2 as secondary, 3 as tertiary (also payers four to eleven, which
/// the 835 has no code for).
/// </summary>
public class Era835CobClaimStatusTests
{
    [Theory]
    [InlineData("P", "1")]
    [InlineData(null, "1")]
    [InlineData("U", "1")]
    [InlineData("S", "2")]
    [InlineData("T", "3")]
    [InlineData("t", "3")]
    [InlineData("A", "3")]
    [InlineData("H", "3")]
    public void ProcessedAsCode_FromSbr01(string? sbr01, string expected) =>
        Assert.Equal(expected, Era835ClaimPaymentBuilder.ProcessedAsCode(sbr01));
}
