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
    [RegularExpression(AuthorizationNumberPattern,
        ErrorMessage = "AuthorizationNumber is 1-50 letters, digits and hyphens.")]
    public string AuthorizationNumber { get; set; } = string.Empty;

    /// <summary>The tracking-number format: what a PAS <c>preAuthRef</c> may be.</summary>
    public const string AuthorizationNumberPattern = "^[A-Za-z0-9-]{1,50}$";

    /// <summary>
    /// SHA-256 (lower-case hex) of this request as the caller serialised it.
    /// Recording the same number with the same hash again returns the stored
    /// record (a retry after a lost response); with another hash it is a
    /// conflict.
    /// </summary>
    [Required]
    [RegularExpression("^[a-f0-9]{64}$", ErrorMessage = "RequestHash is a lower-case hex SHA-256.")]
    public string RequestHash { get; set; } = string.Empty;

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

    /// <summary>
    /// The line of business the deciding path evaluated. When present it must
    /// be the record's <see cref="AutomatedDecisionRecordRequest.LineOfBusiness"/>:
    /// the record may not state a basis the decision did not use. Required,
    /// except for the rules that evaluate none
    /// (<see cref="RulesEvaluatingNoLineOfBusiness"/>), recorded as <c>lob=n/a</c>.
    /// </summary>
    public LineOfBusiness? LineOfBusiness { get; set; }

    /// <summary>
    /// Rules that decide without evaluating a line of business: a provider
    /// exclusion (a fact about the provider), disabled adjudication, an
    /// exceeded time budget, and an undetermined line of business.
    /// </summary>
    public static readonly IReadOnlySet<string> RulesEvaluatingNoLineOfBusiness = new HashSet<string>(StringComparer.Ordinal)
    {
        "provider-exclusion-check", "ConfigDisabled", "TimeBudgetExceeded", "LineOfBusinessUndetermined",
    };

    /// <summary>
    /// The service state the deciding path read (USPS code): the enrollment gate
    /// ran, or the state rule engine was reached. Null when no rule read one,
    /// recorded as <c>state=none</c>; a state the request named but no rule read
    /// is not sent.
    /// </summary>
    [RegularExpression("^[A-Z]{2}$", ErrorMessage = "StateCode must be a two-letter state code.")]
    public string? StateCode { get; set; }
}

public enum AutomatedDecisionOutcome
{
    Approved = 1,
    Denied = 2,
    Pended = 3,
}
