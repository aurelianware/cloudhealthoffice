namespace CloudHealthOffice.GrouperEngine.Mask;

/// <summary>
/// A procedure cluster: a combination of procedure codes that, taken together,
/// qualifies a stay for something no one of them qualifies it for alone.
///
/// <para>
/// A cluster is satisfied when the claim carries a code from <i>every</i>
/// choice group. Most clusters have two groups — a replacement paired with a
/// removal, say — and a handful have up to eight. When one is satisfied, its
/// own code joins the claim's procedures and contributes that code's operands;
/// the codes that satisfied it are then marked as not counting in the MDCs the
/// cluster restricts, so the stay is not credited twice for the combination
/// and for its parts.
/// </para>
/// </summary>
/// <param name="ClusterCode">
/// The cluster's own code, which CMS writes as <c>Z@</c> and four digits. It
/// is looked up in the <i>procedure</i> table like any other procedure code,
/// because to the rest of the grouper that is what it now is.
/// </param>
/// <param name="Choices">
/// The choice groups, in CMS's order. Each is the set of codes that satisfies
/// that group.
/// </param>
/// <param name="RestrictedMdcs">
/// The MDCs in which the contributing codes' own operands stop counting.
/// </param>
public sealed record MsDrgCluster(
    string ClusterCode,
    IReadOnlyList<IReadOnlySet<string>> Choices,
    IReadOnlySet<int> RestrictedMdcs);

/// <summary>
/// The cluster definitions a stay's procedures might satisfy.
///
/// <para>
/// Separate from <see cref="IMsDrgCodeAttributeSource"/> because the lookup is
/// the other way round: not "what does this code mean" but "which clusters
/// could this code be part of". Only about 600 of CMS's 89,000 procedure codes
/// appear in a cluster at all, so a claim usually needs no cluster work.
/// </para>
/// </summary>
public interface IMsDrgClusterSource
{
    /// <summary>
    /// Every cluster that at least one of <paramref name="procedureCodes"/>
    /// appears in. Order is not significant; a cluster is returned once
    /// however many of the codes it mentions.
    /// </summary>
    IReadOnlyCollection<MsDrgCluster> ClustersMentioning(IEnumerable<string> procedureCodes);

    /// <summary>A source with no clusters, for a stay that needs none.</summary>
    static IMsDrgClusterSource None { get; } = new NoClusters();

    private sealed class NoClusters : IMsDrgClusterSource
    {
        public IReadOnlyCollection<MsDrgCluster> ClustersMentioning(
            IEnumerable<string> procedureCodes) => [];
    }
}
