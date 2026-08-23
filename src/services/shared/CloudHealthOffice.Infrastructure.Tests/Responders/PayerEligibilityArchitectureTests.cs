using System.Reflection;
using CloudHealthOffice.Infrastructure.Gateways.Capabilities;
using CloudHealthOffice.Infrastructure.Responders;
using CloudHealthOffice.Infrastructure.Responders.Adapters;
using CloudHealthOffice.Infrastructure.Responders.Models;

namespace CloudHealthOffice.Infrastructure.Tests.Responders;

public class PayerEligibilityArchitectureTests
{
    private static readonly string[] VendorMarkers =
    {
        "stedi", "availity", "changehealthcare", "optum", "waystar"
    };

    private static readonly string[] CanonicalNamespaces =
    {
        "CloudHealthOffice.Infrastructure.Responders",
        "CloudHealthOffice.Infrastructure.Responders.Models",
        "CloudHealthOffice.Infrastructure.Responders.Directory",
        "CloudHealthOffice.Infrastructure.Responders.Routing"
    };

    [Fact]
    public void Responder_IsNotTheOutboundGateway()
    {
        typeof(IEligibilityResponder).Should().NotBeAssignableTo<IEligibilityGateway>();
        typeof(CloudHealthOfficeEligibilityResponder).Should().NotBeAssignableTo<IEligibilityGateway>();
        typeof(IEligibilityGateway).Should().NotBeAssignableTo<IEligibilityResponder>();
    }

    [Fact]
    public void CanonicalResponderTypes_DoNotNameAnyVendor()
    {
        var types = typeof(IEligibilityResponder).Assembly
            .GetTypes()
            .Where(t => t.Namespace is { } ns && CanonicalNamespaces.Contains(ns))
            .ToList();

        types.Should().NotBeEmpty();

        foreach (var type in types)
        {
            var identifier = (type.FullName ?? type.Name).ToLowerInvariant();
            foreach (var marker in VendorMarkers)
            {
                identifier.Should().NotContain(marker,
                    $"canonical responder type '{type.FullName}' must be vendor-neutral");
            }
        }
    }

    [Fact]
    public void CanonicalInquiryAndResponse_HaveNoVendorFields()
    {
        var models = new[]
        {
            typeof(PayerEligibilityInquiry),
            typeof(PayerEligibilityResponse),
            typeof(PayerEligibilityCostShare),
            typeof(PayerEligibilityProvider),
            typeof(PayerEligibilitySourceMetadata)
        };

        foreach (var model in models)
        {
            foreach (var prop in model.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var ns = (prop.PropertyType.Namespace ?? string.Empty).ToLowerInvariant();
                var propName = prop.Name.ToLowerInvariant();
                foreach (var marker in VendorMarkers)
                {
                    ns.Should().NotContain(marker);
                    propName.Should().NotContain(marker);
                }
            }
        }
    }

    [Fact]
    public void StediInboundAdapter_IsPlannedOnly()
    {
        var adapter = new StediInboundEligibilityAdapter();
        adapter.IsImplemented.Should().BeFalse();
        adapter.Name.Should().Be(StediInboundEligibilityAdapter.AdapterName);
        var act = () => adapter.EnsureImplemented();
        act.Should().Throw<NotSupportedException>()
            .WithMessage("*not publicly available*");
    }

    [Fact]
    public void ResponderAssembly_DoesNotReferenceStediInboundContract()
    {
        var responder = typeof(CloudHealthOfficeEligibilityResponder);
        foreach (var ctor in responder.GetConstructors())
        {
            foreach (var param in ctor.GetParameters())
            {
                param.ParameterType.FullName.Should().NotContain("Stedi",
                    "the responder must not take a Stedi dependency");
            }
        }
    }
}
