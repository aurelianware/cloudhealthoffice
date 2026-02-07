namespace CloudHealthOffice.Portal.Services;

public interface IClaimsService
{
    Task<List<ClaimSummary>> GetRecentClaimsAsync(int count);
    Task<ClaimDetails?> GetClaimByIdAsync(string claimId);
    Task<string> SubmitClaimAsync(SubmitClaimRequest request);
}

public interface IEligibilityService
{
    Task<EligibilityResponse> CheckEligibilityAsync(object request);
}

public interface IMemberService
{
    Task<List<MemberSummary>> SearchMembersAsync(string searchTerm);
    Task<MemberDetails?> GetMemberByIdAsync(string memberId);
}

public interface ICoverageService
{
    Task<List<Coverage>> GetCoverageByMemberIdAsync(string memberId);
}

public interface IAuthorizationService
{
    Task<List<AuthorizationSummary>> GetAuthorizationsAsync(string? memberId = null);
    Task<AuthorizationDetails?> GetAuthorizationByIdAsync(string authorizationId);
    Task<string> SubmitAuthorizationAsync(SubmitAuthorizationRequest request);
}

public interface IProviderService
{
    Task<List<ProviderSummary>> SearchProvidersAsync(string searchTerm);
}

public interface IBenefitPlanService
{
    Task<List<BenefitPlan>> GetBenefitPlansAsync();
}

public interface IWorkflowService
{
    Task<List<WorkflowRun>> GetWorkflowRunsAsync(int limit = 20);
    Task<WorkflowDetails?> GetWorkflowDetailsAsync(string workflowId);
}

public interface IMetricsService
{
    Task<DashboardMetrics> GetDashboardMetricsAsync();
}

// DTOs
public class ClaimSummary
{
    public string ClaimId { get; set; } = string.Empty;
    public string MemberName { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public decimal TotalChargeAmount { get; set; }
    public string Status { get; set; } = string.Empty;
    public int ProcessingTimeMs { get; set; }
}

public class ClaimDetails : ClaimSummary
{
    public string MemberId { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public List<ServiceLine> ServiceLines { get; set; } = new();
    public decimal PayerAmount { get; set; }
    public decimal PatientResponsibility { get; set; }
    public DateTime ServiceDate { get; set; }
    public DateTime SubmittedDate { get; set; }
    public DateTime? ProcessedDate { get; set; }
}

public class ServiceLine
{
    public string ProcedureCode { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal ChargeAmount { get; set; }
    public decimal AllowedAmount { get; set; }
    public decimal PayerAmount { get; set; }
}

public class SubmitClaimRequest
{
    public string MemberId { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public DateTime ServiceDate { get; set; }
    public List<ServiceLineRequest> ServiceLines { get; set; } = new();
}

public class ServiceLineRequest
{
    public string ProcedureCode { get; set; } = string.Empty;
    public decimal ChargeAmount { get; set; }
    public int Units { get; set; } = 1;
}

public class MemberSummary
{
    public string MemberId { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public DateTime DateOfBirth { get; set; }
    public string CoverageStatus { get; set; } = string.Empty;
}

public class MemberDetails : MemberSummary
{
    public string Gender { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public Address? Address { get; set; }
}

public class Address
{
    public string Street { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string ZipCode { get; set; } = string.Empty;
}

public class Coverage
{
    public string CoverageId { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public string GroupNumber { get; set; } = string.Empty;
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
    public string Status { get; set; } = string.Empty;
}

public class AuthorizationSummary
{
    public string AuthorizationId { get; set; } = string.Empty;
    public string MemberName { get; set; } = string.Empty;
    public string ServiceType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime RequestDate { get; set; }
    public DateTime? DecisionDate { get; set; }
    public string ProviderName { get; set; } = string.Empty;
    public int ProcessingTimeMs { get; set; }
}

public class AuthorizationDetails : AuthorizationSummary
{
    public string MemberId { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string DiagnosisCode { get; set; } = string.Empty;
    public string DiagnosisDescription { get; set; } = string.Empty;
    public string ProcedureCode { get; set; } = string.Empty;
    public string ProcedureDescription { get; set; } = string.Empty;
    public int UnitsRequested { get; set; }
    public int? UnitsApproved { get; set; }
    public DateTime ServiceStartDate { get; set; }
    public DateTime? ServiceEndDate { get; set; }
    public string ReviewerNotes { get; set; } = string.Empty;
    public string DenialReason { get; set; } = string.Empty;
}

public class SubmitAuthorizationRequest
{
    public string MemberId { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string ServiceType { get; set; } = string.Empty;
    public string DiagnosisCode { get; set; } = string.Empty;
    public string ProcedureCode { get; set; } = string.Empty;
    public int UnitsRequested { get; set; }
    public DateTime ServiceStartDate { get; set; }
    public DateTime? ServiceEndDate { get; set; }
    public string ClinicalNotes { get; set; } = string.Empty;
}

public class ProviderSummary
{
    public string ProviderId { get; set; } = string.Empty;
    public string NPI { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Specialty { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
}

public class BenefitPlan
{
    public string PlanId { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public string PlanType { get; set; } = string.Empty;
    public decimal Deductible { get; set; }
    public decimal OutOfPocketMax { get; set; }
}

public class WorkflowRun
{
    public string WorkflowId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime? FinishTime { get; set; }
    public int DurationSeconds { get; set; }
}

public class WorkflowDetails : WorkflowRun
{
    public List<WorkflowStep> Steps { get; set; } = new();
}

public class WorkflowStep
{
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime? StartTime { get; set; }
    public DateTime? FinishTime { get; set; }
}

public class DashboardMetrics
{
    public int TotalClaims { get; set; }
    public double ClaimsTrend { get; set; }
    public double ApprovalRate { get; set; }
    public int AvgProcessingTimeMs { get; set; }
    public decimal TotalPayerAmount { get; set; }
    public int ApprovedClaims { get; set; }
    public int DeniedClaims { get; set; }
    public int PendingClaims { get; set; }
}

public class EligibilityResponse
{
    public bool IsCovered { get; set; }
    public string? RejectionReason { get; set; }
    public string InsurancePlanName { get; set; } = string.Empty;
    public string GroupNumber { get; set; } = string.Empty;
    public string CoverageLevel { get; set; } = string.Empty;
    public DateTime? CoverageBeginDate { get; set; }
    public DeductibleInfo? Deductible { get; set; }
    public OutOfPocketInfo? OutOfPocket { get; set; }
    public List<Benefit>? Benefits { get; set; }
}

public class DeductibleInfo
{
    public decimal IndividualAmount { get; set; }
    public decimal IndividualMet { get; set; }
    public decimal FamilyAmount { get; set; }
    public decimal FamilyMet { get; set; }
}

public class OutOfPocketInfo
{
    public decimal IndividualAmount { get; set; }
    public decimal IndividualMet { get; set; }
    public decimal FamilyAmount { get; set; }
    public decimal FamilyMet { get; set; }
}

public class Benefit
{
    public string ServiceTypeName { get; set; } = string.Empty;
    public decimal? MonetaryAmount { get; set; }
    public decimal? Percentage { get; set; }
    public bool AuthorizationRequired { get; set; }
}
