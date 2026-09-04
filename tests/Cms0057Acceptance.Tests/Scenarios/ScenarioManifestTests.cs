using System.Reflection;
using Cms0057Acceptance.Tests.TestSupport;
using FluentAssertions;

namespace Cms0057Acceptance.Tests.Scenarios;

/// <summary>
/// Drift guards for the CMS-0057-F scenario manifest (<c>scenarios.json</c>).
/// These keep the machine-readable source of truth, the acceptance tests'
/// <c>[Trait("Scenario",…)]</c>, and (transitively) the generated evidence from
/// diverging. If a scenario is added/removed/retagged without updating the
/// manifest, one of these fails.
/// </summary>
public class ScenarioManifestTests
{
    private static readonly ScenarioManifest.ManifestDocument Manifest = ScenarioManifest.Load();
    private static readonly IReadOnlyList<ScenarioTrait> Traits =
        TraitScanner.Scan(Assembly.GetExecutingAssembly());

    private static readonly IReadOnlySet<string> AllowedTraitBackends =
        new HashSet<string>(StringComparer.Ordinal) { "Replace", "Augment" };
    private static readonly IReadOnlySet<string> AllowedAugmentKeys =
        new HashSet<string>(StringComparer.Ordinal) { "qnxt", "facets", "healthedge" };

    [Fact]
    public void Manifest_LoadsWithExpectedSchemaVersion()
    {
        Manifest.SchemaVersion.Should().Be(1);
        Manifest.Scenarios.Should().NotBeEmpty();
    }

    [Fact]
    public void Manifest_ScenarioIdsAreUnique()
    {
        var dupes = Manifest.Scenarios.GroupBy(s => s.Id)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        dupes.Should().BeEmpty("scenario IDs must be unique");
    }

    [Fact]
    public void Manifest_AllStatusesAreValid()
    {
        foreach (var s in Manifest.Scenarios)
        {
            ScenarioManifest.AllowedStatuses.Should().Contain(s.Replace.Status,
                $"scenario {s.Id} replace.status must be a known status");
            foreach (var (key, backend) in s.Augment)
            {
                AllowedAugmentKeys.Should().Contain(key, $"scenario {s.Id} augment key '{key}' must be a known backend");
                ScenarioManifest.AllowedStatuses.Should().Contain(backend.Status,
                    $"scenario {s.Id} augment.{key}.status must be a known status");
            }
        }
    }

    [Fact]
    public void EveryTestScenarioTrait_IsAKnownManifestScenario()
    {
        var known = Manifest.Scenarios.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var unknown = Traits.Select(t => t.ScenarioId).Distinct()
            .Where(id => !known.Contains(id)).ToList();
        unknown.Should().BeEmpty("every [Trait(\"Scenario\",…)] must reference a manifest scenario");
    }

    [Fact]
    public void EveryTestBackendTrait_IsValid()
    {
        var bad = Traits.Select(t => t.Backend).Distinct()
            .Where(b => !AllowedTraitBackends.Contains(b)).ToList();
        bad.Should().BeEmpty("[Trait(\"Backend\",…)] must be Replace or Augment");
    }

    [Fact]
    public void EveryManifestScenario_HasAtLeastOneAcceptanceTest()
    {
        var tested = Traits.Select(t => t.ScenarioId).ToHashSet(StringComparer.Ordinal);
        var missing = Manifest.Scenarios.Select(s => s.Id)
            .Where(id => !tested.Contains(id)).ToList();
        missing.Should().BeEmpty("every manifest scenario must have at least one acceptance test (prevents silent disappearance)");
    }

    [Fact]
    public void PassableReplaceScenarios_HaveANonGapReplaceTest()
    {
        // Rule 6: a scenario declared PASSABLE for a backend must not be backed
        // only by GAP-assertion tests — otherwise "PASSABLE" is unproven.
        foreach (var s in Manifest.Scenarios.Where(s => s.Replace.Status == "PASSABLE"))
        {
            var replaceTests = Traits
                .Where(t => t.ScenarioId == s.Id && t.Backend == ScenarioManifest.ReplaceBackend)
                .ToList();
            replaceTests.Should().Contain(t => !t.IsGap,
                $"scenario {s.Id} is PASSABLE in Replace mode and needs a non-GAP Replace test proving it");
        }
    }

    [Fact]
    public void PassableAugmentScenarios_HaveANonGapAugmentTest()
    {
        foreach (var s in Manifest.Scenarios)
        {
            foreach (var (key, backend) in s.Augment.Where(a => a.Value.Status == "PASSABLE"))
            {
                var augmentTests = Traits
                    .Where(t => t.ScenarioId == s.Id && t.Backend == "Augment" && !t.IsGap)
                    .ToList();
                augmentTests.Should().NotBeEmpty(
                    $"scenario {s.Id} is PASSABLE for augment backend '{key}' and needs a non-GAP Augment test");
            }
        }
    }
}
