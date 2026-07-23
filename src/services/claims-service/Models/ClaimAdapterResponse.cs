namespace ClaimsService.Models;

/// <summary>
/// Vendor-neutral response envelope returned by the single-claim read methods
/// on <see cref="Adapters.IClaimAdapter"/>: <c>GetClaimAsync</c>,
/// <c>GetClaimByNumberAsync</c>, <c>GetClaimVersionAsync</c>, and
/// <c>SubmitClaimAsync</c>.
///
/// <para>
/// The payload <see cref="AdapterClaim"/> exposed by <see cref="ClaimAdapterResponse.Claim"/> is
/// shaped to project cleanly onto a future FHIR <c>Claim</c> /
/// <c>ClaimResponse</c> resource (capability 5.11): the version-chain fields
/// map onto <c>Claim.identifier</c> + <c>ClaimResponse.related</c>, the line
/// items map onto <c>Claim.item</c>, the diagnosis codes map onto
/// <c>Claim.diagnosis</c>, and the adjudication fields map onto
/// <c>ClaimResponse.adjudication</c>.
/// </para>
/// </summary>
public class ClaimAdapterResponse
{
    /// <summary>Adapter that produced the response (e.g. "cho", "qnxt").</summary>
    public string Platform { get; set; } = string.Empty;

    /// <summary>Optional raw vendor response retained for audit / debugging.</summary>
    public string? RawResponse { get; set; }

    /// <summary>Claim payload. Null when the requested claim is not found.</summary>
    public AdapterClaim? Claim { get; set; }
}

/// <summary>
/// Vendor-neutral response envelope for <see cref="Adapters.IClaimAdapter.ListClaimVersionsAsync"/>.
/// Versions are returned newest-first; <see cref="ContinuationToken"/> is null
/// when the caller has reached the end of the chain.
/// </summary>
public class ClaimVersionListAdapterResponse
{
    public string Platform { get; set; } = string.Empty;
    public string? RawResponse { get; set; }

    /// <summary>Page of version rows (never null; may be empty).</summary>
    public IReadOnlyList<AdapterClaim> Versions { get; set; } = Array.Empty<AdapterClaim>();

    /// <summary>Opaque token for the next page; null when exhausted.</summary>
    public string? ContinuationToken { get; set; }
}

/// <summary>
/// Vendor-neutral response envelope for the search methods on
/// <see cref="Adapters.IClaimAdapter"/>: <c>SearchClaimsAsync</c> and
/// <c>SearchClaimsForMemberAsync</c>.
/// </summary>
public class ClaimSearchAdapterResponse
{
    public string Platform { get; set; } = string.Empty;
    public string? RawResponse { get; set; }

    /// <summary>Page of claims returned by the adapter (never null; may be empty).</summary>
    public IReadOnlyList<AdapterClaim> Claims { get; set; } = Array.Empty<AdapterClaim>();

    /// <summary>Total matching count when the platform reports it; null otherwise.</summary>
    public int? TotalCount { get; set; }
}

/// <summary>
/// Normalized claim DTO. Field shape mirrors <see cref="Claim"/> so the CHO
/// pass-through is lossless; round-trip mappers <see cref="From"/> and
/// <see cref="ToClaim"/> let consumers convert back to the domain type
/// without any information loss. Mirrors <c>AdapterProvider</c> and
/// <c>AdapterBenefitPlan</c>.
///
/// <para>
/// The DTO carries every field on <see cref="Claim"/> — including the
/// CHO-internal <see cref="PendDetails"/> and <see cref="AiExamination"/>
/// surfaces — so the CHO pass-through is lossless. Vendor adapters that don't
/// expose an equivalent leave those fields null on read; CHO round-trip
/// stays whole.
/// </para>
/// </summary>
public class AdapterClaim
{
    public string TenantId { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string ClaimNumber { get; set; } = string.Empty;

    public string MemberId { get; set; } = string.Empty;
    public string? SubscriberId { get; set; }
    public string? BenefitPlanId { get; set; }
    public string? CoverageId { get; set; }

    public string? SubscriberFirstName { get; set; }
    public string? SubscriberLastName { get; set; }
    public string? PatientFirstName { get; set; }
    public string? PatientLastName { get; set; }
    public string? PatientRelationship { get; set; }

    public LineOfBusiness LineOfBusiness { get; set; }

    public string BillingProviderNPI { get; set; } = string.Empty;
    public string? BillingProviderName { get; set; }
    public string? RenderingProviderNPI { get; set; }
    public string? RenderingProviderName { get; set; }
    public string? FacilityNPI { get; set; }
    public string? FacilityName { get; set; }
    public string PlaceOfServiceCode { get; set; } = "11";

    public ClaimType ClaimType { get; set; } = ClaimType.Professional;
    public string ClaimFrequencyCode { get; set; } = "1";
    public decimal TotalChargeAmount { get; set; }
    public DateTime ServiceDateFrom { get; set; }
    public DateTime ServiceDateTo { get; set; }

    public List<AdapterDiagnosisCode> DiagnosisCodes { get; set; } = new();
    public List<AdapterClaimLine> ClaimLines { get; set; } = new();

    public ClaimStatus Status { get; set; } = ClaimStatus.Submitted;
    public DateTime SubmittedDate { get; set; }
    public DateTime? ReceivedDate { get; set; }
    public DateTime? AdjudicatedDate { get; set; }
    public DateTime? PaidDate { get; set; }

    public AdapterAdjudicationResult? AdjudicationResult { get; set; }
    public PendDetails? PendDetails { get; set; }
    public AiExamination? AiExamination { get; set; }

    public string? PriorAuthorizationNumber { get; set; }
    public string? ReferralNumber { get; set; }
    public string? RelatedCausesCode { get; set; }
    public DateTime? AccidentDate { get; set; }
    public string? ClaimNotes { get; set; }
    public string? EDI837ControlNumber { get; set; }
    public string? EDI835ControlNumber { get; set; }

    // Version-chain identity (5.1)
    public string ClaimVersionId { get; set; } = string.Empty;
    public int VersionNumber { get; set; }
    public ClaimVersionState VersionState { get; set; }
    public string? PredecessorVersionId { get; set; }
    public DateTime? PublishedAt { get; set; }
    public string? PublishedBy { get; set; }
    public DateTime? SupersededAt { get; set; }
    public string? SupersededByVersionId { get; set; }

    public DateTime CreatedDate { get; set; }
    public DateTime LastUpdatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public string? LastUpdatedBy { get; set; }

    public static AdapterClaim From(Claim src) => new()
    {
        TenantId = src.TenantId,
        Id = src.Id,
        ClaimNumber = src.ClaimNumber,
        MemberId = src.MemberId,
        SubscriberId = src.SubscriberId,
        BenefitPlanId = src.BenefitPlanId,
        CoverageId = src.CoverageId,
        SubscriberFirstName = src.SubscriberFirstName,
        SubscriberLastName = src.SubscriberLastName,
        PatientFirstName = src.PatientFirstName,
        PatientLastName = src.PatientLastName,
        PatientRelationship = src.PatientRelationship,
        LineOfBusiness = src.LineOfBusiness,
        BillingProviderNPI = src.BillingProviderNPI,
        BillingProviderName = src.BillingProviderName,
        RenderingProviderNPI = src.RenderingProviderNPI,
        RenderingProviderName = src.RenderingProviderName,
        FacilityNPI = src.FacilityNPI,
        FacilityName = src.FacilityName,
        PlaceOfServiceCode = src.PlaceOfServiceCode,
        ClaimType = src.ClaimType,
        ClaimFrequencyCode = src.ClaimFrequencyCode,
        TotalChargeAmount = src.TotalChargeAmount,
        ServiceDateFrom = src.ServiceDateFrom,
        ServiceDateTo = src.ServiceDateTo,
        DiagnosisCodes = src.DiagnosisCodes.Select(AdapterDiagnosisCode.From).ToList(),
        ClaimLines = src.ClaimLines.Select(AdapterClaimLine.From).ToList(),
        Status = src.Status,
        SubmittedDate = src.SubmittedDate,
        ReceivedDate = src.ReceivedDate,
        AdjudicatedDate = src.AdjudicatedDate,
        PaidDate = src.PaidDate,
        AdjudicationResult = src.AdjudicationResult is null
            ? null
            : AdapterAdjudicationResult.From(src.AdjudicationResult),
        PendDetails = src.PendDetails,
        AiExamination = src.AiExamination,
        PriorAuthorizationNumber = src.PriorAuthorizationNumber,
        ReferralNumber = src.ReferralNumber,
        RelatedCausesCode = src.RelatedCausesCode,
        AccidentDate = src.AccidentDate,
        ClaimNotes = src.ClaimNotes,
        EDI837ControlNumber = src.EDI837ControlNumber,
        EDI835ControlNumber = src.EDI835ControlNumber,
        ClaimVersionId = src.ClaimVersionId,
        VersionNumber = src.VersionNumber,
        VersionState = src.VersionState,
        PredecessorVersionId = src.PredecessorVersionId,
        PublishedAt = src.PublishedAt,
        PublishedBy = src.PublishedBy,
        SupersededAt = src.SupersededAt,
        SupersededByVersionId = src.SupersededByVersionId,
        CreatedDate = src.CreatedDate,
        LastUpdatedDate = src.LastUpdatedDate,
        CreatedBy = src.CreatedBy,
        LastUpdatedBy = src.LastUpdatedBy,
    };

    public Claim ToClaim() => new()
    {
        TenantId = TenantId,
        Id = Id,
        ClaimNumber = ClaimNumber,
        MemberId = MemberId,
        SubscriberId = SubscriberId,
        BenefitPlanId = BenefitPlanId,
        CoverageId = CoverageId,
        SubscriberFirstName = SubscriberFirstName,
        SubscriberLastName = SubscriberLastName,
        PatientFirstName = PatientFirstName,
        PatientLastName = PatientLastName,
        PatientRelationship = PatientRelationship,
        LineOfBusiness = LineOfBusiness,
        BillingProviderNPI = BillingProviderNPI,
        BillingProviderName = BillingProviderName,
        RenderingProviderNPI = RenderingProviderNPI,
        RenderingProviderName = RenderingProviderName,
        FacilityNPI = FacilityNPI,
        FacilityName = FacilityName,
        PlaceOfServiceCode = PlaceOfServiceCode,
        ClaimType = ClaimType,
        ClaimFrequencyCode = ClaimFrequencyCode,
        TotalChargeAmount = TotalChargeAmount,
        ServiceDateFrom = ServiceDateFrom,
        ServiceDateTo = ServiceDateTo,
        DiagnosisCodes = DiagnosisCodes.Select(d => d.ToDiagnosisCode()).ToList(),
        ClaimLines = ClaimLines.Select(l => l.ToClaimLine()).ToList(),
        Status = Status,
        SubmittedDate = SubmittedDate,
        ReceivedDate = ReceivedDate,
        AdjudicatedDate = AdjudicatedDate,
        PaidDate = PaidDate,
        AdjudicationResult = AdjudicationResult?.ToAdjudicationResult(),
        PendDetails = PendDetails,
        AiExamination = AiExamination,
        PriorAuthorizationNumber = PriorAuthorizationNumber,
        ReferralNumber = ReferralNumber,
        RelatedCausesCode = RelatedCausesCode,
        AccidentDate = AccidentDate,
        ClaimNotes = ClaimNotes,
        EDI837ControlNumber = EDI837ControlNumber,
        EDI835ControlNumber = EDI835ControlNumber,
        ClaimVersionId = ClaimVersionId,
        VersionNumber = VersionNumber,
        VersionState = VersionState,
        PredecessorVersionId = PredecessorVersionId,
        PublishedAt = PublishedAt,
        PublishedBy = PublishedBy,
        SupersededAt = SupersededAt,
        SupersededByVersionId = SupersededByVersionId,
        CreatedDate = CreatedDate,
        LastUpdatedDate = LastUpdatedDate,
        CreatedBy = CreatedBy,
        LastUpdatedBy = LastUpdatedBy,
    };
}

/// <summary>Vendor-neutral diagnosis-code DTO mirroring <see cref="DiagnosisCode"/>.</summary>
public class AdapterDiagnosisCode
{
    public string Code { get; set; } = string.Empty;
    public string CodeQualifier { get; set; } = "ABK";
    public int PointerNumber { get; set; }
    public string? Description { get; set; }

    public static AdapterDiagnosisCode From(DiagnosisCode src) => new()
    {
        Code = src.Code,
        CodeQualifier = src.CodeQualifier,
        PointerNumber = src.PointerNumber,
        Description = src.Description,
    };

    public DiagnosisCode ToDiagnosisCode() => new()
    {
        Code = Code,
        CodeQualifier = CodeQualifier,
        PointerNumber = PointerNumber,
        Description = Description,
    };
}

/// <summary>Vendor-neutral claim-line DTO mirroring <see cref="ClaimLine"/>.</summary>
public class AdapterClaimLine
{
    public int LineNumber { get; set; }
    public string ProcedureCode { get; set; } = string.Empty;
    public string? ProcedureDescription { get; set; }
    public List<string> Modifiers { get; set; } = new();
    public List<int> DiagnosisPointers { get; set; } = new();
    public decimal Units { get; set; } = 1;
    public decimal ChargeAmount { get; set; }
    public DateTime ServiceDateFrom { get; set; }
    public DateTime ServiceDateTo { get; set; }
    public string? PlaceOfServiceCode { get; set; }
    public string? RevenueCode { get; set; }
    public decimal? MpipMultiplierApplied { get; set; }
    public AdapterLineAdjudicationResult? AdjudicationResult { get; set; }

    public static AdapterClaimLine From(ClaimLine src) => new()
    {
        LineNumber = src.LineNumber,
        ProcedureCode = src.ProcedureCode,
        ProcedureDescription = src.ProcedureDescription,
        Modifiers = src.Modifiers.ToList(),
        DiagnosisPointers = src.DiagnosisPointers.ToList(),
        Units = src.Units,
        ChargeAmount = src.ChargeAmount,
        ServiceDateFrom = src.ServiceDateFrom,
        ServiceDateTo = src.ServiceDateTo,
        PlaceOfServiceCode = src.PlaceOfServiceCode,
        RevenueCode = src.RevenueCode,
        MpipMultiplierApplied = src.MpipMultiplierApplied,
        AdjudicationResult = src.AdjudicationResult is null
            ? null
            : AdapterLineAdjudicationResult.From(src.AdjudicationResult),
    };

    public ClaimLine ToClaimLine() => new()
    {
        LineNumber = LineNumber,
        ProcedureCode = ProcedureCode,
        ProcedureDescription = ProcedureDescription,
        Modifiers = Modifiers.ToList(),
        DiagnosisPointers = DiagnosisPointers.ToList(),
        Units = Units,
        ChargeAmount = ChargeAmount,
        ServiceDateFrom = ServiceDateFrom,
        ServiceDateTo = ServiceDateTo,
        PlaceOfServiceCode = PlaceOfServiceCode,
        RevenueCode = RevenueCode,
        MpipMultiplierApplied = MpipMultiplierApplied,
        AdjudicationResult = AdjudicationResult?.ToLineAdjudicationResult(),
    };
}

/// <summary>Vendor-neutral claim-level adjudication-result DTO mirroring <see cref="AdjudicationResult"/>.</summary>
public class AdapterAdjudicationResult
{
    public string? NetworkTier { get; set; }
    public decimal AllowedAmount { get; set; }
    public decimal DeductibleAmount { get; set; }
    public decimal CoinsuranceAmount { get; set; }
    public decimal CopayAmount { get; set; }
    public decimal PatientResponsibility { get; set; }
    public decimal PayerPayment { get; set; }
    public string? DenialReasonCode { get; set; }
    public string? DenialReason { get; set; }
    public List<ClaimAdjustmentReason> AdjustmentReasons { get; set; } = new();
    public List<string> RemarkCodes { get; set; } = new();
    public string? CheckNumber { get; set; }
    public DateTime? PaymentDate { get; set; }

    public static AdapterAdjudicationResult From(AdjudicationResult src) => new()
    {
        NetworkTier = src.NetworkTier,
        AllowedAmount = src.AllowedAmount,
        DeductibleAmount = src.DeductibleAmount,
        CoinsuranceAmount = src.CoinsuranceAmount,
        CopayAmount = src.CopayAmount,
        PatientResponsibility = src.PatientResponsibility,
        PayerPayment = src.PayerPayment,
        DenialReasonCode = src.DenialReasonCode,
        DenialReason = src.DenialReason,
        AdjustmentReasons = src.AdjustmentReasons.ToList(),
        RemarkCodes = src.RemarkCodes.ToList(),
        CheckNumber = src.CheckNumber,
        PaymentDate = src.PaymentDate,
    };

    public AdjudicationResult ToAdjudicationResult() => new()
    {
        NetworkTier = NetworkTier,
        AllowedAmount = AllowedAmount,
        DeductibleAmount = DeductibleAmount,
        CoinsuranceAmount = CoinsuranceAmount,
        CopayAmount = CopayAmount,
        PatientResponsibility = PatientResponsibility,
        PayerPayment = PayerPayment,
        DenialReasonCode = DenialReasonCode,
        DenialReason = DenialReason,
        AdjustmentReasons = AdjustmentReasons.ToList(),
        RemarkCodes = RemarkCodes.ToList(),
        CheckNumber = CheckNumber,
        PaymentDate = PaymentDate,
    };
}

/// <summary>Vendor-neutral line-level adjudication-result DTO mirroring <see cref="LineAdjudicationResult"/>.</summary>
public class AdapterLineAdjudicationResult
{
    public decimal AllowedAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal PatientResponsibility { get; set; }
    public List<ClaimAdjustmentReason> AdjustmentReasons { get; set; } = new();

    public static AdapterLineAdjudicationResult From(LineAdjudicationResult src) => new()
    {
        AllowedAmount = src.AllowedAmount,
        PaidAmount = src.PaidAmount,
        PatientResponsibility = src.PatientResponsibility,
        AdjustmentReasons = src.AdjustmentReasons.ToList(),
    };

    public LineAdjudicationResult ToLineAdjudicationResult() => new()
    {
        AllowedAmount = AllowedAmount,
        PaidAmount = PaidAmount,
        PatientResponsibility = PatientResponsibility,
        AdjustmentReasons = AdjustmentReasons.ToList(),
    };
}
