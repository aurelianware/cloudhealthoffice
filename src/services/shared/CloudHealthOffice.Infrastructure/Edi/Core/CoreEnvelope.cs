namespace CloudHealthOffice.Infrastructure.Edi.Core;

/// <summary>
/// CAQH CORE Connectivity Rule vC2.2.0 envelope vocabulary (the version
/// HHS adopted under HIPAA for eligibility and claim status, 45 CFR 162.920).
/// Field names, payload types and error codes are the rule's own strings:
/// trading partners compare them literally.
/// </summary>
public static class CoreRule
{
    public const string Version = "2.2.0";

    /// <summary>XSD namespace of the SOAP request/response bodies.</summary>
    public const string XsdNamespace = "http://www.caqh.org/SOAP/WSDL/CORERule2.2.0.xsd";

    /// <summary>WSDL target namespace.</summary>
    public const string WsdlNamespace = "http://www.caqh.org/SOAP/WSDL/";

    public const string Soap12Namespace = "http://www.w3.org/2003/05/soap-envelope";

    public const string WsseNamespace = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
}

/// <summary>CORE PayloadType values for the transactions CHO answers in real time.</summary>
public static class CorePayloadTypes
{
    public const string Eligibility270 = "X12_270_Request_005010X279A1";
    public const string Eligibility271 = "X12_271_Response_005010X279A1";
    public const string ClaimStatus276 = "X12_276_Request_005010X212";
    public const string ClaimStatus277 = "X12_277_Response_005010X212";
    public const string Acknowledgment999 = "X12_999_Response_005010X231A1";

    // The rule spells the TA1 payload type with "00501", not "005010".
    public const string AcknowledgmentTa1 = "X12_TA1_Response_00501X231A1";
}

/// <summary>CORE envelope field names (SOAP element names and MIME form field names).</summary>
public static class CoreFields
{
    public const string PayloadType = "PayloadType";
    public const string ProcessingMode = "ProcessingMode";
    public const string PayloadId = "PayloadID";
    public const string PayloadLength = "PayloadLength";
    public const string TimeStamp = "TimeStamp";
    public const string UserName = "UserName";
    public const string Password = "Password";
    public const string SenderId = "SenderID";
    public const string ReceiverId = "ReceiverID";
    public const string CoreRuleVersion = "CORERuleVersion";
    public const string Checksum = "Checksum";
    public const string Payload = "Payload";
    public const string ErrorCode = "ErrorCode";
    public const string ErrorMessage = "ErrorMessage";
}

/// <summary>CORE envelope ErrorCode values (Connectivity Rule vC2.2.0, section 4.4).</summary>
public static class CoreErrorCodes
{
    public const string Success = "Success";
    public const string Unauthorized = "UnAuthorized";
    public const string VersionMismatch = "VersionMismatch";
    public const string ChecksumMismatch = "ChecksumMismatch";
    public const string PayloadLengthMismatch = "PayloadLengthMismatch";

    /// <summary>The receiver could not process the envelope (a server fault, not the submitter's).</summary>
    public const string ReceiverError = "Receiver";

    /// <summary>A missing mandatory field: <c>{Field}Required</c>, e.g. <c>PayloadIDRequired</c>.</summary>
    public static string Required(string field) => field + "Required";

    /// <summary>A present field with an unusable value: <c>{Field}Illegal</c>, e.g. <c>PayloadTypeIllegal</c>.</summary>
    public static string Illegal(string field) => field + "Illegal";
}

/// <summary>How the submitter proved who it is.</summary>
public enum CoreAuthenticationMethod
{
    None,
    ClientCertificate,
    UserNamePassword,
    BearerToken,
}

/// <summary>The envelope format a request arrived in; the response uses the same one.</summary>
public enum CoreEnvelopeFormat
{
    Soap,
    Mime,
}

/// <summary>One received CORE real-time envelope, before validation.</summary>
public sealed class CoreEnvelopeRequest
{
    public CoreEnvelopeFormat Format { get; init; }
    public string? PayloadType { get; init; }
    public string? ProcessingMode { get; init; }
    public string? PayloadId { get; init; }
    public string? PayloadLength { get; init; }
    public string? TimeStamp { get; init; }
    public string? SenderId { get; init; }
    public string? ReceiverId { get; init; }
    public string? CoreRuleVersion { get; init; }
    public string? Checksum { get; init; }
    public string? Payload { get; init; }

    /// <summary>MIME UserName field, or the SOAP WS-Security UsernameToken Username.</summary>
    public string? UserName { get; init; }

    /// <summary>MIME Password field, or the SOAP WS-Security UsernameToken Password. Never logged.</summary>
    public string? Password { get; init; }
}

/// <summary>The CORE real-time envelope CHO sends back.</summary>
public sealed class CoreEnvelopeResponse
{
    public CoreEnvelopeFormat Format { get; init; }
    public string? PayloadType { get; init; }
    public string ProcessingMode { get; init; } = "RealTime";
    public string? PayloadId { get; init; }
    public required string TimeStamp { get; init; }
    public string? SenderId { get; init; }
    public string? ReceiverId { get; init; }
    public string CoreRuleVersion { get; init; } = CoreRule.Version;
    public string? Payload { get; init; }
    public string ErrorCode { get; init; } = CoreErrorCodes.Success;
    public string? ErrorMessage { get; init; }

    /// <summary>HTTP status the envelope travels with (200 for every answered payload, including a 999 or TA1).</summary>
    public int HttpStatus { get; init; } = 200;
}
