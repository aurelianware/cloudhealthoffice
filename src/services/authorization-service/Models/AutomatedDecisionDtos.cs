using System.ComponentModel.DataAnnotations;

namespace AuthorizationService.Models;

/// <summary>
/// An automated decision on a prior-authorization request, recorded by the
/// service that made it (PAS <c>Claim/$submit</c> in fhir-service) through the
/// internal, service-only endpoint. See
/// <see cref="Controllers.AutomatedDecisionsController"/>.
///
/// An explicit allowlist: the record's id, tenant, actor, status, review
/// decision and history are not fields here. The server derives them from the
/// caller's token and from <see cref="Decision"/>.
/// </summary>
public sealed class AutomatedDecisionRecordRequest
{
    /// <summary>The tracking number already chosen and about to be returned as the PAS <c>preAuthRef</c>.</summary>
    [Required]
    [StringLength(50, MinimumLength = 1)]
    public string AuthorizationNumber { get; set; } = string.Empty;

    /// <summary>
    /// When the request arrived at the payer, before it was adjudicated. The
    /// decision clock (CMS-0057-F) starts here; it becomes the record's
    /// <see cref="Authorization.SubmittedDate"/>.
    /// </summary>
    [Required]
    public DateTime? ReceivedAt { get; set; }

    [Required]
    [StringLength(50, MinimumLength = 1)]
    public string MemberId { get; set; } = string.Empty;

    [StringLength(200)]
    public string? PatientReference { get; set; }

    [StringLength(50)]
    public string? CoverageId { get; set; }

    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string PatientFirstName { get; set; } = string.Empty;

    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string PatientLastName { get; set; } = string.Empty;

    [Required]
    public DateTime? PatientDateOfBirth { get; set; }

    /// <summary>From the request's Coverage. <see cref="LineOfBusiness.Unknown"/> when it cannot be determined.</summary>
    public LineOfBusiness LineOfBusiness { get; set; } = LineOfBusiness.Unknown;

    [Required]
    [RegularExpression(@"^\d{10}$", ErrorMessage = "RequestingProviderNPI must be a 10-digit NPI.")]
    public string RequestingProviderNPI { get; set; } = string.Empty;

    [StringLength(300)]
    public string? RequestingProviderName { get; set; }

    [RegularExpression(@"^\d{10}$", ErrorMessage = "ServicingProviderNPI must be a 10-digit NPI.")]
    public string? ServicingProviderNPI { get; set; }

    [StringLength(300)]
    public string? ServicingProviderName { get; set; }

    /// <summary>X12 UM03 service type, or null when the request carried none.</summary>
    [StringLength(2)]
    public string? ServiceTypeCode { get; set; }

    /// <summary>X12 UM06-style level of service: <c>U</c> (urgent/expedited) or <c>E</c> (standard), or null.</summary>
    [RegularExpression("^[UE]$", ErrorMessage = "LevelOfService must be U or E.")]
    public string? LevelOfService { get; set; }

    [Required]
    public DateTime? RequestedServiceDateFrom { get; set; }

    public DateTime? RequestedServiceDateTo { get; set; }

    public List<DiagnosisCode> DiagnosisCodes { get; set; } = new();

    [MinLength(1, ErrorMessage = "At least one requested service is required.")]
    public List<RequestedService> RequestedServices { get; set; } = new();

    [Required]
    public AutomatedDecision? Decision { get; set; }
}

/// <summary>The outcome the automated adjudicator reached.</summary>
public sealed class AutomatedDecision
{
    [Required]
    public AutomatedDecisionOutcome? Outcome { get; set; }

    [StringLength(50)]
    public string? DenialReasonCode { get; set; }

    [StringLength(500)]
    public string? DenialReason { get; set; }

    [StringLength(500)]
    public string? PendReason { get; set; }

    /// <summary>Approved period. Required for an approval; not allowed otherwise.</summary>
    public DateTime? ApprovedServiceDateFrom { get; set; }

    public DateTime? ApprovedServiceDateTo { get; set; }

    [Required]
    public DateTime? DecidedAt { get; set; }

    /// <summary>The adjudication rule that decided, recorded in the status history.</summary>
    [StringLength(100)]
    public string? Rule { get; set; }
}

public enum AutomatedDecisionOutcome
{
    Approved = 1,
    Denied = 2,
    Pended = 3,
}
