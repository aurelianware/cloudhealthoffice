using FluentAssertions;
using FhirService.Models;
using FhirService.Services.ChoNative;

namespace CloudHealthOffice.FhirService.Tests.ChoNative;

public class FhirDataBindingsTests
{
    private static FhirAdapterOptions With(params (string Resource, string Source)[] sources)
    {
        var options = new FhirAdapterOptions();
        foreach (var (resource, source) in sources) options.Sources[resource] = source;
        return options;
    }

    [Fact]
    public void Every_resource_is_bound_to_the_mock_by_default()
    {
        var bindings = FhirDataBindings.From(new FhirAdapterOptions());

        bindings.Should().Be(new FhirDataBindings(
            FhirAdapterSources.Mock, FhirAdapterSources.Mock, FhirAdapterSources.Mock, FhirAdapterSources.Mock));
        bindings.AnyNative.Should().BeFalse();
    }

    [Fact]
    public void Patient_and_coverage_can_be_bound_to_cho_native_independently_and_case_insensitively()
    {
        var bindings = FhirDataBindings.From(With(("patient", "chonative")));

        bindings.Patient.Should().Be(FhirAdapterSources.ChoNative);
        bindings.Coverage.Should().Be(FhirAdapterSources.Mock);
        bindings.AnyNative.Should().BeTrue();
        bindings.SourceOf("Coverage").Should().Be(FhirAdapterSources.Mock);
        bindings.SourceOf("ExplanationOfBenefit").Should().BeNull();
    }

    [Fact]
    public void Payer_to_payer_can_be_bound_to_cho_native_on_its_own()
    {
        var bindings = FhirDataBindings.From(With(("PayerToPayer", "ChoNative")));

        bindings.PayerToPayerNative.Should().BeTrue();
        bindings.AnyNative.Should().BeTrue();
        bindings.Patient.Should().Be(FhirAdapterSources.Mock);
        bindings.SourceOf("PayerToPayer").Should().Be(FhirAdapterSources.ChoNative);
    }

    [Theory]
    [InlineData("Encounter")]
    [InlineData("Claim")]
    public void Encounter_and_claim_can_be_bound_to_cho_native(string resource)
    {
        // ADR 024 gave both a CHO-native source (encounter-service and
        // claims-service), so the start-up refusal that used to cover them
        // applies to no data resource any more. That the refusal still works is
        // pinned by the native-capable guard below.
        var bindings = FhirDataBindings.From(With((resource, FhirAdapterSources.ChoNative)));

        bindings.SourceOf(resource).Should().Be(FhirAdapterSources.ChoNative);
        bindings.AnyNative.Should().BeTrue();
    }

    [Fact]
    public void Every_known_data_resource_is_native_capable_so_a_live_deployment_needs_no_mock()
    {
        var bindings = FhirDataBindings.From(With(
            ("Patient", FhirAdapterSources.ChoNative),
            ("Coverage", FhirAdapterSources.ChoNative),
            ("Encounter", FhirAdapterSources.ChoNative),
            ("Claim", FhirAdapterSources.ChoNative),
            ("PayerToPayer", FhirAdapterSources.ChoNative)));

        bindings.Should().Be(new FhirDataBindings(
            FhirAdapterSources.ChoNative, FhirAdapterSources.ChoNative, FhirAdapterSources.ChoNative,
            FhirAdapterSources.ChoNative, FhirAdapterSources.ChoNative));
    }

    [Fact]
    public void Only_a_resource_with_a_cho_native_implementation_is_native_capable()
    {
        // The refusal branch in FhirDataBindings.From is unreachable while
        // every data resource has a native source. This pins the set it reads,
        // so adding a resource to the bindings without an implementation puts
        // the refusal back in play instead of silently binding it to ChoNative.
        FhirDataBindings.NativeCapable.Should().BeEquivalentTo(
            ["Patient", "Coverage", "Encounter", "Claim", "PayerToPayer"]);

        // ExplanationOfBenefit is proxied to claims-service, not served
        // through IFhirDataAdapter, so it is not a binding at all.
        FhirDataBindings.NativeCapable.Should().NotContain("ExplanationOfBenefit");
    }

    [Fact]
    public void An_unknown_source_or_resource_stops_start_up()
    {
        var unknownSource = () => FhirDataBindings.From(With(("Patient", "Live")));
        unknownSource.Should().Throw<InvalidOperationException>().WithMessage("*not a known source*");

        var unknownResource = () => FhirDataBindings.From(With(("Observation", "ChoNative")));
        unknownResource.Should().Throw<InvalidOperationException>().WithMessage("*Observation is not a FHIR data resource*");
    }
}
