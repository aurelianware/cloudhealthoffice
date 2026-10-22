using System.Collections.Frozen;

namespace CloudHealthOffice.GrouperEngine.Mask;

/// <summary>
/// Cluster definitions held in memory, indexed by the codes that appear in
/// them.
///
/// <para>
/// The index is what makes the cluster pass cheap. CMS has about 4,100
/// clusters for V44 but only some 600 procedure codes appear in any of them,
/// so a claim whose procedures are all outside that set needs no cluster work
/// at all — which is most claims.
/// </para>
/// </summary>
public sealed class InMemoryClusterSource : IMsDrgClusterSource
{
    private readonly FrozenDictionary<string, MsDrgCluster[]> byCode;

    /// <summary>Indexes the given clusters.</summary>
    public InMemoryClusterSource(IEnumerable<MsDrgCluster> clusters)
    {
        ArgumentNullException.ThrowIfNull(clusters);

        var index = new Dictionary<string, List<MsDrgCluster>>(StringComparer.OrdinalIgnoreCase);

        foreach (var cluster in clusters)
        {
            foreach (var choice in cluster.Choices)
            {
                foreach (var code in choice)
                {
                    if (!index.TryGetValue(code, out var mentioning))
                    {
                        mentioning = [];
                        index[code] = mentioning;
                    }

                    mentioning.Add(cluster);
                }
            }
        }

        byCode = index.ToFrozenDictionary(
            entry => entry.Key,
            entry => entry.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>How many codes appear in at least one cluster.</summary>
    public int IndexedCodeCount => byCode.Count;

    /// <summary>
    /// Every code that appears in at least one cluster. For checking an
    /// imported release over, not for grouping a claim.
    /// </summary>
    public IEnumerable<string> AllIndexedCodes => byCode.Keys;

    /// <inheritdoc/>
    public IReadOnlyCollection<MsDrgCluster> ClustersMentioning(
        IEnumerable<string> procedureCodes)
    {
        ArgumentNullException.ThrowIfNull(procedureCodes);

        // A cluster mentioned by two of the claim's codes is still one cluster,
        // and reference identity is the right key here: the same definition
        // object is indexed under each of its codes.
        var found = new HashSet<MsDrgCluster>(ReferenceEqualityComparer.Instance);

        foreach (var code in procedureCodes)
        {
            if (byCode.TryGetValue(code, out var mentioning))
            {
                foreach (var cluster in mentioning)
                {
                    found.Add(cluster);
                }
            }
        }

        return found;
    }
}
