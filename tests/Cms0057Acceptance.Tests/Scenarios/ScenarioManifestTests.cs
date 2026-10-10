using System.Reflection;
using Cms0057Acceptance.Tests.TestSupport;
using FluentAssertions;

namespace Cms0057Acceptance.Tests.Scenarios;

/// <summary>
/// Drift guards for the CMS-0057-F scenario manifest (<c>scenarios.json</c>).
/// These keep the manifest, the acceptance tests' <c>[Trait("Scenario",…)]</c>,
/// and (transitively) the generated evidence from diverging. Replace statuses
/// live in <c>docs/epa-gap-matrix.md</c> §11 and are reconciled against test
/// outcomes by <c>tools/Cms0057Evidence</c>. If a scenario is
/// added/removed/retagged without updating the manifest, one of these fails.
/// </summary>
public class ScenarioManifestTests
{
    private static readonly ScenarioManifest.ManifestDocument Manifest = ScenarioManifest.Load();
    private static readonly IReadOnlyList<ScenarioTrait> Traits =
        TraitScanner.Scan(Assembly.GetExecutingAssembly());

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
        static bool IsValid(string b) =>
            b is "Replace" or "Augment"
            || (b.StartsWith("augment.", System.StringComparison.Ordinal)
                && new HashSet<string>(StringComparer.Ordinal) { "qnxt", "facets", "healthedge" }
                    .Contains(b["augment.".Length..]));

        var bad = Traits.Select(t => t.Backend).Distinct().Where(b => !IsValid(b)).ToList();
        bad.Should().BeEmpty("[Trait(\"Backend\",…)] must be Replace, Augment, or augment.<known-key>");
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
    public void Manifest_DoesNotDeclareReplaceStatus()
    {
        // The Cloud Health Office (Replace) status comes from docs/epa-gap-matrix.md
        // §11 (owner decision Q-1). The evidence generator rejects a manifest that
        // declares it and checks that a PASSABLE row has a passing, non-GAP Replace
        // test; this guard catches a reintroduced field earlier.
        var declaring = Manifest.Scenarios.Where(s => s.Replace is not null).Select(s => s.Id).ToList();
        declaring.Should().BeEmpty("the Replace status lives in docs/epa-gap-matrix.md §11, not in scenarios.json");
    }

    [Fact]
    public void PassableAugmentScenarios_HaveANonGapAugmentTest()
    {
        foreach (var s in Manifest.Scenarios)
        {
            foreach (var (key, backend) in s.Augment.Where(a => a.Value.Status == "PASSABLE"))
            {
                // Mirrors the evidence generator: a key-specific test proves a
                // specific augment backend PASSABLE (a generic "Augment" test does not).
                var augmentTests = Traits
                    .Where(t => t.ScenarioId == s.Id && !t.IsGap
                        && string.Equals(t.Backend, $"augment.{key}", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                augmentTests.Should().NotBeEmpty(
                    $"scenario {s.Id} is PASSABLE for augment backend '{key}' and needs a non-GAP augment.{key} test");
            }
        }
    }
}
