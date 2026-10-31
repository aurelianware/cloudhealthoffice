using ClaimsService.Adapters;
using CloudHealthOffice.Infrastructure.Tests.Adapters;
using FluentAssertions;

namespace CloudHealthOffice.ClaimsService.Tests.Adapters.Conformance;

/// <summary>
/// ADR 027: an unimplemented vendor claim connector fails explicitly on every
/// method. When an engagement implements a method, pass its name to the
/// assertion and cover it with <see cref="ClaimAdapterConformanceSuite"/>.
/// </summary>
public class VendorClaimAdapterStubTests
{
    public static TheoryData<IClaimAdapter, string> Stubs => new()
    {
        { new QnxtClaimAdapter(), "qnxt" },
        { new FacetsClaimAdapter(), "facets" },
        { new HealthEdgeClaimAdapter(), "healthedge" },
    };

    [Theory]
    [MemberData(nameof(Stubs))]
    public async Task Unimplemented_connector_fails_explicitly_on_every_method(IClaimAdapter stub, string platform)
    {
        stub.Platform.Should().Be(platform);

        var offenders = await VendorConnectorStubAssertions.MethodsThatDoNotFailExplicitlyAsync(stub);

        offenders.Should().BeEmpty();
    }
}
