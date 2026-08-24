using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Derives a business lifecycle from stacked transaction truth. Later
/// transactions do not erase earlier ones: 277CA Accepted is never Paid,
/// and 276/277 Paid does not invent an 835.
/// </summary>
internal static class ClaimIntelligenceMapper
{
    public static ClaimIntelligenceLifecycleStatus MapLifecycle(
        ClaimTransmissionRecord? transmission,
        ClaimAcknowledgmentRecord? acknowledgment,
        ClaimStatusInquiryRecord? status,
        RemittanceReceipt? remittance,
        RemittedClaim? remittedClaim)
    {
        if (remittance is not null &&
            remittedClaim is not null &&
            remittedClaim.MatchStatus == RemittanceClaimMatchStatus.Matched &&
            remittance.Status is RemittanceLifecycleStatus.AvailableForPosting
                or RemittanceLifecycleStatus.Matched)
        {
            return MapFromRemittance(remittedClaim);
        }

        if (status is not null && status.NormalizedStatus != GatewayClaimStatus.Unknown
            && status.NormalizedStatus != GatewayClaimStatus.NoRecordFound)
        {
            return MapFromClaimStatus(status.NormalizedStatus);
        }

        if (acknowledgment is not null)
        {
            return MapFromAcknowledgment(acknowledgment.Status);
        }

        if (transmission is not null)
        {
            return MapFromTransmission(transmission.Status);
        }

        return ClaimIntelligenceLifecycleStatus.Unknown;
    }

    public static ClaimIntelligenceNextAction MapNextAction(
        ClaimIntelligenceLifecycleStatus lifecycle,
        ClaimStatusInquiryRecord? status,
        ClaimIntelligenceAttachmentSummary attachments)
    {
        if (lifecycle is ClaimIntelligenceLifecycleStatus.Denied)
        {
            return ClaimIntelligenceNextAction.CorrectAndResubmit;
        }

        if (lifecycle is ClaimIntelligenceLifecycleStatus.Paid
            or ClaimIntelligenceLifecycleStatus.PartiallyPaid
            or ClaimIntelligenceLifecycleStatus.Completed)
        {
            return ClaimIntelligenceNextAction.ReadyForPosting;
        }

        if (lifecycle == ClaimIntelligenceLifecycleStatus.PendingInformation ||
            status?.NormalizedStatus is GatewayClaimStatus.Pending
                or GatewayClaimStatus.AdditionalInformationRequested ||
            (attachments.Requested && !attachments.Received))
        {
            return ClaimIntelligenceNextAction.ProvideInformation;
        }

        if (lifecycle is ClaimIntelligenceLifecycleStatus.Draft
            or ClaimIntelligenceLifecycleStatus.Submitted
            or ClaimIntelligenceLifecycleStatus.AcceptedByClearinghouse)
        {
            return ClaimIntelligenceNextAction.WaitForClearinghouse;
        }

        if (lifecycle is ClaimIntelligenceLifecycleStatus.AcceptedByPayer
            or ClaimIntelligenceLifecycleStatus.Processing)
        {
            return ClaimIntelligenceNextAction.WaitForPayer;
        }

        return ClaimIntelligenceNextAction.None;
    }

    public static string MapExpected(ClaimIntelligenceLifecycleStatus lifecycle) =>
        lifecycle switch
        {
            ClaimIntelligenceLifecycleStatus.Draft => "Submit claim",
            ClaimIntelligenceLifecycleStatus.Submitted => "Pending 277CA",
            ClaimIntelligenceLifecycleStatus.AcceptedByClearinghouse => "Pending payer acknowledgment",
            ClaimIntelligenceLifecycleStatus.AcceptedByPayer => "Pending ERA",
            ClaimIntelligenceLifecycleStatus.Processing => "Pending ERA",
            ClaimIntelligenceLifecycleStatus.PendingInformation => "Payer requested information",
            ClaimIntelligenceLifecycleStatus.Denied => "Correction required",
            ClaimIntelligenceLifecycleStatus.Paid => "Ready for posting",
            ClaimIntelligenceLifecycleStatus.PartiallyPaid => "Ready for posting",
            ClaimIntelligenceLifecycleStatus.Completed => "Complete",
            _ => "Unknown"
        };

    public static List<string> MissingLinks(
        ClaimTransmissionRecord? transmission,
        ClaimAcknowledgmentRecord? acknowledgment,
        ClaimStatusInquiryRecord? status,
        RemittanceReceipt? remittance)
    {
        var missing = new List<string>();
        if (transmission is null)
        {
            missing.Add("837");
            return missing;
        }

        if (acknowledgment is null)
        {
            missing.Add("277CA");
        }

        if (status is null)
        {
            missing.Add("276277");
        }

        if (remittance is null)
        {
            missing.Add("835");
        }

        return missing;
    }

    private static ClaimIntelligenceLifecycleStatus MapFromRemittance(RemittedClaim claim)
    {
        if (IsDeniedRemittance(claim))
        {
            return ClaimIntelligenceLifecycleStatus.Denied;
        }

        if (IsPartialRemittance(claim))
        {
            return ClaimIntelligenceLifecycleStatus.PartiallyPaid;
        }

        return ClaimIntelligenceLifecycleStatus.Paid;
    }

    private static bool IsDeniedRemittance(RemittedClaim claim) =>
        string.Equals(claim.ClaimStatusCode, "4", StringComparison.Ordinal) ||
        (claim.PaidAmount <= 0 && claim.ChargedAmount > 0);

    private static bool IsPartialRemittance(RemittedClaim claim) =>
        claim.ClaimStatusCode is "2" or "3" or "19" or "20" or "21";

    private static ClaimIntelligenceLifecycleStatus MapFromClaimStatus(GatewayClaimStatus status) =>
        status switch
        {
            GatewayClaimStatus.Denied or GatewayClaimStatus.Rejected =>
                ClaimIntelligenceLifecycleStatus.Denied,
            GatewayClaimStatus.Pending or GatewayClaimStatus.AdditionalInformationRequested =>
                ClaimIntelligenceLifecycleStatus.PendingInformation,
            // 276/277 Paid/Finalized is payer status — it does not create an 835.
            GatewayClaimStatus.Paid or GatewayClaimStatus.PartiallyPaid
                or GatewayClaimStatus.Finalized or GatewayClaimStatus.InProcess
                or GatewayClaimStatus.Received or GatewayClaimStatus.Accepted =>
                ClaimIntelligenceLifecycleStatus.Processing,
            _ => ClaimIntelligenceLifecycleStatus.Processing
        };

    private static ClaimIntelligenceLifecycleStatus MapFromAcknowledgment(
        ClaimAcknowledgmentStatus status) =>
        status switch
        {
            ClaimAcknowledgmentStatus.Rejected => ClaimIntelligenceLifecycleStatus.Denied,
            ClaimAcknowledgmentStatus.Partial => ClaimIntelligenceLifecycleStatus.PendingInformation,
            ClaimAcknowledgmentStatus.Accepted or ClaimAcknowledgmentStatus.AcceptedWithWarnings =>
                ClaimIntelligenceLifecycleStatus.AcceptedByPayer,
            _ => ClaimIntelligenceLifecycleStatus.AcceptedByClearinghouse
        };

    private static ClaimIntelligenceLifecycleStatus MapFromTransmission(
        GatewayClaimTransmissionStatus status) =>
        status switch
        {
            GatewayClaimTransmissionStatus.ReadyForSubmission
                or GatewayClaimTransmissionStatus.Queued
                or GatewayClaimTransmissionStatus.Transmitting =>
                ClaimIntelligenceLifecycleStatus.Draft,
            GatewayClaimTransmissionStatus.SubmissionRejectedByGateway
                or GatewayClaimTransmissionStatus.Failed
                or GatewayClaimTransmissionStatus.AcknowledgmentRejected =>
                ClaimIntelligenceLifecycleStatus.Denied,
            GatewayClaimTransmissionStatus.AcknowledgmentAccepted
                or GatewayClaimTransmissionStatus.AcknowledgmentPartial =>
                ClaimIntelligenceLifecycleStatus.AcceptedByPayer,
            GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway
                or GatewayClaimTransmissionStatus.Transmitted
                or GatewayClaimTransmissionStatus.AwaitingAcknowledgment =>
                ClaimIntelligenceLifecycleStatus.AcceptedByClearinghouse,
            _ => ClaimIntelligenceLifecycleStatus.Submitted
        };
}
