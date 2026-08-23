using System.Reflection;
using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways;

/// <summary>
/// Guards the architectural invariant behind requirement 7 and the acceptance
/// criteria: the canonical gateway models and contracts stay vendor-neutral.
/// No vendor-specific type (Stedi, Availity, etc.) may leak into the
/// <c>CloudHealthOffice.Infrastructure.Gateways</c> abstraction.
/// </summary>
public class GatewayVendorNeutralityTests
{
    private static readonly string[] VendorMarkers =
    {
        "stedi", "availity", "changehealthcare", "optum", "waystar"
    };

    // The vendor-neutral abstraction lives in these namespaces. Vendor adapters
    // legitimately live in vendor-named implementation sub-namespaces (e.g.
    // ...Gateways.Stedi) — those are excluded from the "no vendor name" rule,
    // which protects only the shared contracts and canonical models.
    private static readonly string[] CoreAbstractionNamespaces =
    {
        "CloudHealthOffice.Infrastructure.Gateways",
        "CloudHealthOffice.Infrastructure.Gateways.Capabilities",
        "CloudHealthOffice.Infrastructure.Gateways.Models"
    };

    [Fact]
    public void GatewayAbstractionTypes_DoNotNameAnyVendor()
    {
        var gatewayTypes = typeof(IHealthcareTransactionGateway).Assembly
            .GetTypes()
            .Where(t => t.Namespace is { } ns && CoreAbstractionNamespaces.Contains(ns))
            .ToList();

        gatewayTypes.Should().NotBeEmpty();

        foreach (var type in gatewayTypes)
        {
            // Check the full name (namespace + nested type path), not just the
            // simple name, so a vendor marker cannot slip in via a namespace or
            // nested type without failing this guard.
            var identifier = (type.FullName ?? type.Name).ToLowerInvariant();
            foreach (var marker in VendorMarkers)
            {
                identifier.Should().NotContain(marker,
                    $"gateway abstraction type '{type.FullName}' must be vendor-neutral");
            }
        }
    }

    [Fact]
    public void CanonicalEligibilityModels_ExposeOnlyNeutralPropertyTypes()
    {
        // Every property on the canonical request/response resolves to a BCL or
        // CHO gateway type — never a vendor DTO namespace.
        var models = new[]
        {
            typeof(GatewayEligibilityRequest),
            typeof(GatewayEligibilityResponse),
            typeof(GatewayEligibilityPerson)
        };

        foreach (var model in models)
        {
            foreach (var prop in model.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var ns = (prop.PropertyType.Namespace ?? string.Empty).ToLowerInvariant();
                var propName = prop.Name.ToLowerInvariant();
                foreach (var marker in VendorMarkers)
                {
                    ns.Should().NotContain(marker,
                        $"property '{model.Name}.{prop.Name}' must not expose a vendor type");
                    propName.Should().NotContain(marker,
                        $"property '{model.Name}.{prop.Name}' must not have a vendor-specific name");
                }
            }
        }
    }
}
