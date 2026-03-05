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
    Task<List<ProviderListItem>> SearchProvidersAsync(string? specialty = null, string? networkStatus = null, string? searchTerm = null);
    Task<ProviderDetails?> GetProviderByIdAsync(string providerId);
    Task<string> CreateProviderAsync(CreateProviderRequest request);
    Task UpdateProviderAsync(string providerId, UpdateProviderRequest request);
    Task<List<string>> GetSpecialtiesAsync();
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

public interface IReferenceDataService
{
    Task<List<MedicalCode>> SearchCodesAsync(string? codeSystem = null, string? searchTerm = null);
    Task<MedicalCodeDetails?> GetCodeDetailsAsync(string codeSystem, string code);
    Task<List<string>> GetCodeSystemsAsync();
    Task<CodeUsageStats> GetCodeUsageStatsAsync(string codeSystem, string code);
}

public interface ISponsorService
{
    Task<List<SponsorSummary>> SearchSponsorsAsync(string searchTerm);
    Task<SponsorDetails?> GetSponsorByIdAsync(string sponsorId);
    Task<string> CreateSponsorAsync(CreateSponsorRequest request);
    Task UpdateSponsorAsync(string sponsorId, UpdateSponsorRequest request);
}

public interface ITenantService
{
    Task<TenantSubscription?> GetSubscriptionByAzureTenantIdAsync(string azureTenantId);
    Task<TenantSubscription?> GetDemoTenantAsync();
    Task<bool> IsMemberOfTenantAsync(string azureTenantId, string userEmail);
    Task<string> CreateTenantAsync(CreateTenantRequest request);
}

public interface ISalesInquiryService
{
    Task<string> CreateInquiryAsync(CreateSalesInquiryRequest request);
    Task<List<SalesInquiry>> GetInquiriesAsync(string? status = null, int limit = 100);
    Task<SalesInquiry?> GetInquiryByIdAsync(string inquiryId);
    Task UpdateInquiryStatusAsync(string inquiryId, string status, string? notes = null);
}

public interface IEmailNotificationService
{
    Task SendSalesInquiryNotificationAsync(SalesInquiry inquiry);
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

public class ProviderListItem
{
    public string ProviderId { get; set; } = string.Empty;
    public string NPI { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string PracticeType { get; set; } = string.Empty; // Individual, Group
    public string Specialty { get; set; } = string.Empty;
    public string PracticeName { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string NetworkStatus { get; set; } = string.Empty; // In-Network, Out-of-Network, Pending
    public string CredentialingStatus { get; set; } = string.Empty; // Active, Pending, Expired
    public int NetworkCount { get; set; }
    public DateTime? LastClaimDate { get; set; }
}

public class ProviderDetails : ProviderListItem
{
    public string TaxonomyCode { get; set; } = string.Empty;
    public List<string> BoardCertifications { get; set; } = new();
    public List<PracticeLocation> Locations { get; set; } = new();
    public List<ProviderCredential> Credentials { get; set; } = new();
    public List<NetworkAssignment> NetworkAssignments { get; set; } = new();
    public ProviderContract? Contract { get; set; }
    public ProviderPerformance? Performance { get; set; }
}

public class PracticeLocation
{
    public string LocationId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string AddressLine1 { get; set; } = string.Empty;
    public string? AddressLine2 { get; set; }
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string ZipCode { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string? Fax { get; set; }
    public bool IsPrimary { get; set; }
}

public class ProviderCredential
{
    public string CredentialType { get; set; } = string.Empty; // License, DEA, Board Certification
    public string Number { get; set; } = string.Empty;
    public string IssuingState { get; set; } = string.Empty;
    public DateTime IssueDate { get; set; }
    public DateTime ExpirationDate { get; set; }
    public string Status { get; set; } = string.Empty; // Active, Expired, Suspended
}

public class NetworkAssignment
{
    public string NetworkId { get; set; } = string.Empty;
    public string NetworkName { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
    public string Status { get; set; } = string.Empty;
}

public class ProviderContract
{
    public string ContractId { get; set; } = string.Empty;
    public string ReimbursementMethod { get; set; } = string.Empty; // Fee Schedule, Capitation, Case Rate
    public string FeeScheduleTier { get; set; } = string.Empty;
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
    public decimal? CapitationRate { get; set; }
}

public class ProviderPerformance
{
    public int ClaimsLast90Days { get; set; }
    public decimal TotalBilledLast90Days { get; set; }
    public decimal AvgClaimAmount { get; set; }
    public int AuthorizationRequests { get; set; }
    public decimal AuthorizationApprovalRate { get; set; }
    public int DenialCount { get; set; }
    public decimal DenialRate { get; set; }
    public decimal AvgProcessingTimeDays { get; set; }
    public decimal? QualityScore { get; set; }
}

public class CreateProviderRequest
{
    public string NPI { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string PracticeType { get; set; } = string.Empty;
    public string Specialty { get; set; } = string.Empty;
    public string PracticeName { get; set; } = string.Empty;
    public string TaxonomyCode { get; set; } = string.Empty;
    public string AddressLine1 { get; set; } = string.Empty;
    public string? AddressLine2 { get; set; }
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string ZipCode { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string? Fax { get; set; }
    public string? Email { get; set; }
}

public class UpdateProviderRequest : CreateProviderRequest
{
    public string CredentialingStatus { get; set; } = string.Empty;
    public string NetworkStatus { get; set; } = string.Empty;
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

public class MedicalCode
{
    public string CodeSystem { get; set; } = string.Empty; // CPT, ICD-10-CM, HCPCS, Revenue, etc.
    public string Code { get; set; } = string.Empty;
    public string ShortDescription { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public DateTime EffectiveDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string Status { get; set; } = string.Empty; // Active, Deprecated
}

public class MedicalCodeDetails : MedicalCode
{
    public string LongDescription { get; set; } = string.Empty;
    public List<string> Keywords { get; set; } = new();
    public List<RelatedCode> RelatedCodes { get; set; } = new();
    public string? ParentCode { get; set; }
    public List<string> ChildCodes { get; set; } = new();
    public bool RequiresPriorAuth { get; set; }
    public string? ClinicalNotes { get; set; }
}

public class RelatedCode
{
    public string CodeSystem { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string RelationType { get; set; } = string.Empty; // CrossReference, Alternative, Replacement
}

public class CodeUsageStats
{
    public string CodeSystem { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public int ClaimsCount { get; set; }
    public int AuthorizationsCount { get; set; }
    public int BenefitsCount { get; set; }
    public DateTime? LastUsedDate { get; set; }
    public decimal TotalBilledAmount { get; set; }
}

public class TenantSubscription
{
    public string TenantId { get; set; } = string.Empty;
    public string AzureTenantId { get; set; } = string.Empty;
    public string OrganizationName { get; set; } = string.Empty;
    public string SubscriptionStatus { get; set; } = string.Empty; // Active, Trial, Expired, Cancelled
    public string Tier { get; set; } = string.Empty; // starter, professional, enterprise
    public bool IsDemo { get; set; }
    public string? StripeCustomerId { get; set; }
    public string? StripeSubscriptionId { get; set; }
    public DateTime? TrialEndsAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<string> AdminEmails { get; set; } = new();
}

public class CreateTenantRequest
{
    public string AzureTenantId { get; set; } = string.Empty;
    public string OrganizationName { get; set; } = string.Empty;
    public string TenantDisplayName { get; set; } = string.Empty;
    public string Tier { get; set; } = string.Empty;
    public string AdminEmail { get; set; } = string.Empty;
    public string? StripePaymentMethodId { get; set; }
    public string? StripeCustomerId { get; set; }
    public string? StripeSubscriptionId { get; set; }
    public List<string> EnabledModules { get; set; } = new();
}

public class SalesInquiry
{
    public string Id { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string CompanyName { get; set; } = string.Empty;
    public string? JobTitle { get; set; }
    public string InquiryType { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Status { get; set; } = "New"; // New, Contacted, Qualified, Closed
    public string Source { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? ContactedAt { get; set; }
    public string? Notes { get; set; }
}

public class CreateSalesInquiryRequest
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string CompanyName { get; set; } = string.Empty;
    public string? JobTitle { get; set; }
    public string InquiryType { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Source { get; set; } = "Contact Sales Page";
}
