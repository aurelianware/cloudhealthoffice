using BenefitPlanService.Adapters;
using CloudHealthOffice.Infrastructure.Tests.Adapters;
using FluentAssertions;

namespace CloudHealthOffice.BenefitPlanService.Tests.Adapters;

/// <summary>
/// ADR 027: an unimplemented vendor benefit-plan connector fails explicitly on
/// every method. When an engagement implements a method, pass its name to the
/// assertion and add conformance tests for it.
/// </summary>
public class VendorBenefitPlanAdapterStubTests
{
    public static TheoryData<IBenefitPlanAdapter, string> Stubs => new()
    {
        { new QnxtBenefitPlanAdapter(), "qnxt" },
        { new FacetsBenefitPlanAdapter(), "facets" },
        { new HealthEdgeBenefitPlanAdapter(), "healthedge" },
    };

    [Theory]
    [MemberData(nameof(Stubs))]
    public async Task Unimplemented_connector_fails_explicitly_on_every_method(IBenefitPlanAdapter stub, string platform)
    {
        stub.Platform.Should().Be(platform);

        var offenders = await VendorConnectorStubAssertions.MethodsThatDoNotFailExplicitlyAsync(stub);

        offenders.Should().BeEmpty();
    }
}
