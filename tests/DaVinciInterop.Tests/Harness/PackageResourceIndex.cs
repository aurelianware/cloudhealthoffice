using Hl7.Fhir.Model;

namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// Indexes the conformance resources in a package Bundle by canonical URL, so a
/// scenario can ask whether the dependencies a resource names are actually
/// present.
///
/// Protocol-neutral: DTR ships a questionnaire package, but CRD and PAS also hand
/// back bundles whose internal references need to resolve, so nothing here knows
/// about DTR.
///
/// Canonical matching follows FHIR convention: a reference may carry a
/// <c>|version</c> suffix, and a versionless reference matches any version of the
/// resource. Versions are never normalised away — <see cref="VersionMismatches"/>
/// reports a dependency that resolved only by ignoring the version it asked for,
/// because that is a real interoperability observation rather than a detail to
/// paper over.
/// </summary>
public sealed class PackageResourceIndex
{
    private readonly Dictionary<string, List<Resource>> _byCanonical = new(StringComparer.Ordinal);
    private readonly List<Resource> _resources = new();

    public PackageResourceIndex(Bundle? bundle)
    {
        foreach (var resource in bundle?.Entry.Select(entry => entry.Resource).OfType<Resource>()
                                ?? Enumerable.Empty<Resource>())
        {
            _resources.Add(resource);
            var canonical = CanonicalOf(resource);
            if (canonical is null)
            {
                continue;
            }

            if (!_byCanonical.TryGetValue(canonical, out var list))
            {
                list = new List<Resource>();
                _byCanonical[canonical] = list;
            }

            list.Add(resource);
        }
    }

    /// <summary>Every resource in the package, in bundle order.</summary>
    public IReadOnlyList<Resource> Resources => _resources;

    /// <summary>Count of each resourceType present — the package inventory.</summary>
    public IReadOnlyDictionary<string, int> ResourceTypeCounts =>
        _resources.GroupBy(resource => resource.TypeName)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    /// <summary>Canonical URLs the package defines, ordered for stable evidence.</summary>
    public IReadOnlyList<string> Canonicals =>
        _byCanonical.Keys.OrderBy(url => url, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Canonicals present more than once. A package that defines the same canonical
    /// twice leaves a consumer to pick, so it is worth reporting rather than
    /// silently taking the first.
    /// </summary>
    public IReadOnlyList<string> DuplicateCanonicals =>
        _byCanonical.Where(entry => entry.Value.Count > 1)
            .Select(entry => entry.Key)
            .OrderBy(url => url, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Resolves a canonical reference, honouring a <c>|version</c> suffix when one
    /// is present. Returns null when the package does not contain it.
    /// </summary>
    public Resource? Resolve(string? canonicalReference)
    {
        if (string.IsNullOrWhiteSpace(canonicalReference))
        {
            return null;
        }

        var (url, version) = SplitCanonical(canonicalReference);
        if (!_byCanonical.TryGetValue(url, out var candidates))
        {
            return null;
        }

        if (version is null)
        {
            return candidates[0];
        }

        return candidates.FirstOrDefault(resource => VersionOf(resource) == version);
    }

    /// <summary>
    /// Of <paramref name="references"/>, those whose canonical the package does not
    /// contain at any version.
    ///
    /// A reference whose canonical IS present but at a different version is not
    /// reported here — it is a <see cref="VersionMismatches">version mismatch</see>.
    /// The two have different consequences and different fixes, and reporting a
    /// mismatch as "missing" would send a reader looking for a resource that is
    /// sitting in the package.
    /// </summary>
    public IReadOnlyList<string> UnresolvedReferences(IEnumerable<string> references) =>
        references
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Where(reference => !_byCanonical.ContainsKey(SplitCanonical(reference).Url))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(reference => reference, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// References that resolve only if the requested version is disregarded: the
    /// canonical is present but at a different version. Distinct from unresolved,
    /// because the consequence differs — a consumer gets a resource, just not the
    /// one that was asked for.
    /// </summary>
    public IReadOnlyList<string> VersionMismatches(IEnumerable<string> references)
    {
        var mismatches = new List<string>();
        foreach (var reference in references.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.Ordinal))
        {
            var (url, version) = SplitCanonical(reference);
            if (version is null || !_byCanonical.TryGetValue(url, out var candidates))
            {
                continue;
            }

            if (candidates.All(resource => VersionOf(resource) != version))
            {
                var present = string.Join(", ", candidates.Select(c => VersionOf(c) ?? "(no version)"));
                mismatches.Add($"{reference} — package carries version(s): {present}");
            }
        }

        return mismatches;
    }

    /// <summary>
    /// The canonicals a Questionnaire depends on: its CQF library, the value sets
    /// its items bind to, and any sub-questionnaires it assembles. Walks nested
    /// items, since a dependency several levels down is no less required.
    /// </summary>
    public static IReadOnlyList<string> QuestionnaireDependencies(Questionnaire? questionnaire)
    {
        if (questionnaire is null)
        {
            return Array.Empty<string>();
        }

        var dependencies = new List<string>();

        dependencies.AddRange(questionnaire.Extension
            .Where(extension => extension.Url == CqfLibraryExtension)
            .Select(extension => (extension.Value as Canonical)?.Value)
            .Where(value => value is not null)!);

        void Walk(IEnumerable<Questionnaire.ItemComponent> items)
        {
            foreach (var item in items)
            {
                if (!string.IsNullOrWhiteSpace(item.AnswerValueSet))
                {
                    dependencies.Add(item.AnswerValueSet);
                }

                dependencies.AddRange(item.Extension
                    .Where(extension => extension.Url == SubQuestionnaireExtension)
                    .Select(extension => (extension.Value as Canonical)?.Value)
                    .Where(value => value is not null)!);

                Walk(item.Item);
            }
        }

        Walk(questionnaire.Item);

        return dependencies.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>The CQF library extension a Questionnaire uses to name its CQL.</summary>
    public const string CqfLibraryExtension = "http://hl7.org/fhir/StructureDefinition/cqf-library";

    /// <summary>The SDC extension a Questionnaire uses to assemble a sub-questionnaire.</summary>
    public const string SubQuestionnaireExtension =
        "http://hl7.org/fhir/uv/sdc/StructureDefinition/sdc-questionnaire-subQuestionnaire";

    private static (string Url, string? Version) SplitCanonical(string canonical)
    {
        var separator = canonical.LastIndexOf('|');
        return separator < 0
            ? (canonical, null)
            : (canonical[..separator], canonical[(separator + 1)..]);
    }

    private static string? CanonicalOf(Resource resource) => resource switch
    {
        Questionnaire questionnaire => questionnaire.Url,
        Library library => library.Url,
        ValueSet valueSet => valueSet.Url,
        CodeSystem codeSystem => codeSystem.Url,
        PlanDefinition planDefinition => planDefinition.Url,
        StructureDefinition structureDefinition => structureDefinition.Url,
        _ => null,
    };

    private static string? VersionOf(Resource resource) => resource switch
    {
        Questionnaire questionnaire => questionnaire.Version,
        Library library => library.Version,
        ValueSet valueSet => valueSet.Version,
        CodeSystem codeSystem => codeSystem.Version,
        PlanDefinition planDefinition => planDefinition.Version,
        StructureDefinition structureDefinition => structureDefinition.Version,
        _ => null,
    };
}
