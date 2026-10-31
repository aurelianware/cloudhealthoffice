using CloudHealthOffice.Infrastructure.Tests.Adapters;
using ProviderService.Adapters;

namespace CloudHealthOffice.ProviderService.Tests.Adapters.Conformance;

/// <summary>
/// ADR 027: an unimplemented vendor provider or organization connector fails
/// explicitly on every method. When an engagement implements a method, pass
/// its name to the assertion and cover it with
/// <see cref="ProviderAdapterConformanceSuite"/>.
/// </summary>
public class VendorProviderAdapterStubTests
{
    public static TheoryData<IProviderAdapter, string> ProviderStubs => new()
    {
        { new QnxtProviderAdapter(), "qnxt" },
        { new FacetsProviderAdapter(), "facets" },
        { new HealthEdgeProviderAdapter(), "healthedge" },
    };

    public static TheoryData<IOrganizationAdapter, string> OrganizationStubs => new()
    {
        { new QnxtOrganizationAdapter(), "qnxt" },
        { new FacetsOrganizationAdapter(), "facets" },
    };

    [Theory]
    [MemberData(nameof(ProviderStubs))]
    public async Task Unimplemented_provider_connector_fails_explicitly_on_every_method(IProviderAdapter stub, string platform)
    {
        stub.Platform.Should().Be(platform);

        var offenders = await VendorConnectorStubAssertions.MethodsThatDoNotFailExplicitlyAsync(stub);

        offenders.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(OrganizationStubs))]
    public async Task Unimplemented_organization_connector_fails_explicitly_on_every_method(IOrganizationAdapter stub, string platform)
    {
        stub.Platform.Should().Be(platform);

        var offenders = await VendorConnectorStubAssertions.MethodsThatDoNotFailExplicitlyAsync(stub);

        offenders.Should().BeEmpty();
    }
}
