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
    public void Cho_native_is_refused_where_no_native_source_exists(string resource)
    {
        var act = () => FhirDataBindings.From(With((resource, FhirAdapterSources.ChoNative)));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{resource}*no CHO-native source*");
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
