using AppealsService.Models;

namespace AppealsService.Services;

/// <summary>
/// Pure mapping from a Kafka-published <see cref="Attachment275EnvelopeDto"/>
/// to an <see cref="AppealAttachment"/> ready for repository append.
/// Stateless and side-effect-free — encryption of the
/// <see cref="AppealAttachment.Description"/> field is the caller's
/// responsibility (the consumer resolves
/// <see cref="IAppealFieldEncryptor"/> from its per-message scope and
/// passes ciphertext via <paramref name="encryptedDescription"/>).
/// </summary>
public sealed class Attachment275EnvelopeMapper
{
    /// <summary>X12 PWK02 default per the
    /// <c>cho-appeal-x12-275-transmission-code</c> CodeSystem
    /// (electronically transmitted).</summary>
    public const string DefaultTransmissionCode = "EL";

    /// <summary>X12 PWK01 default per the report-type-code list — used
    /// when the upstream <c>documentType</c> is null or unmapped.</summary>
    public const string DefaultAttachmentTypeCode = "OZ";

    /// <summary>
    /// Codes from the CHO <c>cho-appeal-x12-275-transmission-code</c>
    /// CodeSystem shipped in PR 1
    /// (<c>docs/fhir/profiles/CodeSystem-cho-appeal-x12-275-transmission-code.json</c>).
    /// </summary>
    private static readonly HashSet<string> ValidTransmissionCodes = new(StringComparer.Ordinal)
    {
        "AA", "BM", "EL", "FT", "FX", "IL", "OZ"
    };

    /// <summary>
    /// Curated <c>documentType</c> → X12 PWK01 attachment-type-code map.
    /// Anything not in this table maps to
    /// <see cref="DefaultAttachmentTypeCode"/>. Codes follow the standard
    /// X12 005010X215 PWK01 report-type-code list.
    /// </summary>
    private static readonly Dictionary<string, string> DocumentTypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Medical Records"]   = "OZ",
        ["Lab Results"]       = "LA",
        ["Discharge Summary"] = "DS",
        ["Operative Report"]  = "OB",
        ["Radiology Report"]  = "RR"
    };

    public AppealAttachment ToAppealAttachment(
        Attachment275EnvelopeDto envelope,
        string? encryptedDescription)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        // Compute the effective submitted date once so the persisted
        // UploadedAt and the file-name timestamp are guaranteed to match
        // for the same call — multiple DateTime.UtcNow reads inside the
        // same mapping can drift by milliseconds.
        var effectiveSubmittedDate = envelope.SubmittedDate == default
            ? DateTime.UtcNow
            : envelope.SubmittedDate;
        return new AppealAttachment
        {
            ControlNumber = envelope.ControlNumber,
            AttachmentTypeCode = MapDocumentTypeToAttachmentTypeCode(envelope.DocumentType),
            AttachmentTypeDescription = envelope.DocumentType,
            TransmissionCode = ResolveTransmissionCode(envelope.TransmissionCode),
            FileName = DeriveFileName(envelope, effectiveSubmittedDate),
            ContentType = MapDocumentFormatToContentType(envelope.DocumentFormat),
            UploadedAt = effectiveSubmittedDate,
            Description = encryptedDescription,
            Status = AttachmentStatus.Sent
        };
    }

    public string MapDocumentTypeToAttachmentTypeCode(string? documentType)
    {
        if (string.IsNullOrEmpty(documentType)) return DefaultAttachmentTypeCode;
        return DocumentTypeMap.TryGetValue(documentType, out var code)
            ? code
            : DefaultAttachmentTypeCode;
    }

    public string ResolveTransmissionCode(string? envelopeTransmissionCode)
    {
        if (string.IsNullOrEmpty(envelopeTransmissionCode)) return DefaultTransmissionCode;
        return ValidTransmissionCodes.Contains(envelopeTransmissionCode)
            ? envelopeTransmissionCode
            : DefaultTransmissionCode;
    }

    private static string DeriveFileName(Attachment275EnvelopeDto envelope, DateTime effectiveSubmittedDate)
    {
        var control = string.IsNullOrEmpty(envelope.ControlNumber) ? "unknown" : envelope.ControlNumber;
        var stamp = effectiveSubmittedDate.ToString("yyyyMMddHHmmss");
        var ext = string.IsNullOrEmpty(envelope.DocumentFormat)
            ? "bin"
            : envelope.DocumentFormat.ToLowerInvariant();
        return $"275-{control}-{stamp}.{ext}";
    }

    private static string? MapDocumentFormatToContentType(string? documentFormat)
    {
        if (string.IsNullOrEmpty(documentFormat)) return null;
        return documentFormat.ToUpperInvariant() switch
        {
            "PDF" => "application/pdf",
            "TIFF" or "TIF" => "image/tiff",
            "JPG" or "JPEG" => "image/jpeg",
            "PNG" => "image/png",
            "XML" => "application/xml",
            "DCM" => "application/dicom",
            _ => "application/octet-stream"
        };
    }
}
