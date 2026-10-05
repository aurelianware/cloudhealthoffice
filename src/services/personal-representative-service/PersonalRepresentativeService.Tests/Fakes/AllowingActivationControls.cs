using PersonalRepresentativeService.Models;
using PersonalRepresentativeService.Services;

namespace PersonalRepresentativeService.Tests.Fakes;

/// <summary>
/// For controller unit tests about the lifecycle itself: every activation is
/// allowed, and a document the credential type needs counts as verified. The
/// activation controls have their own tests.
/// </summary>
public sealed class AllowingActivationControls : IPersonalRepActivationControls
{
    public Task<PersonalRepActivationDecision> EvaluateAsync(
        PersonalRepresentative rep,
        string? requestedDocumentId,
        IReadOnlyCollection<string> associatedMemberIds,
        CancellationToken ct = default)
        => Task.FromResult(new PersonalRepActivationDecision
        {
            Allowed = true,
            StatusCode = 200,
            VerifiedDocumentId = PersonalRepActivationControls.RequiresProofOfAuthority(rep.CredentialType)
                ? requestedDocumentId ?? rep.ProofOfAuthorityDocumentId ?? "doc-test"
                : null
        });
}
