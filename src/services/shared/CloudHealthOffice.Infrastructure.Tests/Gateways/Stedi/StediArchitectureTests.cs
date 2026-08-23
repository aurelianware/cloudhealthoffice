using System.Reflection;
using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Stedi;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways.Stedi;

/// <summary>
/// Guards the isolation boundary (task section 7, acceptance criteria 4): Stedi
/// transport DTOs and mapping stay internal to the infrastructure implementation
/// and never become part of a public/domain contract, while the intended public
/// surface (gateway, options, registration) remains minimal.
/// </summary>
public class StediArchitectureTests
{
    private static readonly Assembly InfrastructureAssembly = typeof(StediHealthcareGateway).Assembly;

    [Fact]
    public void StediTransportDtosAndMapping_AreInternal_NotPublic()
    {
        var leaked = InfrastructureAssembly.GetTypes()
            .Where(t => t.Namespace is "CloudHealthOffice.Infrastructure.Gateways.Stedi.Models"
                     or "CloudHealthOffice.Infrastructure.Gateways.Stedi.Mapping")
            .Where(t => t.IsPublic)
            .Select(t => t.FullName)
            .ToList();

        leaked.Should().BeEmpty("Stedi DTOs and mapping must not be exposed as public/domain types");
    }

    [Fact]
    public void OnlyIntendedStediTypes_ArePublic()
    {
        var publicStediTypes = InfrastructureAssembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith("CloudHealthOffice.Infrastructure.Gateways.Stedi", StringComparison.Ordinal) == true)
            .Where(t => t.IsPublic)
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToList();

        // The gateway itself, its options, and its DI registration are the only
        // types a host application needs to see.
        publicStediTypes.Should().BeEquivalentTo(new[]
        {
            nameof(StediGatewayOptions),
            nameof(StediHealthcareGateway),
            nameof(StediHealthcareGatewayServiceCollectionExtensions)
        });
    }

    [Fact]
    public void PublicStediGateway_DoesNotExposeStediDtosOnItsSurface()
    {
        // The public gateway's method signatures must only reference canonical /
        // BCL types — never a Stedi DTO.
        var stediDtoNamespace = "CloudHealthOffice.Infrastructure.Gateways.Stedi.Models";

        foreach (var method in typeof(StediHealthcareGateway).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            var referenced = method.GetParameters().Select(p => p.ParameterType)
                .Append(method.ReturnType)
                .Concat(method.ReturnType.IsGenericType ? method.ReturnType.GetGenericArguments() : Array.Empty<Type>());

            foreach (var type in referenced)
            {
                (type.Namespace ?? string.Empty).Should().NotBe(stediDtoNamespace,
                    $"public member {method.Name} must not expose Stedi DTO type {type.Name}");
            }
        }
    }
}
