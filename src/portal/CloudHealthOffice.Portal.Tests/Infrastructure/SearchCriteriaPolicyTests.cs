using CloudHealthOffice.Portal.Infrastructure;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.Portal.Tests.Infrastructure;

public class SearchCriteriaPolicyTests
{
    [Theory]
    [InlineData(null, SearchCriteriaMode.NonDemoTenants)]
    [InlineData("", SearchCriteriaMode.NonDemoTenants)]
    [InlineData("always", SearchCriteriaMode.Always)]
    [InlineData("Never", SearchCriteriaMode.Never)]
    [InlineData("nondemotenants", SearchCriteriaMode.NonDemoTenants)]
    [InlineData("typo", SearchCriteriaMode.Always)]
    [InlineData("7", SearchCriteriaMode.Always)]
    public void ParseMode_DefaultsToNonDemoAndFailsClosedOnUnknown(string? value, SearchCriteriaMode expected)
    {
        SearchCriteriaPolicy.ParseMode(value).Should().Be(expected);
    }

    [Theory]
    [InlineData(SearchCriteriaMode.NonDemoTenants, true, false)]
    [InlineData(SearchCriteriaMode.NonDemoTenants, false, true)]
    [InlineData(SearchCriteriaMode.NonDemoTenants, null, true)]
    [InlineData(SearchCriteriaMode.Always, true, true)]
    [InlineData(SearchCriteriaMode.Never, false, false)]
    public void Requires_FollowsModeAndTenant(SearchCriteriaMode mode, bool? isDemo, bool expected)
    {
        SearchCriteriaPolicy.Requires(mode, isDemo).Should().Be(expected);
    }

    private static SearchCriteriaPolicy Create(string? mode, Func<Task<TenantContext?>> context)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [SearchCriteriaPolicy.ConfigKey] = mode })
            .Build();
        var tenant = new Mock<ITenantContextService>();
        tenant.Setup(t => t.GetCurrentTenantContextAsync()).Returns(context);
        return new SearchCriteriaPolicy(config, tenant.Object, NullLogger<SearchCriteriaPolicy>.Instance);
    }

    [Fact]
    public async Task DemoTenant_MayBrowse()
    {
        var sut = Create(null, () => Task.FromResult<TenantContext?>(new TenantContext { IsDemo = true }));
        (await sut.RequiresCriteriaAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task RealTenant_RequiresCriteria()
    {
        var sut = Create(null, () => Task.FromResult<TenantContext?>(new TenantContext { IsDemo = false }));
        (await sut.RequiresCriteriaAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task Always_OverridesDemoTenant()
    {
        var sut = Create("Always", () => Task.FromResult<TenantContext?>(new TenantContext { IsDemo = true }));
        (await sut.RequiresCriteriaAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task TenantLookupFailure_RequiresCriteria()
    {
        var sut = Create(null, () => throw new InvalidOperationException("no circuit"));
        (await sut.RequiresCriteriaAsync()).Should().BeTrue();
    }
}
