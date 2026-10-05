using ClaimsService.Models;

namespace ClaimsService.Services;

/// <summary>
/// Submission endpoints accept a full claim body. Its lifecycle, version-chain,
/// adjudication and audit fields belong to the server: a new claim always
/// starts as a fresh Submitted version with no adjudication, pend, AI or
/// payment state, created by the authenticated caller.
/// </summary>
public static class ClaimSubmissionInput
{
    public static void ResetServerOwnedFields(AdapterClaim claim, string actorId)
    {
        ArgumentNullException.ThrowIfNull(claim);

        claim.Status = ClaimStatus.Submitted;
        claim.ClaimVersionId = string.Empty;   // CreateAsync starts a new chain
        claim.VersionNumber = 0;
        claim.VersionState = ClaimVersionState.Submitted;
        claim.PredecessorVersionId = null;
        claim.PublishedAt = null;
        claim.PublishedBy = null;
        claim.SupersededAt = null;
        claim.SupersededByVersionId = null;
        claim.AdjudicatedDate = null;
        claim.PaidDate = null;
        claim.AdjudicationResult = null;
        claim.PendDetails = null;
        claim.AiExamination = null;
        claim.EDI835ControlNumber = null;
        claim.InboundRemittanceId = null;
        claim.CreatedBy = actorId;
        claim.LastUpdatedBy = actorId;
    }
}
