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

    [Fact]
    public void Parse_WhenParallelismExceedsLocalConsumerCapacity_CapsAtMaximum()
    {
        var options = ValidatorOptions.Parse(["--parallelism", "128"]);

        Assert.Equal(ValidatorOptions.MaxParallelism, options.Parallelism);
        Assert.Equal(96, options.Parallelism);
        Assert.Equal(options.Parallelism, options.SeedParallelism);
    }

    [Fact]
    public void Parse_WhenSeedParallelismProvided_SeparatesFixtureAndClaimConcurrency()
    {
        var options = ValidatorOptions.Parse([
            "--parallelism", "56",
            "--seed-parallelism", "4"
        ]);

        Assert.Equal(56, options.Parallelism);
        Assert.Equal(4, options.SeedParallelism);
    }

    [Fact]
    public void Parse_WhenNoSeedProvidersProvided_DisablesProviderSeeding()
    {
        var options = ValidatorOptions.Parse(["--no-seed-providers"]);

        Assert.False(options.SeedProviders);
    }

    [Fact]
    public void Parse_WhenNoSeedMembersProvided_DisablesMemberSeeding()
    {
        var options = ValidatorOptions.Parse(["--no-seed-members"]);

        Assert.False(options.SeedMembers);
    }

    [Fact]
    public void Parse_WhenServiceBusOnlyProvided_EnablesAsynchronousAdjudicationMode()
    {
        var options = ValidatorOptions.Parse(["--servicebus-only"]);

        Assert.True(options.ServiceBusOnly);
        Assert.False(ValidatorOptions.Parse([]).ServiceBusOnly);
    }

    [Fact]
    public void Parse_WhenServiceBusReconciliationOptionsProvided_AppliesOverrides()
    {
        var defaults = ValidatorOptions.Parse([]);
        var disabled = ValidatorOptions.Parse([
            "--no-servicebus-reconciliation",
            "--servicebus-reconciliation-timeout", "120"
        ]);

        Assert.True(defaults.ServiceBusReconciliationEnabled);
        Assert.Equal(300, defaults.ServiceBusReconciliationTimeoutSeconds);
        Assert.False(disabled.ServiceBusReconciliationEnabled);
        Assert.Equal(120, disabled.ServiceBusReconciliationTimeoutSeconds);
    }

    [Fact]
    public void Parse_WhenMemberUrlProvided_AppliesOverride()
    {
        var options = ValidatorOptions.Parse(["--member-url", "http://member-service/"]);

        Assert.Equal("http://member-service", options.MemberUrl);
    }

    [Fact]
    public void Parse_WhenCoverageUrlProvided_AppliesOverride()
    {
        var options = ValidatorOptions.Parse(["--coverage-url", "http://coverage-service/"]);

        Assert.Equal("http://coverage-service", options.CoverageUrl);
    }

    [Fact]
    public void Parse_WhenAuthorizationUrlProvided_AppliesOverride()
    {
        var options = ValidatorOptions.Parse(["--authorization-url", "http://authorization-service/"]);

        Assert.Equal("http://authorization-service", options.AuthorizationUrl);
    }

    [Fact]
    public void Parse_WhenNoSeedAuthorizationsProvided_DisablesAuthorizationSeeding()
    {
        var options = ValidatorOptions.Parse(["--no-seed-authorizations"]);

        Assert.False(options.SeedAuthorizations);
    }

    [Fact]
    public void Parse_WhenPendObservationOptionsProvided_AppliesOverrides()
    {
        var options = ValidatorOptions.Parse([
            "--no-pend-observation",
            "--pend-observation-timeout", "30",
            "--pend-observation-interval-ms", "500"
        ]);

        Assert.False(options.PendObservationEnabled);
        Assert.Equal(30, options.PendObservationTimeoutSeconds);
        Assert.Equal(500, options.PendObservationIntervalMilliseconds);
    }

    [Fact]
    public void Parse_WhenPendDiagnosticsNotProvided_DefaultsToDisabled()
    {
        var options = ValidatorOptions.Parse([]);

        Assert.Null(options.PendDiagnosticsPath);
        Assert.Equal(200, options.PendDiagnosticsNcciSampleSize);
    }

    [Fact]
    public void Parse_WhenPendDiagnosticsProvided_AppliesOverrides()
    {
        var options = ValidatorOptions.Parse([
            "--pend-diagnostics", "/tmp/mcc-pend-diagnostics.json",
            "--pend-diagnostics-ncci-sample", "50"
        ]);

        Assert.Equal("/tmp/mcc-pend-diagnostics.json", options.PendDiagnosticsPath);
        Assert.Equal(50, options.PendDiagnosticsNcciSampleSize);
    }
}
