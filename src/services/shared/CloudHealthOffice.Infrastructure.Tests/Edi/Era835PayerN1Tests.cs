using CloudHealthOffice.Infrastructure.Edi;

namespace CloudHealthOffice.Infrastructure.Tests.Edi;

/// <summary>835 loop 1000A N1*PR: the CMS Plan ID qualifier only when there is a plan id.</summary>
public class Era835PayerN1Tests
{
    [Theory]
    [InlineData("Blue Cross", "BCBS001", "N1*PR*Blue Cross*XV*BCBS001~")]
    [InlineData("Blue Cross", null, "N1*PR*Blue Cross~")]
    [InlineData("Blue Cross", "  ", "N1*PR*Blue Cross~")]
    [InlineData("A*B~C", "P:1", "N1*PR*A B C*XV*P 1~")]
    public void Builds_the_payer_segment(string name, string? planId, string expected) =>
        Era835FinancialSegmentBuilder.BuildPayerN1(name, planId).Should().Be(expected);
}
