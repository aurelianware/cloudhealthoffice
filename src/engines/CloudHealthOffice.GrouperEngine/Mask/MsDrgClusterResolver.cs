namespace CloudHealthOffice.GrouperEngine.Mask;

/// <summary>
/// One cluster a stay satisfied, and the codes that satisfied it.
/// </summary>
/// <param name="Cluster">The cluster definition.</param>
/// <param name="ContributingCodes">
/// The procedure codes that matched a choice group while the cluster was being
/// tested. These are the codes whose own operands stop counting in the
/// cluster's restricted MDCs.
/// </param>
public sealed record SatisfiedCluster(
    MsDrgCluster Cluster,
    IReadOnlySet<string> ContributingCodes);

/// <summary>
/// Works out which procedure clusters a stay satisfies.
///
/// <para>
/// A port of CMS's cluster pass, and faithful to it including the parts that
/// look accidental. In particular CMS walks the claim's procedures in order,
/// collecting the codes that match any choice group, and stops the moment
/// every group is covered — so the contributing set is "the codes seen up to
/// the point the cluster completed", not "the codes that matched a group". On
/// a claim where an early procedure matches a group the cluster did not need,
/// that code is still marked as contributing and its operands are still
/// suppressed. Reproducing that is the difference between agreeing with CMS
/// and being defensibly different from it.
/// </para>
/// </summary>
public static class MsDrgClusterResolver
{
    /// <summary>
    /// The clusters <paramref name="procedureCodes"/> satisfy, in the order
    /// CMS would find them.
    /// </summary>
    public static IReadOnlyList<SatisfiedCluster> Resolve(
        IMsDrgClusterSource clusters, IReadOnlyList<string> procedureCodes)
    {
        ArgumentNullException.ThrowIfNull(clusters);
        ArgumentNullException.ThrowIfNull(procedureCodes);

        if (procedureCodes.Count == 0)
        {
            return [];
        }

        var satisfied = new List<SatisfiedCluster>();

        foreach (var cluster in clusters.ClustersMentioning(procedureCodes))
        {
            if (cluster.Choices.Count == 0)
            {
                continue;
            }

            var covered = new bool[cluster.Choices.Count];
            var remaining = cluster.Choices.Count;
            var contributing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var code in procedureCodes)
            {
                var group = ChoiceGroupFor(cluster, code);
                if (group >= 0)
                {
                    contributing.Add(code);

                    if (!covered[group])
                    {
                        covered[group] = true;
                        remaining--;
                    }
                }

                if (remaining == 0)
                {
                    satisfied.Add(new SatisfiedCluster(cluster, contributing));
                    break;
                }
            }
        }

        // CMS sorts the resulting cluster codes by value, which is what makes
        // its mask order reproducible across runs.
        return satisfied
            .OrderBy(entry => entry.Cluster.ClusterCode, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The index of the first choice group <paramref name="code"/> satisfies,
    /// or -1. First rather than all: CMS's own lookup returns a single choice
    /// number per code.
    /// </summary>
    private static int ChoiceGroupFor(MsDrgCluster cluster, string code)
    {
        for (var group = 0; group < cluster.Choices.Count; group++)
        {
            if (cluster.Choices[group].Contains(code))
            {
                return group;
            }
        }

        return -1;
    }
}
