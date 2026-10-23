namespace EnrollmentImportService.Services;

/// <summary>
/// The 834's REF*1L is a policy number carried by more than one group, so it
/// does not say which employer the file is about
/// (docs/architecture/plan-configuration.md §9b).
///
/// <para>
/// A policy number is unique within a group and nothing makes it unique across
/// groups, so this is reachable state rather than corruption. Picking one of
/// the candidates would file a member, their coverage and their premium under
/// the wrong employer silently; the transaction is rejected into the exception
/// queue instead, naming the groups it could have meant so that whoever fixes
/// the data can see the choice.
/// </para>
///
/// <para>
/// Derived from <see cref="InvalidOperationException"/> so that the message —
/// which carries only group numbers already in the file's own trading
/// relationship — is kept on the stored transaction rather than replaced by
/// the generic text reserved for unexpected failures.
/// </para>
/// </summary>
public sealed class AmbiguousSponsorIdentifierException : InvalidOperationException
{
    public AmbiguousSponsorIdentifierException(string value, IReadOnlyList<string> candidateGroupNumbers)
        : base(candidateGroupNumbers.Count > 0
            ? $"REF*1L '{value}' is the policy number of more than one group "
                + $"({string.Join(", ", candidateGroupNumbers)}); it does not say which sponsor is meant."
            : $"REF*1L '{value}' is the policy number of more than one group; it does not say which sponsor is meant.")
    {
        Value = value;
        CandidateGroupNumbers = candidateGroupNumbers;
    }

    /// <summary>The REF*1L value as the file sent it.</summary>
    public string Value { get; }

    /// <summary>The groups holding a policy with that number.</summary>
    public IReadOnlyList<string> CandidateGroupNumbers { get; }
}
