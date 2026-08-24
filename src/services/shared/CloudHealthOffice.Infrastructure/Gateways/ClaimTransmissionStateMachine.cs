using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Legal transmission transitions for the 277CA lifecycle. Backward moves
/// to submitting/transmitting are rejected. Malformed acknowledgments cannot
/// change an existing acknowledgment outcome.
/// </summary>
internal static class ClaimTransmissionStateMachine
{
    public static bool TryTransition(
        GatewayClaimTransmissionStatus current,
        GatewayClaimTransmissionStatus proposed,
        ClaimAcknowledgmentStatus acknowledgmentStatus,
        out GatewayClaimTransmissionStatus next)
    {
        next = current;
        if (current == proposed)
        {
            return false;
        }

        if (IsSubmitPhase(proposed))
        {
            return false;
        }

        if (acknowledgmentStatus == ClaimAcknowledgmentStatus.Malformed)
        {
            if (IsAcknowledgmentOutcome(current))
            {
                return false;
            }

            if (proposed == GatewayClaimTransmissionStatus.AcknowledgmentFailed &&
                IsAwaitingAcknowledgment(current))
            {
                next = proposed;
                return true;
            }

            return false;
        }

        if (!IsAcknowledgmentOutcome(proposed))
        {
            return false;
        }

        if (IsAwaitingAcknowledgment(current) || IsAcknowledgmentOutcome(current))
        {
            next = proposed;
            return true;
        }

        return false;
    }

    public static bool IsAwaitingAcknowledgment(GatewayClaimTransmissionStatus status) =>
        status is GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway
            or GatewayClaimTransmissionStatus.Transmitted
            or GatewayClaimTransmissionStatus.AwaitingAcknowledgment
            or GatewayClaimTransmissionStatus.AcknowledgmentFailed;

    public static bool IsAcknowledgmentOutcome(GatewayClaimTransmissionStatus status) =>
        status is GatewayClaimTransmissionStatus.AcknowledgmentAccepted
            or GatewayClaimTransmissionStatus.AcknowledgmentRejected
            or GatewayClaimTransmissionStatus.AcknowledgmentPartial;

    private static bool IsSubmitPhase(GatewayClaimTransmissionStatus status) =>
        status is GatewayClaimTransmissionStatus.ReadyForSubmission
            or GatewayClaimTransmissionStatus.Queued
            or GatewayClaimTransmissionStatus.Transmitting
            or GatewayClaimTransmissionStatus.Failed
            or GatewayClaimTransmissionStatus.SubmissionRejectedByGateway;
}
