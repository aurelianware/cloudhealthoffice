using ClaimsService.Models;

namespace ClaimsService.Exceptions;

/// <summary>
/// Thrown when a write violates the claim-version-state invariants — e.g. an
/// attempt to <c>UpdateAsync</c> a row in a terminal state (Paid, Denied,
/// Voided, Adjusted) or a row that doesn't exist. Mirrors
/// <c>ProviderVersionStateException</c> and <c>PlanVersionStateException</c>.
///
/// 5.1 does NOT introduce a controller-boundary mapping — claims-service
/// controllers continue to flow through <c>ExceptionHandlingMiddleware</c>,
/// which renders <see cref="InvalidOperationException"/> as HTTP 500.
/// Capability 5.3 (Submission API refactor) introduces the explicit
/// 404/409 mapping that consumes <see cref="IsNotFound"/>; until then,
/// the structured fields here exist for downstream consumers (5.5
/// adjudication, 5.12 adjustment workflow) to inspect programmatically.
/// </summary>
public sealed class ClaimVersionStateException : InvalidOperationException
{
    public string ClaimVersionId { get; }
    public string VersionId { get; }
    public ClaimVersionState CurrentState { get; }

    /// <summary>
    /// True when the underlying cause is "the requested claim/version does
    /// not exist", as opposed to a state-machine violation. Set on
    /// construction; capability 5.3 will surface this as HTTP 404 once
    /// the controller boundary is refactored.
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
