using ClaimsService.Models;

namespace ClaimsService.Services;

/// <summary>
/// Builds the editable starting point for an operator correction: the
/// claim's submitted content as an <see cref="AdapterClaim"/>, with every
/// adjudication, payment and version field cleared. The adjustment service
/// overrides identity and version fields again on submit; clearing them here
/// keeps the draft from echoing the old outcome back to the editor.
/// </summary>
public static class ClaimCorrectionDraft
{
    public static AdapterClaim From(Claim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);

        var draft = AdapterClaim.From(claim);
        draft.Status = ClaimStatus.Submitted;
        draft.AdjudicatedDate = null;
        draft.PaidDate = null;
        draft.AdjudicationResult = null;
        draft.PendDetails = null;
        draft.AiExamination = null;
        draft.EDI835ControlNumber = null;
        draft.InboundRemittanceId = null;
        draft.PublishedAt = null;
        draft.PublishedBy = null;
        draft.SupersededAt = null;
        draft.SupersededByVersionId = null;
        foreach (var line in draft.ClaimLines)
        {
            line.AdjudicationResult = null;
        }

        return draft;
    }
}
