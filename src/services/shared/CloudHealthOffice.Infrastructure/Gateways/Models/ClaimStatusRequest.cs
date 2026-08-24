namespace CloudHealthOffice.Infrastructure.Gateways.Models;

/// <summary>
/// Vendor-neutral 276 claim-status inquiry handed to
/// <see cref="Capabilities.IClaimStatusGateway"/>.
///
/// This is not a 277CA acknowledgment, not adjudication, and not an 835.
/// Callers may supply <see cref="ClaimId"/> or <see cref="TransmissionId"/>;
/// the gateway coordinator derives payer, provider, subscriber, dates, and
/// control numbers from the original transmission and 277CA when present.
/// </summary>
public sealed class ClaimStatusRequest
{
    public string TenantId { get; set; } = string.Empty;

    public string? ClaimId { get; set; }

    public string? TransmissionId { get; set; }

    public string? PayerId { get; set; }

    public GatewayClaimType? ClaimType { get; set; }

    public GatewayClaimProvider? Provider { get; set; }

    public GatewayEligibilityPerson? Subscriber { get; set; }

    public GatewayEligibilityPerson? Patient { get; set; }

    public string? GroupNumber { get; set; }

    /// <summary>Patient control number from the original 837, when known.</summary>
    public string? PatientControlNumber { get; set; }

    /// <summary>Payer-assigned claim control number, typically from a 277CA.</summary>
    public string? PayerClaimControlNumber { get; set; }

    public DateOnly? ServiceDateFrom { get; set; }

    public DateOnly? ServiceDateTo { get; set; }

    public decimal? ClaimAmount { get; set; }

    /// <summary>837I type of bill, when the original claim was institutional.</summary>
    public string? TypeOfBill { get; set; }

    /// <summary>
    /// Optional service-line inquiry. When set, the line must exist on the
    /// original transmission — it is never silently widened to claim-level.
    /// </summary>
    public int? ServiceLineNumber { get; set; }

    public string? CorrelationId { get; set; }

    public List<ClaimStatusLineSource> ServiceLines { get; set; } = new();
}

/// <summary>
/// Vendor-neutral 277 claim-status response. Distinct from
/// <see cref="ClaimAcknowledgmentStatus"/> and from payment/adjudication state.
/// </summary>
public sealed class ClaimStatusResponse
{
    public string InquiryId { get; set; } = string.Empty;

    public string? ClaimId { get; set; }

    public string? TransmissionId { get; set; }

    public GatewayClaimStatus Status { get; set; } = GatewayClaimStatus.Unknown;

    /// <summary>Original payer/X12 status category code (e.g. F1, P1, A4).</summary>
    public string? StatusCategoryCode { get; set; }

    /// <summary>Original payer/X12 status code (e.g. 65, 20).</summary>
    public string? StatusCode { get; set; }

    public string? StatusDescription { get; set; }

    public string? PayerClaimControlNumber { get; set; }

    public string? PatientControlNumber { get; set; }

    public DateOnly? EffectiveDate { get; set; }

    public DateOnly? StatusDate { get; set; }

    public decimal? ClaimAmount { get; set; }

    public decimal? PaidAmount { get; set; }

    public List<ClaimStatusLineResult> ServiceLineStatuses { get; set; } = new();

    public List<ClaimStatusMessage> Messages { get; set; } = new();

    public string? ExternalTransactionId { get; set; }

    public bool ReplayOfExistingInquiry { get; set; }

    /// <summary>Number of claims the payer matched. Greater than 1 is informational.</summary>
    public int MatchCount { get; set; }
}

/// <summary>
/// Normalized 276/277 claim-status categories. These do not replace 277CA
/// acknowledgment, CHO adjudication, or 835 payment posting.
/// </summary>
public enum GatewayClaimStatus
{
    Unknown = 0,
    Received,
    InProcess,
    Pending,
    Accepted,
    Rejected,
    Denied,
    Finalized,
    Paid,
    PartiallyPaid,
    NoRecordFound,
    AdditionalInformationRequested
}

public sealed class ClaimStatusLineSource
{
    public int LineNumber { get; set; }

    public string? LineItemControlNumber { get; set; }

    public string ProcedureCode { get; set; } = string.Empty;

    public List<string> Modifiers { get; set; } = new();

    public decimal ChargeAmount { get; set; }

    public decimal Units { get; set; } = 1;

    public DateOnly? ServiceDateFrom { get; set; }

    public DateOnly? ServiceDateTo { get; set; }

    public string? RevenueCode { get; set; }
}

public sealed class ClaimStatusLineResult
{
    public int? LineNumber { get; set; }

    public string? LineItemControlNumber { get; set; }

    public string? ProcedureCode { get; set; }

    public GatewayClaimStatus Status { get; set; } = GatewayClaimStatus.Unknown;

    public string? StatusCategoryCode { get; set; }

    public string? StatusCode { get; set; }

    public string? StatusDescription { get; set; }

    public decimal? SubmittedAmount { get; set; }

    public decimal? PaidAmount { get; set; }
}

public sealed class ClaimStatusMessage
{
    public string? Code { get; set; }

    public string? Description { get; set; }
}

/// <summary>
/// Snapshot of 837 fields needed to build a later 276. Stored on the
/// transmission record so callers do not re-enter data CHO already has.
/// Not a raw 837 payload.
/// </summary>
public sealed class ClaimStatusInquirySource
{
    public GatewayClaimProvider? BillingProvider { get; set; }

    public GatewayEligibilityPerson? Subscriber { get; set; }

    public GatewayEligibilityPerson? Patient { get; set; }

    public string? GroupNumber { get; set; }

    public DateOnly? ServiceDateFrom { get; set; }

    public DateOnly? ServiceDateTo { get; set; }

    public decimal? ClaimAmount { get; set; }

    public string? TypeOfBill { get; set; }

    public List<ClaimStatusLineSource> ServiceLines { get; set; } = new();

    public static ClaimStatusInquirySource FromSubmission(GatewayClaimSubmissionRequest request) =>
        new()
        {
            BillingProvider = CloneProvider(request.BillingProvider),
            Subscriber = ClonePerson(request.Subscriber),
            Patient = ClonePerson(request.Patient),
            GroupNumber = request.GroupNumber,
            ServiceDateFrom = request.ServiceDateFrom,
            ServiceDateTo = request.ServiceDateTo ?? request.ServiceDateFrom,
            ClaimAmount = request.TotalCharge,
            TypeOfBill = request.TypeOfBill,
            ServiceLines = request.ServiceLines.Select(CloneLine).ToList()
        };

    public ClaimStatusInquirySource Clone() =>
        new()
        {
            BillingProvider = CloneProvider(BillingProvider),
            Subscriber = ClonePerson(Subscriber),
            Patient = ClonePerson(Patient),
            GroupNumber = GroupNumber,
            ServiceDateFrom = ServiceDateFrom,
            ServiceDateTo = ServiceDateTo,
            ClaimAmount = ClaimAmount,
            TypeOfBill = TypeOfBill,
            ServiceLines = ServiceLines.Select(CloneLine).ToList()
        };

    internal static GatewayClaimProvider? CloneProvider(GatewayClaimProvider? source) =>
        source is null
            ? null
            : new GatewayClaimProvider
            {
                Npi = source.Npi,
                OrganizationName = source.OrganizationName,
                LastName = source.LastName,
                FirstName = source.FirstName,
                EmployerId = source.EmployerId
            };

    internal static GatewayEligibilityPerson? ClonePerson(GatewayEligibilityPerson? source) =>
        source is null
            ? null
            : new GatewayEligibilityPerson
            {
                MemberId = source.MemberId,
                FirstName = source.FirstName,
                LastName = source.LastName,
                DateOfBirth = source.DateOfBirth,
                Gender = source.Gender,
                RelationshipToSubscriber = source.RelationshipToSubscriber
            };

    private static ClaimStatusLineSource CloneLine(GatewayClaimLine line) =>
        new()
        {
            LineNumber = line.LineNumber,
            LineItemControlNumber = line.LineItemControlNumber(),
            ProcedureCode = line.ProcedureCode,
            Modifiers = line.Modifiers.ToList(),
            ChargeAmount = line.ChargeAmount,
            Units = line.Units,
            ServiceDateFrom = line.ServiceDateFrom,
            ServiceDateTo = line.ServiceDateTo,
            RevenueCode = line.RevenueCode
        };

    private static ClaimStatusLineSource CloneLine(ClaimStatusLineSource line) =>
        new()
        {
            LineNumber = line.LineNumber,
            LineItemControlNumber = line.LineItemControlNumber,
            ProcedureCode = line.ProcedureCode,
            Modifiers = line.Modifiers.ToList(),
            ChargeAmount = line.ChargeAmount,
            Units = line.Units,
            ServiceDateFrom = line.ServiceDateFrom,
            ServiceDateTo = line.ServiceDateTo,
            RevenueCode = line.RevenueCode
        };
}
