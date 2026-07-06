using CloudHealthOffice.Tools.MccPlatformValidator;

namespace CloudHealthOffice.MccPlatformValidator.Tests;

public class ValidatorOptionsTests
{
    [Fact]
    public void Parse_WhenClaimsExceedDefaultCap_CapsAtDefaultMaxClaims()
    {
        var options = ValidatorOptions.Parse(["--claims", "50000"]);

        Assert.Equal(ValidatorOptions.DefaultMaxClaims, options.Claims);
    }

    [Fact]
    public void Parse_WhenMaxClaimsProvided_AllowsLargerValidationRuns()
    {
        var options = ValidatorOptions.Parse([
            "--claims", "50000",
            "--max-claims", "50000"
        ]);

        Assert.Equal(50_000, options.Claims);
    }
}
