using CloudHealthOffice.Tools.MccPlatformValidator;

namespace CloudHealthOffice.MccPlatformValidator.Tests;

public class MccValidationProviderIdentityTests
{
    private static readonly Guid RunId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void BuildNpi_UsesWideRoleSeparatedNamespaces()
    {
        var billing = MccValidationProviderIdentity.BuildNpi(42, RunId, 100, role: 0);
        var rendering = MccValidationProviderIdentity.BuildNpi(42, RunId, 100, role: 1);
        var excludedRendering = MccValidationProviderIdentity.BuildNpi(42, RunId, 100, role: 2);

        Assert.StartsWith("93", billing, StringComparison.Ordinal);
        Assert.StartsWith("94", rendering, StringComparison.Ordinal);
        Assert.StartsWith("95", excludedRendering, StringComparison.Ordinal);
        Assert.Equal(10, billing.Length);
        Assert.Equal(10, rendering.Length);
        Assert.Equal(10, excludedRendering.Length);
        Assert.NotEqual(billing, rendering);
        Assert.NotEqual(rendering, excludedRendering);
    }

    [Fact]
    public void BuildNpi_DoesNotCollideAcrossFiftyThousandClaimScoreableWindow()
    {
        var npis = new HashSet<string>(StringComparer.Ordinal);

        for (var scenarioIndex = 0; scenarioIndex < 6_500; scenarioIndex++)
        {
            Assert.True(npis.Add(MccValidationProviderIdentity.BuildNpi(42, RunId, scenarioIndex, role: 0)));
            Assert.True(npis.Add(MccValidationProviderIdentity.BuildNpi(42, RunId, scenarioIndex, role: 1)));
            Assert.True(npis.Add(MccValidationProviderIdentity.BuildNpi(42, RunId, scenarioIndex, role: 2)));
        }
    }

    [Fact]
    public void BuildNpi_RejectsUnsupportedRole()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MccValidationProviderIdentity.BuildNpi(42, RunId, 0, role: 3));
    }
}
