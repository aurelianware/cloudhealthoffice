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

    [Fact]
    public void GatewayAbstractionTypes_DoNotNameAnyVendor()
    {
        var gatewayTypes = typeof(IHealthcareTransactionGateway).Assembly
            .GetTypes()
            .Where(t => t.Namespace is { } ns &&
                        ns.StartsWith("CloudHealthOffice.Infrastructure.Gateways", StringComparison.Ordinal))
            .ToList();

        gatewayTypes.Should().NotBeEmpty();

        foreach (var type in gatewayTypes)
        {
            foreach (var marker in VendorMarkers)
            {
                type.Name.ToLowerInvariant().Should().NotContain(marker,
                    $"gateway abstraction type '{type.FullName}' must be vendor-neutral");
            }
        }
    }

    [Fact]
    public void CanonicalEligibilityModels_ExposeOnlyNeutralPropertyTypes()
    {
        // Every property on the canonical request/response resolves to a BCL or
        // CHO gateway type — never a vendor DTO namespace.
        var models = new[] { typeof(GatewayEligibilityRequest), typeof(GatewayEligibilityResponse) };

        foreach (var model in models)
        {
            foreach (var prop in model.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var ns = (prop.PropertyType.Namespace ?? string.Empty).ToLowerInvariant();
                foreach (var marker in VendorMarkers)
                {
                    ns.Should().NotContain(marker,
                        $"property '{model.Name}.{prop.Name}' must not expose a vendor type");
                }
            }
        }
    }
}
