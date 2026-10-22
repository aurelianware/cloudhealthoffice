namespace CloudHealthOffice.GrouperEngine.Severity;

/// <summary>
/// CMS's CC exclusion lists: for a principal diagnosis, the secondary
/// diagnoses that do not count towards the stay's severity.
///
/// <para>
/// This is the mechanism that stops a stay being paid twice for one clinical
/// fact. A secondary that is merely another way of saying the principal — or
/// its expected consequence — is excluded, so it cannot raise the stay from no
/// complication to a CC or an MCC. Omitting it does not produce an error; it
/// produces a higher-paying DRG than the claim is entitled to, on a large
/// share of stays. V44 lists an exclusion group for 26,168 principal
/// diagnoses.
/// </para>
///
/// <para>
/// CMS compares the secondary's code against the group's codes exactly, and
/// the group is chosen by principal diagnosis and grouper version.
/// </para>
/// </summary>
public interface IMsDrgExclusionSource
{
    /// <summary>The grouper version these lists came from.</summary>
    string Version { get; }

    /// <summary>
    /// The secondary diagnoses excluded by <paramref name="principalDiagnosis"/>.
    /// Empty when the principal has no exclusion group, which is the common
    /// case for codes CMS has no exclusions for.
    /// </summary>
    IReadOnlySet<string> ExclusionsFor(string principalDiagnosis);

    /// <summary>
    /// A source with no lists at all. Deliberately not the default anywhere a
    /// claim is grouped: see the type's remarks for what its absence costs.
    /// </summary>
    public static IMsDrgExclusionSource None { get; } = new NoExclusions();

    private sealed class NoExclusions : IMsDrgExclusionSource
    {
        public string Version => string.Empty;

        public IReadOnlySet<string> ExclusionsFor(string principalDiagnosis) =>
            System.Collections.Immutable.ImmutableHashSet<string>.Empty;
    }
}
