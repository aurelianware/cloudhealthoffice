using System.ComponentModel.DataAnnotations;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace CloudHealthOffice.Portal.Services;

public interface IClaimsService
{
    Task<List<ClaimSummary>> GetRecentClaimsAsync(int count);
    Task<ClaimSearchResult> SearchClaimsAsync(ClaimSearchRequest request);
    Task<ClaimDetails?> GetClaimByIdAsync(string claimId);
    Task<string> SubmitClaimAsync(SubmitClaimRequest request);
    Task UpdateClaimStatusAsync(string claimId, string status, string? notes = null);
    Task<AdjudicationTransparencyData?> GetAdjudicationDataAsync(string claimId);
}

public interface IEligibilityService
{
    Task<EligibilityResponse> CheckEligibilityAsync(object request);
}

public interface IMemberService
{
    Task<List<MemberSummary>> SearchMembersAsync(string searchTerm);
    Task<MemberDetails?> GetMemberByIdAsync(string memberId);
    Task<MemberPcp?> GetMemberPcpAsync(string memberId);
    Task AssignPcpAsync(AssignPcpRequest request);
    Task<List<CoverageHistoryEvent>> GetCoverageHistoryAsync(string memberId);
    Task<List<Enrollment834Record>> GetMember834TransactionsAsync(string memberId);
    Task TerminateEnrollmentAsync(TerminateEnrollmentRequest request);
    Task<MemberAccumulators> GetAccumulatorsAsync(string memberId);
}

public class MemberAccumulators
{
    public decimal IndividualDeductibleUsed { get; set; }
    public decimal IndividualDeductibleLimit { get; set; }
    public decimal FamilyDeductibleUsed { get; set; }
    public decimal FamilyDeductibleLimit { get; set; }
    public decimal IndividualOopUsed { get; set; }
    public decimal IndividualOopLimit { get; set; }
    public decimal FamilyOopUsed { get; set; }
    public decimal FamilyOopLimit { get; set; }
    public List<ServiceAccumulator> ServiceAccumulators { get; set; } = new();
    public List<AccumulatorActivity> RecentActivity { get; set; } = new();
}

public class ServiceAccumulator
{
    public string ServiceType { get; set; } = string.Empty;
    public int Used { get; set; }
    public int Limit { get; set; }
    public string UnitType { get; set; } = "visits";
}

public class AccumulatorActivity
{
    public string ClaimId { get; set; } = string.Empty;
    public DateTime ServiceDate { get; set; }
    public decimal DeductibleApplied { get; set; }
    public decimal CopayApplied { get; set; }
    public decimal CoinsuranceApplied { get; set; }
    public decimal PlanPaid { get; set; }
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
    Task<List<ServiceBenefitRule>> GetServiceBenefitRulesAsync(string planId);
    Task UpdateServiceBenefitRulesAsync(UpdateServiceBenefitRulesRequest request);
    Task<AccumulatorConfiguration?> GetAccumulatorConfigAsync(string planId);
    Task UpdateAccumulatorConfigAsync(string planId, AccumulatorConfiguration config);
}

public interface IWorkflowService
{
    Task<List<WorkflowRun>> GetWorkflowRunsAsync(int limit = 20);
    Task<WorkflowDetails?> GetWorkflowDetailsAsync(string workflowId);
    Task<List<WorkflowRun>> GetActiveWorkflowsAsync();
    Task<bool> RetriggerWorkflowAsync(string workflowId);
}

public interface IMetricsService
{
    Task<DashboardMetrics> GetDashboardMetricsAsync();
    Task<OperationalAlerts> GetOperationalAlertsAsync();
    Task<EdiVolumeSummary> GetTodayEdiVolumeAsync();
}

public class OperationalAlerts
{
    public int WorkQueueCount { get; set; }
    public int PendingRfais { get; set; }
    public int AppealsDueThisWeek { get; set; }
    public int ApproachingFilingLimit { get; set; }
}

public class EdiVolumeSummary
{
    public int Claims837Received { get; set; }
    public int Era835Generated { get; set; }
    public int Eligibility270271 { get; set; }
    public int PriorAuth278 { get; set; }
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
    Task UpdateTenantAsync(string azureTenantId, UpdateTenantRequest request);
    Task DeleteTenantAsync(string azureTenantId);
    Task<List<TenantSubscription>> GetAllSubscriptionsAsync();
    Task UpdateSubscriptionStatusAsync(string azureTenantId, string status);
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
    public string ClaimNumber { get; set; } = string.Empty;
    public string MemberName { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string ClaimType { get; set; } = string.Empty; // Professional, Institutional
    public decimal TotalChargeAmount { get; set; }
    public decimal AllowedAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime ServiceDateFrom { get; set; }
    public DateTime ServiceDateTo { get; set; }
    public DateTime SubmittedDate { get; set; }
    public DateTime? AdjudicatedDate { get; set; }
    public int ProcessingTimeMs { get; set; }
    public string? PriorAuthorizationNumber { get; set; }
    public int LineCount { get; set; }
}

public class ClaimDetails : ClaimSummary
{
    public string SubscriberId { get; set; } = string.Empty;
    public string SubscriberName { get; set; } = string.Empty;
    public string PatientName { get; set; } = string.Empty;
    public string PatientRelationship { get; set; } = string.Empty;
    public string BillingProviderName { get; set; } = string.Empty;
    public string BillingProviderNPI { get; set; } = string.Empty;
    public string? RenderingProviderName { get; set; }
    public string? RenderingProviderNPI { get; set; }
    public string? FacilityName { get; set; }
    public string? FacilityNPI { get; set; }
    public string PlaceOfService { get; set; } = string.Empty;
    public decimal DeductibleAmount { get; set; }
    public decimal CoinsuranceAmount { get; set; }
    public decimal CopayAmount { get; set; }
    public decimal PatientResponsibility { get; set; }
    public string? ClaimNotes { get; set; }
    public string? ReferralNumber { get; set; }
    public DateTime ReceivedDate { get; set; }
    public DateTime? PaidDate { get; set; }
    public string? CheckNumber { get; set; }
    public string? DenialReason { get; set; }
    public List<ClaimDiagnosisCode> DiagnosisCodes { get; set; } = new();
    public List<ClaimServiceLine> ServiceLines { get; set; } = new();
    public ClaimAdjustmentInfo? AdjustmentInfo { get; set; }
    public bool IsEditable { get; set; }
    public bool CanApprove { get; set; }
    public bool CanDeny { get; set; }
    public bool CanReverse { get; set; }
    public List<ClaimAudit> AuditTrail { get; set; } = new();
}

public class ClaimDiagnosisCode
{
    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // Principal, Secondary
    public int PointerNumber { get; set; }
}

public class ClaimServiceLine
{
    public int LineNumber { get; set; }
    public string ProcedureCode { get; set; } = string.Empty;
    public string ProcedureDescription { get; set; } = string.Empty;
    public List<string> Modifiers { get; set; } = new();
    public decimal Units { get; set; }
    public decimal ChargeAmount { get; set; }
    public decimal AllowedAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal PatientResponsibility { get; set; }
    public DateTime ServiceDateFrom { get; set; }
    public DateTime ServiceDateTo { get; set; }
    public string? RevenueCode { get; set; } // Institutional
    public List<int> DiagnosisPointers { get; set; } = new();
    public List<ClaimLineAdjustment> Adjustments { get; set; } = new();
    public string? LineStatus { get; set; }
}

public class ClaimLineAdjustment
{
    public string GroupCode { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Description { get; set; } = string.Empty;
}

public class ClaimAdjustmentInfo
{
    public string AdjustmentType { get; set; } = string.Empty; // Reversal, Adjustment, Correction
    public string? OriginalClaimId { get; set; }
    public string? RelatedClaimId { get; set; }
    public decimal AdjustmentAmount { get; set; }
    public string? Reason { get; set; }
    public DateTime? AdjustmentDate { get; set; }
    public string? AdjustedBy { get; set; }
}

public class ClaimAudit
{
    public DateTime Timestamp { get; set; }
    public string Action { get; set; } = string.Empty;
    public string ChangedBy { get; set; } = string.Empty;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string? Notes { get; set; }
}

public class ServiceLine
{
    public string ProcedureCode { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal ChargeAmount { get; set; }
    public decimal AllowedAmount { get; set; }
    public decimal PayerAmount { get; set; }
}

public class ClaimSearchRequest
{
    public string? ClaimNumber { get; set; }
    public string? MemberId { get; set; }
    public string? MemberName { get; set; }
    public string? ProviderId { get; set; }
    public string? ProviderName { get; set; }
    public string? ClaimType { get; set; } // Professional, Institutional
    public DateTime? ServiceDateFrom { get; set; }
    public DateTime? ServiceDateTo { get; set; }
    public string? Status { get; set; } // Submitted, Received, InAdjudication, Pended, Approved, Denied, Paid, Voided, PartiallyPaid
    public string? AuthorizationNumber { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 25;
    public string? SortBy { get; set; } = "SubmittedDate";
    public string? SortOrder { get; set; } = "Descending";
}

public class ClaimSearchResult
{
    public List<ClaimSummary> Claims { get; set; } = new();
    public int TotalCount { get; set; }
    public int PageNumber { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => (TotalCount + PageSize - 1) / PageSize;
    public decimal TotalChargeAmount { get; set; }
    public decimal TotalAllowedAmount { get; set; }
    public decimal TotalPaidAmount { get; set; }
    public int ApprovedCount { get; set; }
    public int DeniedCount { get; set; }
    public int PendingCount { get; set; }
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
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }
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
    public string? Notes { get; set; }
}

public class CreateTenantRequest
{
    [Required]
    [StringLength(300)]
    public string OrganizationName { get; set; } = string.Empty;

    [Required]
    public string AzureTenantId { get; set; } = string.Empty;

    [Required]
    public string Tier { get; set; } = "starter";

    public string TenantDisplayName { get; set; } = string.Empty;

    public string SubscriptionStatus { get; set; } = "Trial";

    public string AdminEmail { get; set; } = string.Empty;

    public List<string> AdminEmails { get; set; } = new();

    public bool IsDemo { get; set; }

    public string? Notes { get; set; }

    public string? StripePaymentMethodId { get; set; }
    public string? StripeCustomerId { get; set; }
    public string? StripeSubscriptionId { get; set; }
    public List<string> EnabledModules { get; set; } = new();
}

public class UpdateTenantRequest
{
    [StringLength(300)]
    public string? OrganizationName { get; set; }

    public string? Tier { get; set; }

    public string? SubscriptionStatus { get; set; }

    public List<string>? AdminEmails { get; set; }

    public bool? IsDemo { get; set; }

    public string? Notes { get; set; }
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

// Operating Mode
public interface IOperatingModeService
{
    Task<OperatingModeConfiguration> GetOperatingModeAsync(string tenantId);
}

// EDI Operations
public interface IEdiOperationsService
{
    Task<List<Edi834Batch>> Get834BatchesAsync(DateTime? from = null, DateTime? to = null);
    Task<List<Enrollment834Record>> Get834BatchRecordsAsync(string batchId);
    Task Resolve834RecordAsync(Edi834ResolutionRequest request);
    Task<List<ClaimAcknowledgmentSummary>> Get277CaAcknowledgmentsAsync(DateTime? from = null, DateTime? to = null);
    Task<Stream> Download277CaAsync(string claimId);
    Task<List<EraSummary>> GetErasAsync(DateTime? from = null, DateTime? to = null);
    Task<Stream> DownloadEraAsync(string paymentId);
    Task<List<EdiTransactionHistoryItem>> GetTransactionHistoryAsync(DateTime? from, DateTime? to, string? transactionType, string? partnerId, string? status, int pageNumber, int pageSize);
}

// Payment Runs
public interface IPaymentRunService
{
    Task<List<PaymentRunSummary>> GetPaymentRunsAsync(int limit = 50);
    Task<PaymentRunDetails?> GetPaymentRunByIdAsync(string runId);
    Task<string> CreatePaymentRunAsync(CreatePaymentRunRequest request);
    Task CancelPaymentRunAsync(string runId);
    Task<Stream> DownloadEraForRunAsync(string runId);
}

// Premium Billing
public interface IPremiumBillingService
{
    Task<List<BillingCycle>> GetBillingCyclesAsync(string? sponsorId = null, string? status = null);
    Task<BillingCycleDetails?> GetBillingCycleByIdAsync(string cycleId);
    Task<string> GenerateInvoiceAsync(CreateInvoiceRequest request);
    Task<List<PremiumRate>> GetPremiumRatesAsync(string? planId = null);
    Task UpdatePremiumRateAsync(string rateId, decimal newRate, DateTime effectiveDate);
    Task MarkCycleAsPaidAsync(string cycleId, DateTime paidDate);
    Task<Stream> DownloadInvoiceAsync(string cycleId);
}

// Reporting
public interface IReportingService
{
    Task<ClaimsSummaryReport> GetClaimsSummaryAsync(ReportRequest request);
    Task<PaymentSummaryReport> GetPaymentSummaryAsync(ReportRequest request);
    Task<EligibilityStatsReport> GetEligibilityStatsAsync(ReportRequest request);
    Task<AuthApprovalReport> GetAuthApprovalReportAsync(ReportRequest request);
    Task<List<ClaimsByProvider>> GetProviderPerformanceAsync(ReportRequest request);
}

public class OperatingModeConfiguration
{
    public static readonly Dictionary<string, string> DefaultEngines = new(StringComparer.OrdinalIgnoreCase)
    {
        { "benefitCalculation", "replace" },
        { "rateResolution", "replace" },
        { "ncciEdits", "replace" },
        { "eligibilityVerification", "replace" },
        { "claimsAdjudication", "replace" }
    };

    public string TenantId { get; set; } = string.Empty;
    public Dictionary<string, string> Engines { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime? UpdatedAt { get; set; }
}

// ── PR13: Member Enrollment Operations ──────────────────────────────────────

public class MemberPcp
{
    public string ProviderId { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public string NPI { get; set; } = string.Empty;
    public string Specialty { get; set; } = string.Empty;
    public string NetworkStatus { get; set; } = string.Empty;
    public DateTime AssignedDate { get; set; }
    public string? PracticeName { get; set; }
    public string? Phone { get; set; }
}

public class CoverageHistoryEvent
{
    public string EventId { get; set; } = string.Empty;
    public DateTime EventDate { get; set; }
    public string EventType { get; set; } = string.Empty; // Enrolled, PlanChange, PcpChange, Terminated, Reinstated
    public string Description { get; set; } = string.Empty;
    public string? ChangedBy { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
}

public class Enrollment834Record
{
    public string TransactionId { get; set; } = string.Empty;
    public string BatchId { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string MemberName { get; set; } = string.Empty;
    public string MaintenanceTypeCode { get; set; } = string.Empty; // 001=Change, 021=Add, 024=Cancel
    public string MaintenanceReasonCode { get; set; } = string.Empty;
    public string TransactionSetPurpose { get; set; } = string.Empty;
    public DateTime TransactionDate { get; set; }
    public string Status { get; set; } = string.Empty; // Accepted, Rejected, Pending
    public List<string> Errors { get; set; } = new();
    public string? RawSegmentPreview { get; set; }
}

public class AssignPcpRequest
{
    public string MemberId { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public DateTime EffectiveDate { get; set; }
    public string? Reason { get; set; }
}

public class TerminateEnrollmentRequest
{
    public string MemberId { get; set; } = string.Empty;
    public string CoverageId { get; set; } = string.Empty;
    public DateTime TerminationDate { get; set; }
    public string ReasonCode { get; set; } = string.Empty; // 1=Voluntary, 2=Involuntary, 3=Death, 4=Medicare, 5=Medicaid, 6=Other
    public string? Notes { get; set; }
}

// ── PR14: EDI Transaction Operations ────────────────────────────────────────

public class Edi834Batch
{
    public string BatchId { get; set; } = string.Empty;
    public string TradingPartnerId { get; set; } = string.Empty;
    public string TradingPartnerName { get; set; } = string.Empty;
    public DateTime ReceivedDate { get; set; }
    public int TotalRecords { get; set; }
    public int AcceptedCount { get; set; }
    public int RejectedCount { get; set; }
    public int PendingCount { get; set; }
    public string Status { get; set; } = string.Empty; // Processing, Completed, Failed, PartiallyAccepted
    public string? OriginalFileName { get; set; }
}

public class ClaimAcknowledgmentSummary
{
    public string AckId { get; set; } = string.Empty;
    public string ClaimId { get; set; } = string.Empty;
    public string ClaimNumber { get; set; } = string.Empty;
    public string MemberName { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public DateTime GeneratedDate { get; set; }
    public string AckStatus { get; set; } = string.Empty; // Accepted, Rejected, Pended
    public string StatusCategoryCode { get; set; } = string.Empty;
    public string StatusCode { get; set; } = string.Empty;
    public string StatusDescription { get; set; } = string.Empty;
}

public class EraSummary
{
    public string EraId { get; set; } = string.Empty;
    public string PaymentId { get; set; } = string.Empty;
    public string PayerName { get; set; } = string.Empty;
    public string PayeeNPI { get; set; } = string.Empty;
    public string PayeeName { get; set; } = string.Empty;
    public DateTime PaymentDate { get; set; }
    public string PaymentMethod { get; set; } = string.Empty; // ACH, CHK
    public string CheckNumber { get; set; } = string.Empty;
    public decimal TotalPaymentAmount { get; set; }
    public int ClaimCount { get; set; }
    public string Status { get; set; } = string.Empty; // Generated, Transmitted, Acknowledged
}

public class EdiTransactionHistoryItem
{
    public string TransactionId { get; set; } = string.Empty;
    public string TransactionType { get; set; } = string.Empty; // 834, 835, 277CA, 270, 271, 278
    public DateTime TransactionDate { get; set; }
    public string TradingPartnerId { get; set; } = string.Empty;
    public string TradingPartnerName { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty; // Inbound, Outbound
    public string Status { get; set; } = string.Empty;
    public string? ErrorSummary { get; set; }
    public int RecordCount { get; set; }
}

public class Edi834ResolutionRequest
{
    public string BatchId { get; set; } = string.Empty;
    public string TransactionId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty; // Accept, Reject, Hold
    public string? Notes { get; set; }
}

// ── PR15: Payment Runs ───────────────────────────────────────────────────────

public class PaymentRunSummary
{
    public string RunId { get; set; } = string.Empty;
    public string RunName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty; // Pending, Running, Completed, Failed, Cancelled
    public DateTime CreatedDate { get; set; }
    public DateTime? StartedDate { get; set; }
    public DateTime? CompletedDate { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public int ClaimCount { get; set; }
    public int ProcessedCount { get; set; }
    public decimal TotalAmount { get; set; }
    public string? ErrorMessage { get; set; }
    public string? EraFileUrl { get; set; }
}

public class PaymentRunDetails : PaymentRunSummary
{
    public DateTime ClaimServiceDateFrom { get; set; }
    public DateTime ClaimServiceDateTo { get; set; }
    public string? SponsorFilter { get; set; }
    public string? PlanFilter { get; set; }
    public List<PaymentRunClaimItem> Claims { get; set; } = new();
    public decimal TotalCharges { get; set; }
    public decimal TotalAllowed { get; set; }
    public decimal TotalMemberResponsibility { get; set; }
    public int ApprovedCount { get; set; }
    public int DeniedCount { get; set; }
    public int AdjustmentCount { get; set; }
}

public class PaymentRunClaimItem
{
    public string ClaimId { get; set; } = string.Empty;
    public string ClaimNumber { get; set; } = string.Empty;
    public string MemberName { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public decimal ChargeAmount { get; set; }
    public decimal AllowedAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal MemberResponsibility { get; set; }
    public string PaymentStatus { get; set; } = string.Empty; // Included, Excluded, Adjusted
}

public class CreatePaymentRunRequest
{
    public string RunName { get; set; } = string.Empty;
    public DateTime ClaimServiceDateFrom { get; set; }
    public DateTime ClaimServiceDateTo { get; set; }
    public string? SponsorId { get; set; }
    public string? PlanId { get; set; }
    public List<string> ClaimStatuses { get; set; } = new() { "Approved" };
}

// ── PR15: Premium Billing ────────────────────────────────────────────────────

public class BillingCycle
{
    public string CycleId { get; set; } = string.Empty;
    public string SponsorId { get; set; } = string.Empty;
    public string SponsorName { get; set; } = string.Empty;
    public string BillingPeriod { get; set; } = string.Empty; // YYYY-MM
    public string BillingFrequency { get; set; } = string.Empty; // Monthly, Quarterly
    public DateTime DueDate { get; set; }
    public decimal TotalPremium { get; set; }
    public string Status { get; set; } = string.Empty; // Draft, Sent, Paid, Overdue, Void
    public DateTime? PaidDate { get; set; }
    public string? InvoiceNumber { get; set; }
    public int MemberCount { get; set; }
}

public class BillingCycleDetails : BillingCycle
{
    public List<BillingLineItem> LineItems { get; set; } = new();
    public decimal TaxAmount { get; set; }
    public decimal AdjustmentAmount { get; set; }
    public string? Notes { get; set; }
}

public class BillingLineItem
{
    public string PlanId { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public string CoverageLevel { get; set; } = string.Empty; // Employee, Employee+Spouse, Family
    public int MemberCount { get; set; }
    public decimal UnitRate { get; set; }
    public decimal SubTotal { get; set; }
    public string? AgeBand { get; set; }
}

public class PremiumRate
{
    public string RateId { get; set; } = string.Empty;
    public string PlanId { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public string CoverageLevel { get; set; } = string.Empty;
    public string? AgeBand { get; set; }
    public decimal Rate { get; set; }
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
    public bool IsEditing { get; set; } // UI state only
    public decimal EditRate { get; set; } // UI edit buffer
}

public class CreateInvoiceRequest
{
    public string SponsorId { get; set; } = string.Empty;
    public string BillingPeriod { get; set; } = string.Empty;
    public DateTime DueDate { get; set; }
    public string? Notes { get; set; }
}

// ── PR16: Enhanced Benefit Configuration ────────────────────────────────────

public class ServiceBenefitRule
{
    public string RuleId { get; set; } = string.Empty;
    public string ServiceCategory { get; set; } = string.Empty; // Medical, Pharmacy, Dental, Vision, MentalHealth
    public string ServiceTypeCode { get; set; } = string.Empty;
    public string ServiceTypeDescription { get; set; } = string.Empty;
    public string NetworkTier { get; set; } = string.Empty; // Tier1, Tier2, OutOfNetwork
    public decimal? Copay { get; set; }
    public decimal? CoinsurancePercent { get; set; }
    public bool SubjectToDeductible { get; set; }
    public int? AnnualVisitLimit { get; set; }
    public decimal? AnnualDollarLimit { get; set; }
    public bool PriorAuthRequired { get; set; }
    public string? PriorAuthThreshold { get; set; }
    public string DeductibleAccumulatorGroup { get; set; } = "Individual";
    public string OopAccumulatorGroup { get; set; } = "Individual";
    public bool CrossAccumulatesWithMedical { get; set; }
    public bool IsEditing { get; set; } // UI state
}

public class AccumulatorConfiguration
{
    public string ConfigId { get; set; } = string.Empty;
    public string PlanId { get; set; } = string.Empty;
    public decimal IndividualDeductible { get; set; }
    public decimal FamilyDeductible { get; set; }
    public decimal IndividualOopMax { get; set; }
    public decimal FamilyOopMax { get; set; }
    public bool PharmacyCrossAccumulatesDeductible { get; set; }
    public bool PharmacyCrossAccumulatesOop { get; set; }
    public bool DentalCrossAccumulatesOop { get; set; }
    public string EmbeddedOrAggregate { get; set; } = "Embedded"; // Embedded, Aggregate
}

public class UpdateServiceBenefitRulesRequest
{
    public string PlanId { get; set; } = string.Empty;
    public List<ServiceBenefitRule> Rules { get; set; } = new();
    public AccumulatorConfiguration Accumulators { get; set; } = new();
}

// ── PR16: Adjudication Transparency ─────────────────────────────────────────

public class NcciEditResult
{
    public string EditCode { get; set; } = string.Empty;
    public string EditType { get; set; } = string.Empty; // MUE, NCCI-PTP, NCCI-MU
    public string Description { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string? FailureReason { get; set; }
    public string? AffectedProcedureCode { get; set; }
    public string? AffectedModifier { get; set; }
    public string? ResolutionApplied { get; set; }
}

public class FeeScheduleResult
{
    public string ProcedureCode { get; set; } = string.Empty;
    public string Modifier { get; set; } = string.Empty;
    public string FeeScheduleName { get; set; } = string.Empty;
    public decimal BilledAmount { get; set; }
    public decimal AllowedAmount { get; set; }
    public decimal ContractedRate { get; set; }
    public string RateBasis { get; set; } = string.Empty; // MedicareRVU, PercentOfCharge, CaseRate
    public decimal RateMultiplier { get; set; }
    public string NetworkTier { get; set; } = string.Empty;
}

public class AccumulatorUpdate
{
    public string AccumulatorType { get; set; } = string.Empty; // IndividualDeductible, FamilyDeductible, IndividualOop, FamilyOop
    public decimal AmountApplied { get; set; }
    public decimal NewBalance { get; set; }
    public decimal Limit { get; set; }
}

public class BenefitCalculationResult
{
    public string ServiceType { get; set; } = string.Empty;
    public string BenefitRuleApplied { get; set; } = string.Empty;
    public string NetworkTier { get; set; } = string.Empty;
    public decimal AllowedAmount { get; set; }
    public decimal DeductibleApplied { get; set; }
    public decimal DeductibleRemaining { get; set; }
    public decimal CopayAmount { get; set; }
    public decimal CoinsuranceAmount { get; set; }
    public decimal PlanPayment { get; set; }
    public decimal MemberResponsibility { get; set; }
    public bool DeductibleMet { get; set; }
    public bool OopMaxMet { get; set; }
    public decimal IndividualDeductibleBalance { get; set; }
    public decimal IndividualDeductibleLimit { get; set; }
    public decimal IndividualOopBalance { get; set; }
    public decimal IndividualOopLimit { get; set; }
    public List<AccumulatorUpdate> AccumulatorUpdates { get; set; } = new();
}

public class AdjudicationStep
{
    public string StepName { get; set; } = string.Empty;
    public int StepNumber { get; set; }
    public string Status { get; set; } = string.Empty; // Passed, Failed, Skipped, Warning
    public DateTime? Timestamp { get; set; }
    public int? DurationMs { get; set; }
    public string? Summary { get; set; }
    public string? ErrorDetail { get; set; }
}

public class AdjudicationTransparencyData
{
    public List<AdjudicationStep> Steps { get; set; } = new();
    public List<NcciEditResult> NcciResults { get; set; } = new();
    public List<FeeScheduleResult> FeeScheduleResults { get; set; } = new();
    public BenefitCalculationResult? BenefitCalculation { get; set; }
}

// ── PR17: Workflow Extensions ────────────────────────────────────────────────

public class WorkflowRunExtended : WorkflowRun
{
    public string WorkflowTemplate { get; set; } = string.Empty;
    public string TriggerSource { get; set; } = string.Empty; // API, Scheduled, Manual
    public int StepCount { get; set; }
    public int CompletedStepCount { get; set; }
    public List<WorkflowStepExtended> DetailedSteps { get; set; } = new();
}

public class WorkflowStepExtended : WorkflowStep
{
    public int? DurationMs { get; set; }
    public string? NodeName { get; set; }
    public string? Message { get; set; }
    public int StepNumber { get; set; }
}

// ── PR17: Reporting DTOs ─────────────────────────────────────────────────────

public class ReportRequest
{
    public DateTime DateFrom { get; set; }
    public DateTime DateTo { get; set; }
    public string? ProviderId { get; set; }
    public string? SponsorId { get; set; }
    public string? PlanId { get; set; }
}

public class ClaimsByDateBucket
{
    public DateTime Date { get; set; }
    public int Count { get; set; }
    public decimal TotalAmount { get; set; }
}

public class ClaimsByProvider
{
    public string ProviderId { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public string Specialty { get; set; } = string.Empty;
    public int ClaimCount { get; set; }
    public decimal TotalBilled { get; set; }
    public decimal TotalPaid { get; set; }
    public double DenialRate { get; set; }
    public double AvgProcessingDays { get; set; }
}

public class ClaimsByDiagnosis
{
    public string DiagnosisCode { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int ClaimCount { get; set; }
    public decimal TotalAmount { get; set; }
}

public class ClaimsSummaryReport
{
    public DateTime PeriodFrom { get; set; }
    public DateTime PeriodTo { get; set; }
    public int TotalClaims { get; set; }
    public decimal TotalCharges { get; set; }
    public decimal TotalAllowed { get; set; }
    public decimal TotalPaid { get; set; }
    public int ApprovedCount { get; set; }
    public int DeniedCount { get; set; }
    public int PendedCount { get; set; }
    public double ApprovalRate { get; set; }
    public decimal AvgClaimAmount { get; set; }
    public List<ClaimsByDateBucket> DailyBreakdown { get; set; } = new();
    public List<ClaimsByProvider> TopProviders { get; set; } = new();
    public List<ClaimsByDiagnosis> TopDiagnoses { get; set; } = new();
}

public class EraByPeriod
{
    public string Period { get; set; } = string.Empty; // YYYY-MM
    public int EraCount { get; set; }
    public decimal TotalAmount { get; set; }
}

public class PaymentSummaryReport
{
    public DateTime PeriodFrom { get; set; }
    public DateTime PeriodTo { get; set; }
    public int EraCount { get; set; }
    public decimal TotalEraAmount { get; set; }
    public decimal AvgEraAmount { get; set; }
    public List<EraByPeriod> ByPeriod { get; set; } = new();
}

public class EligibilityStatsReport
{
    public DateTime PeriodFrom { get; set; }
    public DateTime PeriodTo { get; set; }
    public int TotalRequests { get; set; }
    public int EligibleCount { get; set; }
    public int IneligibleCount { get; set; }
    public double EligibilityRate { get; set; }
    public double AvgResponseTimeMs { get; set; }
}

public class AuthByServiceType
{
    public string ServiceType { get; set; } = string.Empty;
    public int Count { get; set; }
    public int ApprovedCount { get; set; }
    public int DeniedCount { get; set; }
    public double ApprovalRate { get; set; }
    public double AvgDecisionDays { get; set; }
}

public class AuthApprovalReport
{
    public DateTime PeriodFrom { get; set; }
    public DateTime PeriodTo { get; set; }
    public int TotalRequests { get; set; }
    public int ApprovedCount { get; set; }
    public int DeniedCount { get; set; }
    public int PendingCount { get; set; }
    public double ApprovalRate { get; set; }
    public double AvgDecisionDays { get; set; }
    public List<AuthByServiceType> ByServiceType { get; set; } = new();
}

// ---------------------------------------------------------------------------
// Work Queue
// ---------------------------------------------------------------------------

public interface IWorkQueueService
{
    Task<WorkQueueSummary> GetQueueSummaryAsync();
    Task<List<WorkQueueItem>> GetQueueItemsAsync(string? queueType = null,
        string? assignedTo = null, int limit = 100);
    Task AssignClaimAsync(string claimId, string assignTo);
    Task OverrideAsync(string claimId, string overrideReason);
}

public class WorkQueueSummary
{
    public int NcciEditFailures { get; set; }
    public int MissingAuth { get; set; }
    public int ProviderNotContracted { get; set; }
    public int CobRequired { get; set; }
    public int MedicalReview { get; set; }
}

public class WorkQueueItem
{
    public string ClaimId { get; set; } = string.Empty;
    public string MemberName { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public DateTime ServiceDate { get; set; }
    public string QueueReason { get; set; } = string.Empty;
    public string QueueReasonCode { get; set; } = string.Empty;
    public int DaysInQueue { get; set; }
    public string Priority { get; set; } = "Low";
    public string AssignedTo { get; set; } = string.Empty;
    public decimal TotalCharged { get; set; }
    public List<string> ProcedureCodes { get; set; } = new();
}

// ---------------------------------------------------------------------------
// Enrollment Operations
// ---------------------------------------------------------------------------

public interface IEnrollmentOperationsService
{
    Task<EnrollmentDailySummary> GetTodaySummaryAsync();
    Task<List<EnrollmentFile>> GetRecentFilesAsync(int days = 7);
    Task<EnrollmentFileDetail> GetFileDetailAsync(string fileId);
}

public class EnrollmentDailySummary
{
    public int FilesReceived { get; set; }
    public int TotalTransactions { get; set; }
    public int MembersAdded { get; set; }
    public int MembersTermed { get; set; }
    public int MembersChanged { get; set; }
    public int ErrorCount { get; set; }
}

public class EnrollmentFile
{
    public string FileId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public DateTime ReceivedTime { get; set; }
    public string SponsorName { get; set; } = string.Empty;
    public string GroupNumber { get; set; } = string.Empty;
    public int TransactionCount { get; set; }
    public int AddedCount { get; set; }
    public int TermedCount { get; set; }
    public int ChangedCount { get; set; }
    public int RejectedCount { get; set; }
    public string Status { get; set; } = string.Empty;
}

public class EnrollmentFileDetail
{
    public string FileId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public DateTime ReceivedTime { get; set; }
    public string SponsorName { get; set; } = string.Empty;
    public string GroupNumber { get; set; } = string.Empty;
    public int TransactionCount { get; set; }
    public int AddedCount { get; set; }
    public int TermedCount { get; set; }
    public int ChangedCount { get; set; }
    public int RejectedCount { get; set; }
    public string Status { get; set; } = string.Empty;
    public List<EnrollmentRejection> Rejections { get; set; } = new();
}

public class EnrollmentRejection
{
    public string MemberId { get; set; } = string.Empty;
    public string MemberName { get; set; } = string.Empty;
    public string ErrorCode { get; set; } = string.Empty;
    public string ErrorDescription { get; set; } = string.Empty;
    public string RawSegmentReference { get; set; } = string.Empty;
}

// ---------------------------------------------------------------------------
// Appeals
// ---------------------------------------------------------------------------

public interface IAppealsService
{
    Task<AppealsSummary> GetSummaryAsync();
    Task<List<AppealSummary>> SearchAppealsAsync(string? appealId = null,
        string? memberId = null, string? originalClaimId = null);
    Task<AppealDetails?> GetAppealByIdAsync(string appealId);
}

public class AppealsSummary
{
    public int OpenAppeals { get; set; }
    public int UrgentExpedited { get; set; }
    public int DueThisWeek { get; set; }
    public double OverturnedRate { get; set; }
}

public class AppealSummary
{
    public string AppealId { get; set; } = string.Empty;
    public string MemberName { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string AppealType { get; set; } = string.Empty;
    public string OriginalDecisionId { get; set; } = string.Empty;
    public string OriginalDecision { get; set; } = string.Empty;
    public string OriginalDenialReason { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool IsExpedited { get; set; }
    public DateTime FiledDate { get; set; }
    public DateTime DueDate { get; set; }
    public int DaysRemaining { get; set; }
    public string AssignedReviewer { get; set; } = string.Empty;
    public string ComplianceStatus { get; set; } = string.Empty;
}

public class AppealDetails
{
    public string AppealId { get; set; } = string.Empty;
    public string MemberName { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string AppealType { get; set; } = string.Empty;
    public string OriginalDecisionId { get; set; } = string.Empty;
    public string OriginalDecision { get; set; } = string.Empty;
    public string OriginalDenialReason { get; set; } = string.Empty;
    public string AppealReason { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool IsExpedited { get; set; }
    public DateTime FiledDate { get; set; }
    public DateTime DueDate { get; set; }
    public int DaysRemaining { get; set; }
    public string AssignedReviewer { get; set; } = string.Empty;
    public string ComplianceStatus { get; set; } = string.Empty;
    public string FinalDecision { get; set; } = string.Empty;
    public string FinalDecisionNotes { get; set; } = string.Empty;
    public DateTime? DecisionDate { get; set; }
    public List<AppealDocument> Documents { get; set; } = new();
    public List<AppealTimelineEvent> Timeline { get; set; } = new();
}

public class AppealDocument
{
    public string DocumentId { get; set; } = string.Empty;
    public string DocumentName { get; set; } = string.Empty;
    public string DocumentType { get; set; } = string.Empty;
    public DateTime UploadedDate { get; set; }
    public string UploadedBy { get; set; } = string.Empty;
}

public class AppealTimelineEvent
{
    public DateTime EventDate { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string PerformedBy { get; set; } = string.Empty;
}

// ---------------------------------------------------------------------------
// Correspondence
// ---------------------------------------------------------------------------

public interface ICorrespondenceService
{
    Task<CorrespondenceSummary> GetSummaryAsync();
    Task<List<CorrespondenceItem>> GetQueueAsync(string? type = null,
        string? status = null, int limit = 50);
    Task<List<RfaiTrackingItem>> GetOutstandingRfaisAsync();
}

public class CorrespondenceSummary
{
    public int PendingGeneration { get; set; }
    public int GeneratedToday { get; set; }
    public int SentThisWeek { get; set; }
    public int FailedReturned { get; set; }
}

public class CorrespondenceItem
{
    public string LetterId { get; set; } = string.Empty;
    public string LetterType { get; set; } = string.Empty;
    public string RecipientName { get; set; } = string.Empty;
    public string RecipientType { get; set; } = string.Empty;
    public string RelatedId { get; set; } = string.Empty;
    public DateTime? GeneratedDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public string DeliveryMethod { get; set; } = string.Empty;
}

public class RfaiTrackingItem
{
    public string RfaiId { get; set; } = string.Empty;
    public string RecipientName { get; set; } = string.Empty;
    public string RecipientType { get; set; } = string.Empty;
    public string RelatedClaimId { get; set; } = string.Empty;
    public string DocumentsRequested { get; set; } = string.Empty;
    public DateTime SentDate { get; set; }
    public DateTime ResponseDeadline { get; set; }
    public int DaysSinceSent { get; set; }
    public int DaysUntilDeadline { get; set; }
    public string Status { get; set; } = string.Empty;
}

public interface IPricingApiService
{
    Task<List<PricingApiKey>> GetApiKeysAsync();
    Task<PricingApiKey> CreateApiKeyAsync(string tenantName, string contactEmail, string tier);
    Task DeactivateApiKeyAsync(string apiKey);
    Task ResetUsageAsync();
    Task<List<PricingFeeScheduleInfo>> GetFeeSchedulesAsync();
    Task<FeeScheduleUploadResult> UploadFeeScheduleAsync(string type, int year, Stream csvStream, string fileName, decimal? baseRate = null);
    Task SeedDemoDataAsync();
}

public class PricingApiKey
{
    public string ApiKey { get; set; } = "";
    public string TenantName { get; set; } = "";
    public string ContactEmail { get; set; } = "";
    public string Tier { get; set; } = "";
    public int MonthlyLimit { get; set; }
    public int CurrentMonthUsage { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public bool IsActive { get; set; }
}

public class PricingFeeScheduleInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Version { get; set; } = "";
    public int CodeCount { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset LastUpdated { get; set; }
}

public class FeeScheduleUploadResult
{
    public string Message { get; set; } = "";
    public int CodeCount { get; set; }
    public string FeeScheduleId { get; set; } = "";
}

// ── Capitation Management ─────────────────────────────────────────────────

public interface ICapitationService
{
    // Contracts
    Task<List<CapitationContractSummary>> GetContractsAsync(string? npi = null, string? status = null, string? lob = null);
    Task<CapitationContractSummary?> GetContractByIdAsync(string id);
    Task<string> CreateContractAsync(CapitationContractSummary contract);
    Task UpdateContractAsync(string id, CapitationContractSummary contract);
    Task ActivateContractAsync(string id);
    Task TerminateContractAsync(string id, string reason, DateTime? terminationDate = null);

    // Runs
    Task<List<CapRunSummary>> GetRunsAsync(DateTime? from = null, DateTime? to = null);
    Task<CapRunSummary?> GetRunByIdAsync(string id);
    Task<string> CreateRunAsync(CreateCapRunRequest request);
    Task<CapRunSummary> ExecuteRunAsync(string id);
    Task CancelRunAsync(string id);

    // Statements
    Task<List<CapStatementSummary>> GetStatementsAsync(string? npi = null, DateTime? periodFrom = null, DateTime? periodTo = null, string? status = null);
    Task<CapStatementSummary?> GetStatementByIdAsync(string id);
    Task<List<CapStatementSummary>> GetStatementsByRunAsync(string runId);
    Task<List<CapStatementSummary>> GetUnpaidStatementsAsync();
    Task ApproveStatementAsync(string id);
    Task VoidStatementAsync(string id, string reason);
    Task HoldStatementAsync(string id, string reason);
    Task<CapitationPeriodSummaryDto> GetPeriodSummaryAsync(DateTime period);

    // Disbursements
    Task<string> InitiateDisbursementAsync(string statementId, string? initiatedBy = null);
    Task<CapDisbursementBatchResult> InitiateBatchDisbursementAsync(List<string> statementIds, string? initiatedBy = null);
}

public class CapitationContractSummary
{
    public string Id { get; set; } = string.Empty;
    public string ContractNumber { get; set; } = string.Empty;
    public string ProviderNPI { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public string ProviderType { get; set; } = "Individual";
    public string ContractType { get; set; } = "PrimaryCareOnly";
    public string LineOfBusiness { get; set; } = "Commercial";
    public List<string> PlanIds { get; set; } = new();
    public List<CapRateTier> RateTiers { get; set; } = new();
    public bool RiskAdjusted { get; set; }
    public decimal DefaultRiskScore { get; set; } = 1.0m;
    public decimal WithholdPercentage { get; set; }
    public decimal? IncentivePoolPercentage { get; set; }
    public decimal? StopLossThreshold { get; set; }
    public decimal? AggregateStopLoss { get; set; }
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
    public string Status { get; set; } = "Draft";
}

public class CapRateTier
{
    public string TierName { get; set; } = string.Empty;
    public int AgeFrom { get; set; }
    public int AgeTo { get; set; }
    public string? Gender { get; set; }
    public string? AgeSexCategory { get; set; }
    public decimal BasePMPM { get; set; }
    public string? ServiceCategory { get; set; }
}

public class CapRunSummary
{
    public string Id { get; set; } = string.Empty;
    public string RunNumber { get; set; } = string.Empty;
    public string RunType { get; set; } = "Monthly";
    public DateTime CapitationPeriod { get; set; }
    public string Status { get; set; } = "Pending";
    public string? LineOfBusiness { get; set; }
    public int TotalStatements { get; set; }
    public int TotalMemberMonths { get; set; }
    public decimal TotalGrossCapitation { get; set; }
    public decimal TotalWithholds { get; set; }
    public decimal TotalNetPayable { get; set; }
    public int TotalProviders { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ExecutionStartedAt { get; set; }
    public DateTime? ExecutionCompletedAt { get; set; }
    public double? ExecutionDurationSeconds { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

public class CreateCapRunRequest
{
    public string RunType { get; set; } = "Monthly";
    public DateTime CapitationPeriod { get; set; }
    public string? LineOfBusiness { get; set; }
    public string? ProviderNPI { get; set; }
    public string? ContractType { get; set; }
    public DateTime? OriginalPeriod { get; set; }
    public string? CreatedBy { get; set; }
    public string? Description { get; set; }
}

public class CapStatementSummary
{
    public string Id { get; set; } = string.Empty;
    public string StatementNumber { get; set; } = string.Empty;
    public string? CapitationRunId { get; set; }
    public string ContractId { get; set; } = string.Empty;
    public string ContractNumber { get; set; } = string.Empty;
    public string ProviderNPI { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public DateTime CapitationPeriodStart { get; set; }
    public DateTime CapitationPeriodEnd { get; set; }
    public string Status { get; set; } = "Generated";
    public int MemberMonths { get; set; }
    public decimal GrossCapitation { get; set; }
    public decimal WithholdAmount { get; set; }
    public decimal TotalAdjustments { get; set; }
    public decimal NetPayable { get; set; }
    public DateTime? PaymentDate { get; set; }
    public List<CapLineItem> LineItems { get; set; } = new();
    public List<CapAdjustment> Adjustments { get; set; } = new();
}

public class CapLineItem
{
    public string MemberId { get; set; } = string.Empty;
    public string MemberName { get; set; } = string.Empty;
    public string? PlanId { get; set; }
    public int MemberAge { get; set; }
    public string? Gender { get; set; }
    public decimal BasePMPM { get; set; }
    public decimal RiskScore { get; set; } = 1.0m;
    public decimal AdjustedPMPM { get; set; }
    public decimal ProrationFactor { get; set; } = 1.0m;
    public decimal GrossAmount { get; set; }
    public decimal WithholdAmount { get; set; }
    public decimal NetAmount { get; set; }
    public bool IsRetroactive { get; set; }
    public string? AdjustmentReason { get; set; }
}

public class CapAdjustment
{
    public string Type { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string? RelatedMemberId { get; set; }
    public DateTime AdjustmentDate { get; set; }
}

public class CapitationPeriodSummaryDto
{
    public DateTime Period { get; set; }
    public int TotalProviders { get; set; }
    public int TotalMemberMonths { get; set; }
    public decimal TotalGrossCapitation { get; set; }
    public decimal TotalWithholds { get; set; }
    public decimal TotalNetPayable { get; set; }
    public Dictionary<string, decimal> ByLineOfBusiness { get; set; } = new();
    public Dictionary<string, decimal> ByContractType { get; set; } = new();
}

public class CapDisbursementBatchResult
{
    public int TotalStatements { get; set; }
    public int DisbursementsInitiated { get; set; }
    public int Skipped { get; set; }
    public int Errors { get; set; }
    public decimal TotalAmount { get; set; }
    public List<string> ErrorMessages { get; set; } = new();
}
