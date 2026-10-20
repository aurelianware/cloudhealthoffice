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
        // Inbound Payer-to-Payer respond (P2P-01), member-match (P2P-04), and
        // outbound initiation (P2P-02) are implemented over CHO-owned data, so
        // PayerToPayer reports Demo (was OutOfScope). Dedicated P2P consent
        // (P2P-03) is still partial, and no external-core P2P integration exists.
        report.Resources.Should().Contain(r =>
            r.Resource == "PayerToPayer" && r.Mode == FhirAdapterModes.Demo);
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
                ["ExplanationOfBenefit"] = FhirAdapterModes.Live,
            },
        };
        var report = CreateService(options, useMockAppeal: true).GetStatus();

        report.Resources.Single(r => r.Resource == "ExplanationOfBenefit").Mode
            .Should().Be(FhirAdapterModes.Live);
    }

    [Theory]
    [InlineData("Patient")]
    [InlineData("Coverage")]
    [InlineData("Encounter")]
    [InlineData("Claim")]
    public void A_mock_bound_data_resource_reports_demo_whatever_label_is_configured(string resource)
    {
        // ADR 018: the label cannot claim more than the binding delivers.
        var options = new FhirAdapterOptions
        {
            Resources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [resource] = FhirAdapterModes.Live },
        };

        var status = CreateService(options).GetStatus().Resources.Single(r => r.Resource == resource);

        status.Mode.Should().Be(FhirAdapterModes.Demo);
        status.Source.Should().Be("MockFhirDataAdapter");
    }

    [Theory]
    [InlineData("Patient", "member-service")]
    [InlineData("Coverage", "coverage-service")]
    public void A_cho_native_binding_reports_hybrid_and_its_source_unless_labelled(string resource, string service)
    {
        var bound = new FhirAdapterOptions
        {
            Sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [resource] = FhirAdapterSources.ChoNative },
        };
        var status = CreateService(bound).GetStatus().Resources.Single(r => r.Resource == resource);
        status.Mode.Should().Be(FhirAdapterModes.Hybrid);
        status.Source.Should().Contain(service).And.Contain("CHO-native");

        bound.Resources[resource] = FhirAdapterModes.Live;
        CreateService(bound).GetStatus().Resources.Single(r => r.Resource == resource).Mode
            .Should().Be(FhirAdapterModes.Live, "an operator may label a CHO-native binding Live for a pilot");

        CreateService(bound).GetStatus().Public().Resources.Single(r => r.Resource == resource).Source
            .Should().BeNull("the anonymous report never names backing services");
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
