using System.Reflection;
using CloudHealthOffice.Infrastructure.Gateways.Stedi;
using CloudHealthOffice.Infrastructure.ReferenceData.Payers;
using CloudHealthOffice.Infrastructure.ReferenceData.Payers.Stedi;

namespace CloudHealthOffice.Infrastructure.Tests.ReferenceData.Payers;

public class PayerReferenceArchitectureTests
{
    private static readonly Assembly InfrastructureAssembly = typeof(StediHealthcareGateway).Assembly;

    [Fact]
    public void CanonicalPayerModel_HasNoStediSpecificPropertyNames()
    {
        var banned = new[] { "StediPayerId", "StediTradingPartnerId", "StediId" };
        var types = InfrastructureAssembly.GetTypes()
            .Where(t => t.Namespace == "CloudHealthOffice.Infrastructure.ReferenceData.Payers")
            .ToList();

        types.Should().NotBeEmpty();
        foreach (var type in types)
        {
            foreach (var name in type.GetProperties().Select(p => p.Name).Concat(type.GetFields().Select(f => f.Name)))
            {
                banned.Should().NotContain(name, $"canonical type {type.Name} must not have Stedi-specific property {name}");
            }
        }
    }

    [Fact]
    public void StediPayerDtos_AreInternal()
    {
        var leaked = InfrastructureAssembly.GetTypes()
            .Where(t => t.Namespace is "CloudHealthOffice.Infrastructure.ReferenceData.Payers.Stedi.DTOs"
                     or "CloudHealthOffice.Infrastructure.ReferenceData.Payers.Stedi")
            .Where(t => t.IsPublic)
            .Select(t => t.FullName)
            .ToList();

        leaked.Should().BeEmpty("Stedi payer DTOs and mapper must remain internal");
    }

    [Fact]
    public void PayerReferenceService_DoesNotReferenceEligibilityService()
    {
        var refs = typeof(PayerReferenceService).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType));

        refs.Select(t => t.Namespace ?? string.Empty)
            .Should()
            .NotContain(ns => ns.Contains("EligibilityService", StringComparison.Ordinal));
    }

    [Fact]
    public void StediPayerMapper_IsInternal()
    {
        typeof(StediPayerMapper).IsPublic.Should().BeFalse();
        typeof(StediPayerDirectoryClient).IsPublic.Should().BeFalse();
    }
}
