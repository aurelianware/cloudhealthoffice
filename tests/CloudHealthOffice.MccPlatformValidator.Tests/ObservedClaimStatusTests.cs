using CloudHealthOffice.Tools.MccPlatformValidator;

namespace CloudHealthOffice.MccPlatformValidator.Tests;

public class ObservedClaimStatusTests
{
    [Fact]
    public void FromClaimJson_ParsesNumericPendedStatusAndPendCode()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""
        {
          "status": 4,
          "pendDetails": {
            "pendCode": "COB"
          }
        }
        """);

        var observed = ObservedClaimStatus.FromClaimJson(document.RootElement);

        Assert.Equal(ClaimValidationOutcome.Pended, observed.Outcome);
        Assert.Equal("4", observed.RawStatus);
        Assert.Equal("COB", observed.PendCode);
        Assert.True(observed.IsTerminal);
    }

    [Fact]
    public void FromClaimJson_ParsesNumericBusinessDenialCode()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""
        {
          "status": 6,
          "adjudicationResult": {
            "denialReasonCode": "197"
          }
        }
        """);

        var observed = ObservedClaimStatus.FromClaimJson(document.RootElement);

        Assert.Equal(ClaimValidationOutcome.BusinessDenial, observed.Outcome);
        Assert.Equal(MccWorkflowValidation.PriorAuthRequiredCode, observed.BusinessDenialCode);
        Assert.True(observed.IsTerminal);
    }

    [Fact]
    public void FromClaimJson_ParsesPersistedPlanPayment()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""
        {
          "status": 5,
          "adjudicationResult": {
            "payerPayment": 123.45
          }
        }
        """);

        var observed = ObservedClaimStatus.FromClaimJson(document.RootElement);

        Assert.Equal(ClaimValidationOutcome.Paid, observed.Outcome);
        Assert.Equal(123.45m, observed.PlanPayment);
        Assert.True(observed.IsTerminal);
    }
}
