using System.Collections.Frozen;
using System.Collections.Immutable;

namespace CloudHealthOffice.GrouperEngine.Severity;

/// <summary>
/// CC exclusion lists held in memory, keyed by principal diagnosis.
///
/// <para>
/// The groups are shared: V44 has 26,168 principal diagnoses pointing at 3,013
/// groups, and the largest group lists 5,565 codes. So the groups are stored
/// once and the index holds references to them rather than copies.
/// </para>
/// </summary>
public sealed class InMemoryExclusionSource : IMsDrgExclusionSource
{
    private readonly FrozenDictionary<string, IReadOnlySet<string>> byPrincipal;

    /// <summary>Builds a source from a principal-diagnosis to group mapping.</summary>
    public InMemoryExclusionSource(
        string version,
        IEnumerable<KeyValuePair<string, IReadOnlySet<string>>> byPrincipalDiagnosis)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(byPrincipalDiagnosis);

        Version = version;
        byPrincipal = byPrincipalDiagnosis.ToFrozenDictionary(
            entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public string Version { get; }

    /// <summary>How many principal diagnoses have an exclusion group.</summary>
    public int PrincipalDiagnosisCount => byPrincipal.Count;

    /// <inheritdoc/>
    public IReadOnlySet<string> ExclusionsFor(string principalDiagnosis)
    {
        ArgumentNullException.ThrowIfNull(principalDiagnosis);

        return byPrincipal.TryGetValue(principalDiagnosis, out var codes)
            ? codes
            : ImmutableHashSet<string>.Empty;
    }
}
