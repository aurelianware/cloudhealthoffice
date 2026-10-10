namespace CloudHealthOffice.Infrastructure.Edi.Interchange;

/// <summary>
/// TA104 Interchange Acknowledgment Code (X12 data element I17).
/// </summary>
public static class Ta1AckCodes
{
    /// <summary>The transmitted interchange control structure header and trailer have been received and have no errors.</summary>
    public const string Accepted = "A";

    /// <summary>Received and accepted, but errors are noted (TA105). The contents are processed.</summary>
    public const string AcceptedWithErrors = "E";

    /// <summary>Rejected because of errors. Nothing in the interchange is processed, and no 999 is produced for it.</summary>
    public const string Rejected = "R";
}

/// <summary>
/// TA105 Interchange Note Code (X12 data element I18), 000–031.
/// </summary>
public static class Ta1NoteCodes
{
    public const string NoError = "000";
    public const string ControlNumberMismatch = "001";
    public const string StandardNotSupported = "002";
    public const string VersionNotSupported = "003";
    public const string InvalidSegmentTerminator = "004";
    public const string InvalidSenderQualifier = "005";
    public const string InvalidSenderId = "006";
    public const string InvalidReceiverQualifier = "007";
    public const string InvalidReceiverId = "008";
    public const string UnknownReceiverId = "009";
    public const string InvalidAuthorizationQualifier = "010";
    public const string InvalidAuthorizationValue = "011";
    public const string InvalidSecurityQualifier = "012";
    public const string InvalidSecurityValue = "013";
    public const string InvalidDate = "014";
    public const string InvalidTime = "015";
    public const string InvalidStandardsIdentifier = "016";
    public const string InvalidVersionId = "017";
    public const string InvalidControlNumber = "018";
    public const string InvalidAckRequested = "019";
    public const string InvalidTestIndicator = "020";
    public const string InvalidGroupCount = "021";
    public const string InvalidControlStructure = "022";
    public const string PrematureEndOfFile = "023";
    public const string InvalidInterchangeContent = "024";
    public const string DuplicateControlNumber = "025";
    public const string InvalidElementSeparator = "026";
    public const string InvalidComponentSeparator = "027";
    public const string InvalidDeliveryDate = "028";
    public const string InvalidDeliveryTime = "029";
    public const string InvalidDeliveryTimeCode = "030";
    public const string InvalidGradeOfService = "031";

    private static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>
    {
        [NoError] = "No error",
        [ControlNumberMismatch] = "The Interchange Control Number in the header and trailer do not match. The value from the header is used in the acknowledgment.",
        [StandardNotSupported] = "This standard as noted in the Control Standards Identifier is not supported.",
        [VersionNotSupported] = "This version of the controls is not supported.",
        [InvalidSegmentTerminator] = "The segment terminator is invalid.",
        [InvalidSenderQualifier] = "Invalid interchange ID qualifier for sender.",
        [InvalidSenderId] = "Invalid interchange sender ID.",
        [InvalidReceiverQualifier] = "Invalid interchange ID qualifier for receiver.",
        [InvalidReceiverId] = "Invalid interchange receiver ID.",
        [UnknownReceiverId] = "Unknown interchange receiver ID.",
        [InvalidAuthorizationQualifier] = "Invalid authorization information qualifier value.",
        [InvalidAuthorizationValue] = "Invalid authorization information value.",
        [InvalidSecurityQualifier] = "Invalid security information qualifier value.",
        [InvalidSecurityValue] = "Invalid security information value.",
        [InvalidDate] = "Invalid interchange date value.",
        [InvalidTime] = "Invalid interchange time value.",
        [InvalidStandardsIdentifier] = "Invalid interchange standards identifier value.",
        [InvalidVersionId] = "Invalid interchange version ID value.",
        [InvalidControlNumber] = "Invalid interchange control number value.",
        [InvalidAckRequested] = "Invalid acknowledgment requested value.",
        [InvalidTestIndicator] = "Invalid test indicator value.",
        [InvalidGroupCount] = "Invalid number of included groups value.",
        [InvalidControlStructure] = "Invalid control structure.",
        [PrematureEndOfFile] = "Improper (premature) end-of-file (transmission).",
        [InvalidInterchangeContent] = "Invalid interchange content (e.g., invalid GS segment).",
        [DuplicateControlNumber] = "Duplicate interchange control number.",
        [InvalidElementSeparator] = "Invalid data element separator.",
        [InvalidComponentSeparator] = "Invalid component element separator.",
        [InvalidDeliveryDate] = "Invalid delivery date in deferred delivery request.",
        [InvalidDeliveryTime] = "Invalid delivery time in deferred delivery request.",
        [InvalidDeliveryTimeCode] = "Invalid delivery time code in deferred delivery request.",
        [InvalidGradeOfService] = "Invalid grade of service code.",
    };

    /// <summary>Every TA105 code, 000–031.</summary>
    public static IReadOnlyCollection<string> All => (IReadOnlyCollection<string>)Descriptions.Keys;

    /// <summary>The X12 description of a TA105 code, or <c>null</c> for an unknown code.</summary>
    public static string? Describe(string? code) =>
        code is not null && Descriptions.TryGetValue(code, out var d) ? d : null;
}
