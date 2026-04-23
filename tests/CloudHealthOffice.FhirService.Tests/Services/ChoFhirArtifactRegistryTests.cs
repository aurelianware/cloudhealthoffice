using FhirService.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.FhirService.Tests.Services;

/// <summary>
/// Unit tests for <see cref="ChoFhirArtifactRegistry"/> — verifies embedded
/// artifacts load at ctor time and lookups behave correctly.
/// </summary>
public class ChoFhirArtifactRegistryTests
{
    private static ChoFhirArtifactRegistry BuildRegistry()
        => new(NullLogger<ChoFhirArtifactRegistry>.Instance);

    [Fact]
    public void Loads_expected_counts_on_construction()
    {
        var registry = BuildRegistry();

        registry.AllStructureDefinitions.Should().HaveCount(11,
            "4 profiles + 7 extensions");
        registry.AllCodeSystems.Should().HaveCount(6);
        registry.AllValueSets.Should().HaveCount(9);
        registry.AllOperationDefinitions.Should().HaveCount(1);
    }

    [Fact]
    public void GetStructureDefinition_returns_known_profile()
    {
        var sd = BuildRegistry().GetStructureDefinition("cho-appeal-task");

        sd.Should().NotBeNull();
        sd!.Url.Should().Be(ChoFhirCanonicalUrls.AppealTask);
        sd.Type.Should().Be("Task");
    }

    [Fact]
    public void GetStructureDefinition_returns_null_for_unknown_id()
        => BuildRegistry().GetStructureDefinition("does-not-exist").Should().BeNull();

    [Fact]
    public void GetOperationDefinition_returns_known_operation()
    {
        var od = BuildRegistry().GetOperationDefinition("cho-appeal-submit");

        od.Should().NotBeNull();
        od!.Url.Should().Be(ChoFhirCanonicalUrls.AppealSubmitOperation);
    }

    [Fact]
    public void GetCodeSystem_and_GetValueSet_round_trip()
    {
        var registry = BuildRegistry();

        registry.GetCodeSystem("cho-appeal-type").Should().NotBeNull();
        registry.GetValueSet("cho-appeal-task-status").Should().NotBeNull();
        registry.GetCodeSystem("nope").Should().BeNull();
        registry.GetValueSet("nope").Should().BeNull();
    }
}
