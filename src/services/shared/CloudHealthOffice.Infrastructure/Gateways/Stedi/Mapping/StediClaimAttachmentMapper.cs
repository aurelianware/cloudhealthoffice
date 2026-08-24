using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Gateways.Stedi.Models;

namespace CloudHealthOffice.Infrastructure.Gateways.Stedi.Mapping;

/// <summary>
/// Maps canonical 275 attachment models to Stedi's documented JSON contract.
/// Create-file only sends <c>contentType</c>. Report-type codes are the X12
/// PWK01 values Stedi uses when the 837 later references the attachment.
/// </summary>
internal static class StediClaimAttachmentMapper
{
    public static StediCreateClaimAttachmentRequestDto ToCreateFileRequest(
        ClaimAttachmentSubmissionRequest request)
    {
        var contentType = ClaimAttachmentRules.NormalizeContentType(request.ContentType);
        if (contentType == "image/jpg")
        {
            contentType = "image/jpeg";
        }

        return new StediCreateClaimAttachmentRequestDto { ContentType = contentType };
    }

    public static string ToAttachmentReportTypeCode(ClaimAttachmentType type) => type switch
    {
        ClaimAttachmentType.MedicalRecord => "M1",
        ClaimAttachmentType.ClinicalNote => "PN",
        ClaimAttachmentType.OperativeReport => "OB",
        ClaimAttachmentType.DiagnosticImage => "RB",
        ClaimAttachmentType.LabResult => "LA",
        ClaimAttachmentType.Referral => "B4",
        ClaimAttachmentType.AuthorizationDocumentation => "CT",
        ClaimAttachmentType.DentalImage => "DA",
        ClaimAttachmentType.DentalNarrative => "OZ",
        ClaimAttachmentType.Radiograph => "RB",
        ClaimAttachmentType.IntraoralImage => "XP",
        ClaimAttachmentType.PeriodontalChart => "P6",
        ClaimAttachmentType.TreatmentPlan => "08",
        _ => "OZ"
    };

    /// <summary>PWK02 / attachmentTransmissionCode for Stedi electronic 275.</summary>
    public const string AttachmentTransmissionCode = "EL";

    public static ClaimAttachmentSubmissionResult ToCanonical(
        ClaimAttachmentSubmissionRequest request,
        StediCreateClaimAttachmentResponseDto response,
        string attachmentTransmissionId,
        string idempotencyKey,
        bool replay) =>
        new()
        {
            AttachmentId = request.AttachmentId,
            AttachmentTransmissionId = attachmentTransmissionId,
            TransmissionId = request.TransmissionId,
            ClaimId = request.ClaimId,
            Status = ClaimAttachmentTransmissionStatus.GatewayAccepted,
            AttachmentType = request.AttachmentType,
            Mode = request.Mode,
            AssociationLevel = request.AssociationLevel,
            ServiceLineNumber = request.ServiceLineNumber,
            ContentType = ClaimAttachmentRules.NormalizeContentType(request.ContentType),
            ContentLength = request.Content?.ContentLength ?? request.ContentLength,
            ChecksumSha256 = request.Content?.ChecksumSha256,
            ExternalTransactionId = response.AttachmentId,
            AttachmentControlNumber = request.AttachmentControlNumber,
            IdempotencyKey = idempotencyKey,
            AcceptedForProcessing = true,
            ReplayOfExistingTransmission = replay
        };
}
