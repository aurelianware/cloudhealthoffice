using CloudHealthOffice.Tools.MccPlatformValidator;

namespace CloudHealthOffice.MccPlatformValidator.Tests;

public class MccFixtureScopeTests
{
    [Fact]
    public void Create_IsStableForSameTenantAndSeed()
    {
        var first = MccFixtureScope.Create("tenant-a", 42);
        var second = MccFixtureScope.Create("tenant-a", 42);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Create_IsolatesTenantsAndSeeds()
    {
        var baseline = MccFixtureScope.Create("tenant-a", 42);

        Assert.NotEqual(baseline, MccFixtureScope.Create("tenant-b", 42));
        Assert.NotEqual(baseline, MccFixtureScope.Create("tenant-a", 43));
    }
}
