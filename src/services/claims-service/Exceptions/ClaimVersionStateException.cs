using ClaimsService.Models;

namespace ClaimsService.Exceptions;

/// <summary>
/// Thrown when a write violates the claim-version-state invariants — e.g. an
/// attempt to <c>UpdateAsync</c> a row in a terminal state (Paid, Denied,
/// Voided, Adjusted) or a row that doesn't exist. Mirrors
/// <c>ProviderVersionStateException</c> and <c>PlanVersionStateException</c>.
///
/// The controller boundary maps <see cref="IsNotFound"/> to HTTP 404 and
/// everything else to 409.
/// </summary>
public sealed class ClaimVersionStateException : InvalidOperationException
{
    public string ClaimVersionId { get; }
    public string VersionId { get; }
    public ClaimVersionState CurrentState { get; }

    /// <summary>
    /// True when the underlying cause is "the requested claim/version does
    /// not exist", as opposed to a state-machine violation. Set on
    /// construction; controllers map this to HTTP 404 instead of 409.
    /// </summary>
    public bool IsNotFound { get; init; }

    public ClaimVersionStateException(
        string claimVersionId,
        string versionId,
        ClaimVersionState currentState,
        string message)
        : base(message)
    {
        ClaimVersionId = claimVersionId;
        VersionId = versionId;
        CurrentState = currentState;
    }
}
