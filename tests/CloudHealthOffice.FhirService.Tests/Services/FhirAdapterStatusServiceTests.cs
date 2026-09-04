using FluentAssertions;
using FhirService.Models;
using FhirService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.FhirService.Tests.Services;

public class FhirAdapterStatusServiceTests
{
    [Fact]
    public void Default_configuration_is_demo_synthetic_and_not_live()
    {
        var report = CreateService().GetStatus();

        report.ConfiguredMode.Should().Be(FhirAdapterModes.Demo);
        report.EffectiveMode.Should().Be(FhirAdapterModes.Hybrid);
        report.DataClassification.Should().Be(FhirAdapterDataClasses.Synthetic);
        report.TenantId.Should().Be("demo-tenant");
        report.BuyerSafeLabel.Should().Contain("source labels");
        report.AttestationNote.Should().Contain("not legal attestation");
        report.Resources.Should().Contain(r =>
            r.Resource == "Patient" && r.Mode == FhirAdapterModes.Demo);
        report.Resources.Should().Contain(r =>
            r.Resource == "PayerToPayer" && r.Mode == FhirAdapterModes.OutOfScope);
        report.Resources.Should().NotContain(r => r.Mode == FhirAdapterModes.Live);
    }

    [Fact]
    public void Appeal_stays_demo_when_mock_adapter_is_enabled()
    {
        var report = CreateService(useMockAppeal: true).GetStatus();
        var appeal = report.Resources.Single(r => r.Resource == "Appeal");

        appeal.Mode.Should().Be(FhirAdapterModes.Demo);
        appeal.Source.Should().Contain("MockFhirAppealAdapter");
    }

    [Fact]
    public void Appeal_is_hybrid_when_http_adapter_is_wired()
    {
        var report = CreateService(useMockAppeal: false).GetStatus();
        var appeal = report.Resources.Single(r => r.Resource == "Appeal");

        appeal.Mode.Should().Be(FhirAdapterModes.Hybrid);
        appeal.Source.Should().Contain("HttpFhirAppealAdapter");
    }

    [Fact]
    public void Mixed_resource_modes_force_effective_hybrid_even_if_configured_live()
    {
        var options = new FhirAdapterOptions
        {
            Mode = FhirAdapterModes.Live,
            DataClassification = FhirAdapterDataClasses.Synthetic,
            TenantId = "demo-tenant",
        };
        var report = CreateService(options, useMockAppeal: true).GetStatus();

        report.ConfiguredMode.Should().Be(FhirAdapterModes.Live);
        report.EffectiveMode.Should().Be(FhirAdapterModes.Hybrid);
    }

    [Fact]
    public void Resource_override_is_honored()
    {
        var options = new FhirAdapterOptions
        {
            Mode = FhirAdapterModes.Hybrid,
            Resources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Patient"] = FhirAdapterModes.Live,
            },
        };
        var report = CreateService(options, useMockAppeal: true).GetStatus();

        report.Resources.Single(r => r.Resource == "Patient").Mode
            .Should().Be(FhirAdapterModes.Live);
    }

    private static FhirAdapterStatusService CreateService(
        FhirAdapterOptions? options = null,
        bool useMockAppeal = true)
    {
        options ??= new FhirAdapterOptions();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Appeals:UseMockAdapter"] = useMockAppeal ? "true" : "false",
            })
            .Build();
        return new FhirAdapterStatusService(Options.Create(options), config);
    }
}
