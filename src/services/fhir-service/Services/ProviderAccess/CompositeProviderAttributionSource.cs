namespace FhirService.Services.ProviderAccess;

/// <summary>
/// Attribution from more than one source, where any one of them is enough.
///
/// <para>
/// The two sources answer different questions and neither subsumes the other.
/// A configured panel is an <em>assignment</em>: a member allocated to a PCP
/// who may not have been seen yet, which no claim can evidence. Claims history
/// is <em>treatment that happened</em>: the specialist who operated last month
/// and is on no panel CHO has been given. A plan with both should be served by
/// both, so this is a disjunction and deliberately not a precedence order.
/// </para>
///
/// <para>
/// Fail-closed is preserved, and that is the reason this is not a plain
/// <c>Any()</c> over the sources. A source that throws has not said "no" — it
/// has said nothing — so the composite rethrows rather than letting a later
/// source's "no" stand in for an answer the failed one never gave, and rather
/// than letting an earlier source's "yes" hide that another could not be
/// reached. Sources are therefore asked in order and the first fault stops the
/// evaluation; <see cref="ProviderAccessAuthorizationService"/> turns it into a
/// denial.
/// </para>
/// </summary>
public sealed class CompositeProviderAttributionSource : IProviderAttributionSource
{
    private readonly IReadOnlyList<IProviderAttributionSource> _sources;

    public CompositeProviderAttributionSource(IEnumerable<IProviderAttributionSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = sources.ToList();

        if (_sources.Count == 0)
        {
            // No source attributes no one, which is the correct answer, but it
            // is an answer a deployment should never reach by accident.
            throw new ArgumentException(
                "A composite attribution source needs at least one source.", nameof(sources));
        }
    }

    public async Task<bool> IsAttributedAsync(
        string tenantId, string providerId, string memberId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId)
            || string.IsNullOrWhiteSpace(providerId)
            || string.IsNullOrWhiteSpace(memberId))
            return false;

        var attributed = false;

        foreach (var source in _sources)
        {
            // Every source is asked even once one has said yes: a source that
            // cannot be read is a fact about the decision, and skipping the
            // rest on a yes would make the outcome depend on the order the
            // sources happen to be registered in.
            if (await source.IsAttributedAsync(tenantId, providerId, memberId, ct))
                attributed = true;
        }

        return attributed;
    }

    /// <summary>
    /// Everyone any source lists. As with <see cref="IsAttributedAsync"/>, a
    /// source that throws stops the listing rather than letting a partial
    /// panel stand in for the whole one.
    /// </summary>
    public async Task<IReadOnlyCollection<string>> ListAttributedAsync(
        string tenantId, string providerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(providerId))
            return Array.Empty<string>();

        var members = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var source in _sources)
            members.UnionWith(await source.ListAttributedAsync(tenantId, providerId, ct));
        return members;
    }
}
