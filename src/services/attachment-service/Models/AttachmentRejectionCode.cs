namespace AttachmentService.Models;

/// <summary>
/// Structured rejection reason codes for 275 attachment submissions.
///
/// These codes are stored on the Attachment record when Status = "Failed"
/// and are emitted as X12 TED (Transaction Error Data) segments in the
/// 824 Application Advice (005010X186A1).
///
/// X12 TED01 Error Type Code mapping:
///   1 = Issuer of ID Not Found
///   2 = Referenced ID Not Found
///   3 = Format Not Supported
///   4 = Missing Data
///   5 = Invalid Data
///   7 = Duplicate Transaction
/// </summary>
public static class AttachmentRejectionCode
{
    /// <summary>Attachment with the same control number already received. TED01=7</summary>
    public const string Duplicate = "DUPLICATE";

    /// <summary>Document format not supported (e.g., unsupported file type). TED01=3</summary>
    public const string InvalidFormat = "INVALID_FORMAT";

    /// <summary>Required clinical information or mandatory fields are missing. TED01=4</summary>
    public const string MissingData = "MISSING_DATA";

    /// <summary>Provider NPI not found or not credentialed with this payer. TED01=1</summary>
    public const string InvalidProvider = "INVALID_PROVIDER";

    /// <summary>RFAI reference not found, already fulfilled, or past due date. TED01=2</summary>
    public const string InvalidRfai = "INVALID_RFAI";

    /// <summary>Data validation failure — field values are present but invalid. TED01=5</summary>
    public const string InvalidData = "INVALID_DATA";

    /// <summary>File size exceeds the payer-defined maximum. TED01=5</summary>
    public const string SizeExceeded = "SIZE_EXCEEDED";

    /// <summary>Document type does not match the type requested in the RFAI. TED01=5</summary>
    public const string DocumentTypeMismatch = "DOC_TYPE_MISMATCH";

    /// <summary>
    /// Map a CHO rejection code to the corresponding X12 TED01 Error Type Code.
    /// Returns null if the code is not recognised (no TED segment emitted).
    /// </summary>
    public static string? ToTed01ErrorTypeCode(string? rejectionCode) => rejectionCode switch
    {
        Duplicate             => "7",
        InvalidFormat         => "3",
        MissingData           => "4",
        InvalidProvider       => "1",
        InvalidRfai           => "2",
        InvalidData           => "5",
        SizeExceeded          => "5",
        DocumentTypeMismatch  => "5",
        _                     => null
    };

    /// <summary>
    /// Standard human-readable description for each rejection code,
    /// used as TED02 (free-form text) when the attachment has no explicit Notes.
    /// </summary>
    public static string DefaultDescription(string? rejectionCode) => rejectionCode switch
    {
        Duplicate             => "Duplicate attachment submission",
        InvalidFormat         => "Document format not supported",
        MissingData           => "Required clinical information missing",
        InvalidProvider       => "Provider NPI not found or not credentialed",
        InvalidRfai           => "RFAI reference not found or past due date",
        InvalidData           => "Data validation failure",
        SizeExceeded          => "File size exceeds maximum allowed",
        DocumentTypeMismatch  => "Document type does not match RFAI request",
        _                     => "Attachment rejected"
    };
}
