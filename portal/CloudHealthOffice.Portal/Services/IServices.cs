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

public interface IAttachmentService
{
    Task<List<AttachmentInfo>> GetAttachmentsAsync(string authorizationId);
    Task<string> UploadAttachmentAsync(string authorizationId, Stream fileStream, string fileName, string contentType);
    Task<Stream> DownloadAttachmentAsync(string authorizationId, string attachmentId);
    Task DeleteAttachmentAsync(string authorizationId, string attachmentId);
}

public interface IProviderService
{
    Task<List<ProviderSummary>> SearchProvidersAsync(string searchTerm);
}

public interface IBenefitPlanService
{
    Task<List<BenefitPlan>> GetBenefitPlansAsync();
    Task<List<BenefitPlanListItem>> SearchBenefitPlansAsync(string? sponsorId = null, string? productType = null);
    Task<BenefitPlanDetails?> GetBenefitPlanByIdAsync(string planId);
    Task<string> CreateBenefitPlanAsync(CreateBenefitPlanRequest request);
    Task UpdateBenefitPlanAsync(string planId, UpdateBenefitPlanRequest request);
    Task<List<BenefitItem>> GetAvailableBenefitsAsync();
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

public interface ISponsorService
{
    Task<List<SponsorSummary>> SearchSponsorsAsync(string searchTerm);
    Task<SponsorDetails?> GetSponsorByIdAsync(string sponsorId);
    Task<string> CreateSponsorAsync(CreateSponsorRequest request);
    Task UpdateSponsorAsync(string sponsorId, UpdateSponsorRequest request);
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
    public List<AttachmentInfo> Attachments { get; set; } = new();
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

public class BenefitPlanListItem
{
    public string PlanId { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public string SponsorId { get; set; } = string.Empty;
    public string SponsorName { get; set; } = string.Empty;
    public string ProductType { get; set; } = string.Empty; // HMO, PPO, EPO, HDHP
    public string Network { get; set; } = string.Empty;
    public int EnrolledMembers { get; set; }
    public int AssignedBenefits { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
}

public class BenefitPlanDetails : BenefitPlanListItem
{
    public string MetalTier { get; set; } = string.Empty; // Bronze, Silver, Gold, Platinum
    public decimal IndividualDeductible { get; set; }
    public decimal FamilyDeductible { get; set; }
    public decimal IndividualOOPMax { get; set; }
    public decimal FamilyOOPMax { get; set; }
    public decimal Coinsurance { get; set; }
    public decimal MonthlyPremium { get; set; }
    public string PlanYear { get; set; } = string.Empty;
    public List<PlanBenefit> Benefits { get; set; } = new();
    public List<string> Exclusions { get; set; } = new();
}

public class PlanBenefit
{
    public string BenefitId { get; set; } = string.Empty;
    public string ServiceType { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public decimal? Copay { get; set; }
    public decimal? CoinsurancePercent { get; set; }
    public decimal? CoveragePercent { get; set; }
    public int? AnnualLimit { get; set; }
    public bool PriorAuthRequired { get; set; }
}

public class BenefitItem
{
    public string BenefitId { get; set; } = string.Empty;
    public string ServiceType { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty; // Medical, Pharmacy, Dental, Vision, etc.
    public string Description { get; set; } = string.Empty;
    public decimal? DefaultCopay { get; set; }
    public decimal? DefaultCoinsurance { get; set; }
    public bool RequiresPriorAuth { get; set; }
}

public class CreateBenefitPlanRequest
{
    public string SponsorId { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public string ProductType { get; set; } = string.Empty;
    public string Network { get; set; } = string.Empty;
    public string MetalTier { get; set; } = string.Empty;
    public decimal IndividualDeductible { get; set; }
    public decimal FamilyDeductible { get; set; }
    public decimal IndividualOOPMax { get; set; }
    public decimal FamilyOOPMax { get; set; }
    public decimal Coinsurance { get; set; }
    public decimal MonthlyPremium { get; set; }
    public DateTime EffectiveDate { get; set; }
    public string PlanYear { get; set; } = string.Empty;
}

public class UpdateBenefitPlanRequest : CreateBenefitPlanRequest
{
    public string Status { get; set; } = string.Empty;
    public DateTime? TerminationDate { get; set; }
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
public class AttachmentInfo
{
    public string AttachmentId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public DateTime UploadedDate { get; set; }
    public string UploadedBy { get; set; } = string.Empty;
    public string AttachmentType { get; set; } = string.Empty;
    public string BlobPath { get; set; } = string.Empty;
}

public class SponsorSummary
{
    public string SponsorId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // Employer, Union, Association
    public string State { get; set; } = string.Empty;
    public int ActiveBenefitPlans { get; set; }
    public int TotalMembers { get; set; }
    public string Status { get; set; } = string.Empty; // Active, Inactive, Pending
    public DateTime ContractStartDate { get; set; }
    public DateTime? ContractEndDate { get; set; }
}

public class SponsorDetails : SponsorSummary
{
    public string TaxId { get; set; } = string.Empty;
    public string AddressLine1 { get; set; } = string.Empty;
    public string? AddressLine2 { get; set; }
    public string City { get; set; } = string.Empty;
    public string ZipCode { get; set; } = string.Empty;
    public string ContactName { get; set; } = string.Empty;
    public string ContactPhone { get; set; } = string.Empty;
    public string ContactEmail { get; set; } = string.Empty;
    public string BillingFrequency { get; set; } = string.Empty; // Monthly, Quarterly, Annual
    public string PaymentMethod { get; set; } = string.Empty; // ACH, Check, Wire
    public string GroupSizeTier { get; set; } = string.Empty; // Small (<50), Large (50+)
    public List<BenefitPlanSummary> BenefitPlans { get; set; } = new();
}

public class BenefitPlanSummary
{
    public string PlanId { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public string ProductType { get; set; } = string.Empty; // HMO, PPO, EPO, HDHP
    public int EnrolledMembers { get; set; }
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
}

public class CreateSponsorRequest
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string TaxId { get; set; } = string.Empty;
    public string AddressLine1 { get; set; } = string.Empty;
    public string? AddressLine2 { get; set; }
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string ZipCode { get; set; } = string.Empty;
    public string ContactName { get; set; } = string.Empty;
    public string ContactPhone { get; set; } = string.Empty;
    public string ContactEmail { get; set; } = string.Empty;
    public string BillingFrequency { get; set; } = string.Empty;
    public string PaymentMethod { get; set; } = string.Empty;
    public DateTime ContractStartDate { get; set; }
}

public class UpdateSponsorRequest : CreateSponsorRequest
{
    public string Status { get; set; } = string.Empty;
    public DateTime? ContractEndDate { get; set; }
}