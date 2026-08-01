using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace CloudHealthOffice.Portal.Services;

public interface IClaimsService
{
    Task<List<ClaimSummary>> GetRecentClaimsAsync(int count);
    Task<ClaimSearchResult> SearchClaimsAsync(ClaimSearchRequest request);
    Task<ClaimDetails?> GetClaimByIdAsync(string claimId);
    Task<string?> GetExplanationOfBenefitJsonAsync(string claimId);
    Task<List<MassAdjudicationRunSummary>> GetMassAdjudicationRunsAsync(int limit = 25);
    Task<MassAdjudicationRunSummary?> GetMassAdjudicationRunAsync(string runId);
    Task<List<MassAdjudicationClaimResult>> GetMassAdjudicationClaimResultsAsync(
        string runId,
        string? outcome = null,
        int limit = 250,
        string? validationStatus = null,
        string? paymentStatus = null);
    Task<string> SubmitClaimAsync(SubmitClaimRequest request);
    Task UpdateClaimStatusAsync(string claimId, string status, string? notes = null);
    Task<bool> TryRecordAiExaminerAgreementAsync(
        string claimId,
        string agreement,
        string examinerUserId,
        string? notes = null);
    Task<AdjudicationTransparencyData?> GetAdjudicationDataAsync(string claimId);

    /// <summary>
    /// Search the claims-service v1 endpoint for a member; returns FHIR
    /// ExplanationOfBenefit resources wrapped with pagination metadata. Used
    /// by the portal Member Details dialog Claims tab; the dialog only shows
    /// counts and links out — the full grid stays on /claims.
    /// </summary>
    Task<EobSearchResponse> SearchClaimsByMemberAsync(string memberId, MemberClaimsFilter filter);
}

/// <summary>
/// Admin-console read path for raw EDI import outcomes — 834 enrollment
/// batches (enrollment-import-service) and 837 claim imports
/// (claims-service). Distinct from <see cref="IMemberService.GetMember834TransactionsAsync"/>,
/// which is member-scoped and proxied through member-service; this is a
/// tenant-wide admin view, so it calls both owning services directly, same
/// as <see cref="IClaimsService.GetMassAdjudicationRunsAsync"/> does for
/// claims-service.
/// </summary>
public interface IEdiTransactionsService
{
    Task<List<Enrollment834Record>> GetEnrollment834TransactionsAsync(int limit = 100);
    Task<List<ClaimImportTransactionRecord>> GetClaimImportTransactionsAsync(int limit = 100);

    /// <summary>Batch-level 834 run summaries — one row per import, not per member. See <see cref="EnrollmentImportRunRecord"/>.</summary>
    Task<List<EnrollmentImportRunRecord>> GetEnrollmentImportRunsAsync(int limit = 100);
}

/// <summary>Portal projection of claims-service's ClaimImportTransaction.</summary>
public class ClaimImportTransactionRecord
{
    public string Id { get; set; } = string.Empty;
    public string ClaimNumber { get; set; } = string.Empty;
    public string? ClaimId { get; set; }
    public string MemberId { get; set; } = string.Empty;
    public string? FileName { get; set; }
    public DateTime ReceivedAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public List<string> Errors { get; set; } = new();
}

/// <summary>Portal projection of enrollment-import-service's EnrollmentImportRun — one row per 834 batch.</summary>
public class EnrollmentImportRunRecord
{
    public string Id { get; set; } = string.Empty;
    public string BatchId { get; set; } = string.Empty;
    public string? FileName { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int SuccessCount { get; set; }
    public int FailedCount { get; set; }
    public int SkippedCount { get; set; }
    public int MembersCreated { get; set; }
    public int MembersUpdated { get; set; }
    public int MembersTerminated { get; set; }
    public int DependentsCreated { get; set; }
    public int CoverageRecordsCreated { get; set; }
    public int CoverageMappingsUnresolved { get; set; }
    public List<string> Errors { get; set; } = new();
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

    /// <summary>
    /// Assign a PCP. Returns a populated <see cref="PcpAssignmentOutcome"/> — on
    /// validation failure (400), <see cref="PcpAssignmentOutcome.ValidationError"/>
    /// is set with the structured error code from coverage-service.
    /// </summary>
    Task<PcpAssignmentOutcome> AssignPcpAsync(AssignPcpRequest request);

    Task<List<PcpAssignmentHistoryItem>> GetMemberPcpHistoryAsync(string memberId);
    Task<List<CoverageHistoryEvent>> GetCoverageHistoryAsync(string memberId);
    Task<List<Enrollment834Record>> GetMember834TransactionsAsync(string memberId);
    Task<EnrollmentEventPage> GetEnrollmentEventsAsync(
        string memberId,
        EnrollmentEventFilter filter);
    Task TerminateEnrollmentAsync(TerminateEnrollmentRequest request);
    Task<MemberAccumulators> GetAccumulatorsAsync(string memberId);
}

public class MemberAccumulators
{
    public string MemberId { get; set; } = string.Empty;
    public DateTime PlanYearStart { get; set; }
    public DateTime PlanYearEnd { get; set; }

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

public class MassAdjudicationRunSummary
{
    public string Id { get; set; } = string.Empty;
    public string Status { get; set; } = "Completed";
    public MassAdjudicationRunMetadata Run { get; set; } = new();
    public int TotalClaims { get; set; }
    public int Processed { get; set; }
    public int Paid { get; set; }
    public int Pended { get; set; }
    public int BusinessDenials { get; set; }
    public int ObservationTimeouts { get; set; }
    public int PlatformFailures { get; set; }
    public int ServiceBusObservationTimeouts { get; set; }
    public int ServiceBusLateCompletions { get; set; }
    public int ServiceBusUnreconciledClaims { get; set; }
    public int WorkflowScenarios { get; set; }
    public int WorkflowMatches { get; set; }
    public int WorkflowMismatches { get; set; }
    public int WorkflowUnsupported { get; set; }
    public int WorkflowObservationTimeouts { get; set; }
    public TimeSpan Elapsed { get; set; }
    public double ThroughputClaimsPerSecond { get; set; }
    public double P95LatencyMilliseconds { get; set; }
    public double P99LatencyMilliseconds { get; set; }
    public MassAdjudicationStageTiming? SubmitTiming { get; set; }
    public MassAdjudicationStageTiming? AdjudicateTiming { get; set; }
    public MassAdjudicationStageTiming? WritebackTiming { get; set; }
    public List<MassAdjudicationStageTiming> AdjudicationStepTimings { get; set; } = new();
    public List<MassAdjudicationLifecycleTiming> LifecycleTimings { get; set; } = new();
    public MassAdjudicationFixturePreparation? FixturePreparation { get; set; }
    public decimal? AveragePaymentDelta { get; set; }
    public decimal PaymentTolerance { get; set; }
    public int PaymentComparisons { get; set; }
    public int PaymentMatches { get; set; }
    public int PaymentMismatches { get; set; }
    public decimal? MaximumPaymentDelta { get; set; }
    public List<MassAdjudicationPaymentDeltaBucket> PaymentDeltaDistribution { get; set; } = new();
    public List<MassAdjudicationBusinessDenialSummary> BusinessDenialBreakdown { get; set; } = new();
    public List<MassAdjudicationWorkflowScenarioSummary> WorkflowScenarioBreakdown { get; set; } = new();
    public List<MassAdjudicationFailureSummary> SampleFailures { get; set; } = new();
    public List<MassAdjudicationClaimResult> ClaimResults { get; set; } = new();
    public DateTime CreatedAtUtc { get; set; }
    public DateTime LastUpdatedAtUtc { get; set; }
    public MassAdjudicationRunProgress? Progress { get; set; }
}

public class MassAdjudicationRunMetadata
{
    public string TenantId { get; set; } = string.Empty;
    public int RequestedClaims { get; set; }
    public int Seed { get; set; }
    public int Parallelism { get; set; }
    public string ClaimsUrl { get; set; } = string.Empty;
    public string BenefitUrl { get; set; } = string.Empty;
    public string MemberUrl { get; set; } = string.Empty;
    public string CoverageUrl { get; set; } = string.Empty;
    public string ProviderUrl { get; set; } = string.Empty;
    public bool SeedMembers { get; set; }
    public bool SeedProviders { get; set; }
    public bool SkipClaimUpdate { get; set; }
    public int LineOfBusiness { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset CompletedAtUtc { get; set; }
}

public class MassAdjudicationRunProgress
{
    public string Phase { get; set; } = "Processing claims";
    public int RequestedClaims { get; set; }
    public int CompletedClaims { get; set; }
    public int ProcessedClaims { get; set; }
    public int PlatformFailures { get; set; }
    public double PercentComplete { get; set; }
    public double CurrentThroughputClaimsPerSecond { get; set; }
    public double RollingP95LatencyMilliseconds { get; set; }
    public double RollingP99LatencyMilliseconds { get; set; }
    public int PendingExpectedPendObservations { get; set; }
    public int PendingTerminalStatusObservations { get; set; }
    public int PendingWorkflowObservations { get; set; }
    public DateTimeOffset LastPublishedAtUtc { get; set; }
}

public class MassAdjudicationStageTiming
{
    public string Label { get; set; } = string.Empty;
    public double AverageMilliseconds { get; set; }
    public double P95Milliseconds { get; set; }
}

public class MassAdjudicationLifecycleTiming
{
    public string Label { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public double DurationMilliseconds { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset CompletedAtUtc { get; set; }
}

public class MassAdjudicationFixturePreparation
{
    public int GeneratedClaims { get; set; }
    public int ProviderPoolDistinctBefore { get; set; }
    public int ProviderPoolDistinctAfter { get; set; }
    public int ProviderPoolReusedAssignments { get; set; }
    public int ProviderPoolProtectedClaims { get; set; }
    public int MembersCreated { get; set; }
    public int MembersExisting { get; set; }
    public int MemberStatusesAligned { get; set; }
    public int CobCoverageCreated { get; set; }
    public int CobCoverageExisting { get; set; }
    public int ProviderNetworksCreated { get; set; }
    public int ProviderNetworksExisting { get; set; }
    public int ProvidersCreated { get; set; }
    public int ProvidersExisting { get; set; }
}

public class MassAdjudicationPaymentDeltaBucket
{
    public string Label { get; set; } = string.Empty;
    public decimal? LowerBoundExclusive { get; set; }
    public decimal? UpperBoundInclusive { get; set; }
    public int Count { get; set; }
}

public class MassAdjudicationBusinessDenialSummary
{
    public string Code { get; set; } = string.Empty;
    public int Count { get; set; }
}

public class MassAdjudicationWorkflowScenarioSummary
{
    public string Scenario { get; set; } = string.Empty;
    public int Total { get; set; }
    public int Matches { get; set; }
    public int Mismatches { get; set; }
    public int Unsupported { get; set; }
    public int ObservationTimeouts { get; set; }
    public int Unspecified { get; set; }
}

public class MassAdjudicationFailureSummary
{
    public string GeneratedClaimId { get; set; } = string.Empty;
    public string? Stage { get; set; }
    public string? Error { get; set; }
}

public class MassAdjudicationClaimResult
{
    public string Id { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string GeneratedClaimId { get; set; } = string.Empty;
    public string? SubmittedClaimId { get; set; }
    [JsonConverter(typeof(FlexibleClaimTypeJsonConverter))]
    public string ClaimType { get; set; } = string.Empty;
    public string? ValidationScenario { get; set; }
    public string? ExpectedOutcome { get; set; }
    public string? ExpectedBusinessDenialCode { get; set; }
    public string ValidationStatus { get; set; } = "Unspecified";
    public string Outcome { get; set; } = string.Empty;
    public bool AdjudicationSuccess { get; set; }
    public string? BusinessDenialCode { get; set; }
    public string? FailureStage { get; set; }
    public string? Error { get; set; }
    public decimal? ActualPlanPayment { get; set; }
    public decimal? ExpectedPlanPayment { get; set; }
    public decimal? PaymentDelta { get; set; }
    public double ElapsedMilliseconds { get; set; }
    public double SubmitMilliseconds { get; set; }
    public double AdjudicationMilliseconds { get; set; }
    public double WritebackMilliseconds { get; set; }
    public Dictionary<string, double> AdjudicationStepMilliseconds { get; set; } = new();
    public bool ServiceBusObservationTimedOut { get; set; }
    public bool ReconciledAfterObservationTimeout { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public class ServiceAccumulator
{
    public string BenefitCategory { get; set; } = string.Empty;
    public decimal Used { get; set; }
    public decimal Limit { get; set; }
    public string Unit { get; set; } = "USD";
}

public class AccumulatorActivity
{
    public string EventId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string? SourceReference { get; set; }
    public DateTime OccurredAt { get; set; }
    public decimal DeductibleDelta { get; set; }
    public decimal OopDelta { get; set; }
    public decimal FamilyDeductibleDelta { get; set; }
    public decimal FamilyOopDelta { get; set; }
    public string? Reason { get; set; }
    public string ActorId { get; set; } = "system";
}

public interface ICoverageService
{
    Task<List<Coverage>> GetCoverageByMemberIdAsync(string memberId);
}

public interface IMemberAlertService
{
    Task<List<MemberAlertView>> ListAsync(string memberId, bool activeOnly);
    Task<MemberAlertView?> CreateAsync(string memberId, CreateMemberAlertPayload payload);
    Task<MemberAlertView?> EndAsync(string memberId, string alertId);
}

public interface IMemberNoteService
{
    Task<MemberNotePage> ListAsync(string memberId, MemberNoteFilter filter);
    Task<MemberNoteView?> CreateAsync(string memberId, CreateMemberNotePayload payload);
}

public class MemberAlertView
{
    public string Id { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string AlertType { get; set; } = string.Empty;
    public string Severity { get; set; } = "Info";
    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? RequiredAction { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; }
    public string? EndedBy { get; set; }

    public bool IsActive(DateTime? asOf = null)
    {
        var t = asOf ?? DateTime.UtcNow;
        return StartDate <= t && (!EndDate.HasValue || EndDate.Value > t);
    }
}

public class CreateMemberAlertPayload
{
    public string AlertType { get; set; } = string.Empty;
    public string Severity { get; set; } = "Info";
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? RequiredAction { get; set; }
}

public class MemberNoteView
{
    public string Id { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string Category { get; set; } = "CustomerService";
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; }
    public string? LinkedResourceType { get; set; }
    public string? LinkedResourceId { get; set; }
}

public class CreateMemberNotePayload
{
    public string Category { get; set; } = "CustomerService";
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? Author { get; set; }
    public string? LinkedResourceType { get; set; }
    public string? LinkedResourceId { get; set; }
}

public sealed record MemberNoteFilter(string? Category, int Limit, string? ContinuationToken);

public class MemberNotePage
{
    public List<MemberNoteView> Items { get; set; } = new();
    public string? ContinuationToken { get; set; }
}

public interface IFamilyRelationshipService
{
    Task<List<FamilyRelationshipRow>> ListForMemberAsync(string memberId);
    Task<FamilyRelationshipRow?> AddDependentAsync(string subscriberMemberId, AddDependentPayload payload);
    Task EndRelationshipAsync(string memberId, string relationshipId, DateTime? endDate = null);
    Task<FamilyRelationshipRow?> UpdateRelationshipAsync(string memberId, string relationshipId, UpdateRelationshipPayload payload);
    Task SoftDeleteAsync(string memberId, string relationshipId, string reason);
}

public class FamilyRelationshipRow
{
    public string Id { get; set; } = string.Empty;
    public string SubjectMemberId { get; set; } = string.Empty;
    public string RelatedMemberId { get; set; } = string.Empty;
    public string RelationshipCode { get; set; } = string.Empty;
    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public bool IsCustodial { get; set; }
    public string? QmcsoReference { get; set; }
    public DateTime? DeletedAt { get; set; }
}

public class AddDependentPayload
{
    public AddDependentMember Member { get; set; } = new();
    public AddDependentRelationship Relationship { get; set; } = new();
}

public class AddDependentMember
{
    public string MemberId { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? MiddleName { get; set; }
    public DateTime DateOfBirth { get; set; }
    public string? Gender { get; set; }
    public string? SSN { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public DateTime EffectiveDate { get; set; }
}

public class AddDependentRelationship
{
    public string RelationshipCode { get; set; } = "19";
    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public bool IsCustodial { get; set; }
    public string? QmcsoReference { get; set; }
}

public class UpdateRelationshipPayload
{
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public bool? IsCustodial { get; set; }
    public string? QmcsoReference { get; set; }
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
    Task<ProviderNetworkInfo?> GetNetworkAsync(string networkId);
    Task<ProviderNetworkRoster?> GetNetworkRosterAsync(string networkId, DateTime asOfDate, int pageSize = 25);
    Task<ProviderNetworkMembership?> GetNetworkMembershipAsync(string networkId, string npi, DateTime asOfDate);

    /// <summary>
    /// Trigger an on-demand verification refresh for a single provider
    /// (capability 5.10). Wraps
    /// <c>POST /api/v1/providers/{id}/verification/refresh</c> on
    /// <c>provider-service</c>; the response carries the freshly
    /// projected integrity fields that the caller can splice back into
    /// the rendered detail view without round-tripping the full
    /// provider record.
    /// </summary>
    Task<ProviderIntegrityRefreshResult?> RefreshProviderVerificationAsync(string providerId);
}

/// <summary>
/// Subset of <c>IntegrityProjectionRefreshResult</c> that the portal
/// renders after an on-demand refresh (capability 5.10). The shape
/// mirrors the four cached projection fields on
/// <c>Provider.IntegrityScore</c> in <c>provider-service</c>.
/// </summary>
public class ProviderIntegrityRefreshResult
{
    public string ProviderId { get; set; } = string.Empty;
    public int? IntegrityScore { get; set; }
    public string? IntegrityRating { get; set; }
    public DateTimeOffset? LastVerifiedAt { get; set; }
    public DateTimeOffset? NextVerificationDue { get; set; }
}

public class ProviderNetworkInfo
{
    public string OrganizationId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string NetworkType { get; set; } = string.Empty;
    public string LineOfBusiness { get; set; } = string.Empty;
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public int VersionNumber { get; set; }
    public string VersionState { get; set; } = string.Empty;
}

public class ProviderNetworkRoster
{
    public List<ProviderNetworkRosterEntry> Items { get; set; } = new();
    public string? NextCursor { get; set; }
    public int PageSize { get; set; }
}

public class ProviderNetworkRosterEntry
{
    public string ProviderId { get; set; } = string.Empty;
    public ProviderNetworkRosterProvider Provider { get; set; } = new();
    public ProviderNetworkRosterParticipation Participation { get; set; } = new();
    public ProviderNetworkRosterIntegrity? IntegrityScore { get; set; }
}

public class ProviderNetworkRosterProvider
{
    public string Npi { get; set; } = string.Empty;
    public string ProviderType { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PrimarySpecialty { get; set; } = string.Empty;
    public string? City { get; set; }
    public string? State { get; set; }
    public bool AcceptingNewPatients { get; set; }
}

public class ProviderNetworkRosterParticipation
{
    public string? PlanId { get; set; }
    public string LineOfBusiness { get; set; } = string.Empty;
    public string NetworkTier { get; set; } = string.Empty;
    public bool AcceptingNewPatients { get; set; }
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
}

public class ProviderNetworkRosterIntegrity
{
    public int? Score { get; set; }
    public string? Rating { get; set; }
    public DateTimeOffset? LastVerifiedAt { get; set; }
}

public class ProviderNetworkMembership
{
    public string NetworkId { get; set; } = string.Empty;
    public string Npi { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public bool IsActiveMember { get; set; }
    public DateTime AsOfDate { get; set; }
    public DateTime? EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public string? ParticipationStatus { get; set; }
    public string? LineOfBusiness { get; set; }
    public string? NetworkTier { get; set; }
}

public interface IBenefitPlanService
{
    Task<List<BenefitPlan>> GetBenefitPlansAsync();
    Task<List<BenefitPlanListItem>> SearchBenefitPlansAsync(string? sponsorId = null, string? productType = null);
    Task<BenefitPlanDetails?> GetBenefitPlanByIdAsync(string planId);
    Task<string> CreateBenefitPlanAsync(CreateBenefitPlanRequest request);
    Task UpdateBenefitPlanAsync(string planId, UpdateBenefitPlanRequest request);
    Task AddBenefitAsync(string planId, UpsertPlanBenefitRequest request);
    Task UpdateBenefitAsync(string planId, string benefitId, UpsertPlanBenefitRequest request);
    Task ReplaceNetworkTiersAsync(string planId, IReadOnlyList<PlanNetworkTier> networkTiers);
    Task<List<BenefitItem>> GetAvailableBenefitsAsync();
    Task<List<ServiceBenefitRule>> GetServiceBenefitRulesAsync(string planId);
    Task UpdateServiceBenefitRulesAsync(UpdateServiceBenefitRulesRequest request);
    Task<AccumulatorConfiguration?> GetAccumulatorConfigAsync(string planId);
    Task UpdateAccumulatorConfigAsync(string planId, AccumulatorConfiguration config);

    /// <summary>
    /// Returns a categorized member-facing view of the plan as of the given
    /// service date. Null when the plan is not found (404) or the service
    /// is unreachable is surfaced as a <see cref="ServiceUnavailableException"/>.
    /// </summary>
    Task<MemberBenefitView?> GetMemberViewAsync(string planId, DateTime serviceDate);
}

public interface IBenefitPlanValidationService
{
    bool SyntheticClaimsEnabled { get; }
    Task<BenefitPlanValidationResult> ValidateAsync(
        BenefitPlanDetails plan,
        DateTime serviceDate,
        CancellationToken cancellationToken = default);
    Task<SyntheticClaimValidationResult> RunSynthetic837Async(
        BenefitPlanDetails plan,
        SyntheticClaimValidationRequest request,
        CancellationToken cancellationToken = default);
}

public class BenefitPlanValidationResult
{
    public DateTime ServiceDate { get; set; }
    public string PlanVersion { get; set; } = string.Empty;
    public MemberBenefitView? MemberView { get; set; }
    public List<BenefitPlanValidationCheck> Checks { get; set; } = new();
    public bool IsValid => Checks.All(check => check.Severity != "Error");
}

public class BenefitPlanValidationCheck
{
    public string Name { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Severity { get; set; } = "Success";
}

public class SyntheticClaimValidationRequest
{
    public DateTime ServiceDate { get; set; }
    public string ProviderNpi { get; set; } = "1999999992";
    public string ProcedureCode { get; set; } = "99213";
    public decimal ChargeAmount { get; set; } = 150m;
}

public class SyntheticClaimValidationResult
{
    public string ClaimId { get; set; } = string.Empty;
    public string ClaimNumber { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string ExpectedPlanId { get; set; } = string.Empty;
    public string? ResolvedPlanId { get; set; }
    public string PlanVersion { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? NetworkTier { get; set; }
    public decimal ChargeAmount { get; set; }
    public decimal AllowedAmount { get; set; }
    public decimal DeductibleAmount { get; set; }
    public decimal CopayAmount { get; set; }
    public decimal CoinsuranceAmount { get; set; }
    public decimal PatientResponsibility { get; set; }
    public decimal PaidAmount { get; set; }
    public string? OutcomeReason { get; set; }
    public TimeSpan Elapsed { get; set; }
    public bool ExactPlanMatched => string.Equals(ExpectedPlanId, ResolvedPlanId, StringComparison.Ordinal);
}

public class MemberBenefitView
{
    public string PlanId { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public string Payer { get; set; } = string.Empty;
    public string PlanType { get; set; } = string.Empty;
    public string? MetalLevel { get; set; }
    public string LineOfBusiness { get; set; } = string.Empty;
    public DateTime AsOfDate { get; set; }
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
    public string PlanVersion { get; set; } = string.Empty;
    public string FamilyAccumulatorModel { get; set; } = "Embedded";
    public MemberBenefitCostSharing CostSharing { get; set; } = new();
    public List<CategorizedBenefit> Categories { get; set; } = new();
    public List<PlanDocumentLink> Documents { get; set; } = new();
}

public class MemberBenefitCostSharing
{
    public decimal IndividualDeductible { get; set; }
    public decimal FamilyDeductible { get; set; }
    public decimal IndividualOutOfPocketMax { get; set; }
    public decimal FamilyOutOfPocketMax { get; set; }
    public decimal InNetworkDeductible { get; set; }
    public decimal OutOfNetworkDeductible { get; set; }
    public decimal InNetworkOutOfPocketMax { get; set; }
    public decimal OutOfNetworkOutOfPocketMax { get; set; }
}

public class CategorizedBenefit
{
    public string Category { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ServiceCategory { get; set; } = string.Empty;
    public string? Description { get; set; }
    public NetworkTierBenefit InNetwork { get; set; } = new();
    public NetworkTierBenefit? OutOfNetwork { get; set; }
    public bool DeductibleApplies { get; set; }
    public bool OopApplies { get; set; }
    public bool PriorAuthRequired { get; set; }
    public int? VisitLimit { get; set; }
    public string? VisitLimitPeriod { get; set; }
    public decimal? AnnualMaximum { get; set; }
    public decimal? LifetimeMaximum { get; set; }
    public string? Limitations { get; set; }
    public PharmacyDetail? Pharmacy { get; set; }
}

public class NetworkTierBenefit
{
    public string TierName { get; set; } = string.Empty;
    public decimal? Copay { get; set; }
    public decimal? Coinsurance { get; set; }
}

public class PharmacyDetail
{
    /// <summary>Verbatim plan ServiceCategory. Display this in the UI.</summary>
    public string? TierLabel { get; set; }

    /// <summary>Normalized bucket for analytics (Tier1/Tier2/.../Specialty). Do not display.</summary>
    public string? CanonicalTier { get; set; }

    public bool IsSpecialty { get; set; }
}

public class PlanDocumentLink
{
    public string DocType { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public long? Size { get; set; }
    public string? ContentHashSha256 { get; set; }
    public string? Version { get; set; }
    public DateTime? EffectiveDate { get; set; }
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

    /// <summary>
    /// Compact sponsor projection consumed by the portal Coverage tab's
    /// Sponsor sub-section in MemberDetailsDialog. Returns null on 404.
    /// </summary>
    Task<SponsorMemberView?> GetSponsorMemberViewAsync(string groupNumber);
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
    /// <summary>
    /// Get all tenant subscriptions where the given email appears in admin emails
    /// or user roster. Used for tenant switcher when home tenant ID doesn't match.
    /// </summary>
    Task<List<TenantSubscription>> GetTenantsForUserAsync(string userEmail);
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

public interface IIdCardService
{
    Task<IdCardOrderView> OrderAsync(string memberId, string? languageCode = null, string? requestedBy = null);
    Task<IdCardOrderView?> GetOrderAsync(string orderId);
    Task<List<IdCardHistoryView>> ListForMemberAsync(string memberId);
    string BuildDocumentDownloadUrl(string documentId);
    Task RevokeAsync(string cardId, string reason, string? notes = null);
}

public class IdCardOrderView
{
    public string OrderId { get; set; } = string.Empty;
    [JsonConverter(typeof(FlexibleClaimStatusJsonConverter))]
    public string Status { get; set; } = string.Empty;
    public string? CardId { get; set; }
    public string? DocumentId { get; set; }
    public string? PreviewDocumentId { get; set; }
    public string? FailureReason { get; set; }
    public string? FailureCode { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? IssuedAt { get; set; }
}

public class IdCardHistoryView
{
    public string CardId { get; set; } = string.Empty;
    public string OrderId { get; set; } = string.Empty;
    public string DocumentId { get; set; } = string.Empty;
    public string? PreviewDocumentId { get; set; }
    public string? PlanId { get; set; }
    public string? SponsorId { get; set; }
    public string? LanguageCode { get; set; }
    public DateTime IssuedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevocationReason { get; set; }
    public long ScanCount { get; set; }
}

// DTOs
public class ClaimSummary
{
    private string _claimId = string.Empty;

    public string ClaimId
    {
        get => _claimId;
        set => _claimId = string.IsNullOrWhiteSpace(value) ? _claimId : value;
    }

    [JsonPropertyName("id")]
    public string? Id
    {
        get => _claimId;
        set => _claimId = string.IsNullOrWhiteSpace(value) ? _claimId : value;
    }

    public string ClaimNumber { get; set; } = string.Empty;
    public string MemberName { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    [JsonConverter(typeof(FlexibleClaimTypeJsonConverter))]
    public string ClaimType { get; set; } = string.Empty; // Professional, Institutional, Dental
    [JsonConverter(typeof(FlexibleDecimalJsonConverter))]
    public decimal TotalChargeAmount { get; set; }
    [JsonConverter(typeof(FlexibleDecimalJsonConverter))]
    public decimal AllowedAmount { get; set; }
    [JsonConverter(typeof(FlexibleDecimalJsonConverter))]
    public decimal PaidAmount { get; set; }
    [JsonConverter(typeof(FlexibleClaimStatusJsonConverter))]
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
    private List<ClaimDiagnosisCode> _diagnosisCodes = new();
    private List<ClaimServiceLine> _serviceLines = new();
    private List<ClaimAudit> _auditTrail = new();

    public string SubscriberId { get; set; } = string.Empty;
    public string? BenefitPlanId { get; set; }
    public string? NetworkTier { get; set; }
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
    public DateTime? ReceivedDate { get; set; }
    public DateTime? PaidDate { get; set; }
    public string? CheckNumber { get; set; }
    public string? DenialReason { get; set; }
    public ClaimPendDetails? PendDetails { get; set; }
    public ClaimAiExamination? AiExamination { get; set; }
    [JsonPropertyName("adjudicationResult")]
    public ClaimAdjudicationProjection? AdjudicationResult
    {
        set
        {
            if (value is null)
            {
                return;
            }

            AllowedAmount = value.AllowedAmount;
            PaidAmount = value.PayerPayment;
            DeductibleAmount = value.DeductibleAmount;
            CoinsuranceAmount = value.CoinsuranceAmount;
            CopayAmount = value.CopayAmount;
            PatientResponsibility = value.PatientResponsibility;
            CheckNumber = value.CheckNumber;
            PaidDate = value.PaymentDate;
            DenialReason = value.DenialReason;
            NetworkTier = value.NetworkTier;
        }
    }
    public List<ClaimDiagnosisCode> DiagnosisCodes
    {
        get => _diagnosisCodes;
        set => _diagnosisCodes = value ?? new();
    }
    public List<ClaimServiceLine> ServiceLines
    {
        get => _serviceLines;
        set => _serviceLines = value ?? new();
    }
    [JsonPropertyName("claimLines")]
    public List<ClaimServiceLine>? ClaimLines
    {
        get => _serviceLines;
        set => _serviceLines = value ?? new();
    }
    public ClaimAdjustmentInfo? AdjustmentInfo { get; set; }
    public bool IsEditable { get; set; }
    public bool CanApprove { get; set; }
    public bool CanDeny { get; set; }
    public bool CanReverse { get; set; }
    public List<ClaimAudit> AuditTrail
    {
        get => _auditTrail;
        set => _auditTrail = value ?? new();
    }
}

public class ClaimPendDetails
{
    public string PendCode { get; set; } = string.Empty;
    public string PendReason { get; set; } = string.Empty;
    public DateTime PendedAt { get; set; }
    public List<ClaimPendEditFailure> EditFailures { get; set; } = new();
}

public class ClaimPendEditFailure
{
    public string EditType { get; set; } = string.Empty;
    public string RuleId { get; set; } = string.Empty;
    public string? Column1Code { get; set; }
    public string? Column2Code { get; set; }
    public List<int> AffectedLineNumbers { get; set; } = new();
    public bool ModifierOverridePresent { get; set; }
    public string? SuggestedCarc { get; set; }
    public string? SuggestedRarc { get; set; }
}

public class ClaimAiExamination
{
    public string RecommendedDisposition { get; set; } = string.Empty;
    public double ConfidenceScore { get; set; }
    public string? Rationale { get; set; }
    public List<string> PolicyCitations { get; set; } = new();
    public string? ModelId { get; set; }
    public string? PromptVersion { get; set; }
    public DateTime GeneratedAt { get; set; }
    public string? ExaminerAgreement { get; set; }
    public DateTime? ExaminerActedAt { get; set; }
    public string? ExaminerUserId { get; set; }
}

public class ClaimDiagnosisCode
{
    private string _codeQualifier = string.Empty;
    private string _type = string.Empty;

    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string CodeQualifier
    {
        get => _codeQualifier;
        set => _codeQualifier = value ?? string.Empty;
    }
    public string Type
    {
        get => !string.IsNullOrWhiteSpace(_type) ? _type : DiagnosisTypeLabel(CodeQualifier);
        set => _type = value ?? string.Empty;
    }
    public int PointerNumber { get; set; }

    private static string DiagnosisTypeLabel(string qualifier)
        => qualifier.Trim().ToUpperInvariant() switch
        {
            "ABK" => "Principal",
            "ABF" => "Secondary",
            "" => "Unspecified",
            _ => qualifier
        };
}

public class ClaimServiceLine
{
    private List<string> _modifiers = new();
    private List<int> _diagnosisPointers = new();
    private List<ClaimLineAdjustment> _adjustments = new();

    public int LineNumber { get; set; }
    public string ProcedureCode { get; set; } = string.Empty;
    public string ProcedureDescription { get; set; } = string.Empty;
    public List<string> Modifiers
    {
        get => _modifiers;
        set => _modifiers = value ?? new();
    }
    [JsonConverter(typeof(FlexibleDecimalJsonConverter))]
    public decimal Units { get; set; }
    [JsonConverter(typeof(FlexibleDecimalJsonConverter))]
    public decimal ChargeAmount { get; set; }
    [JsonConverter(typeof(FlexibleDecimalJsonConverter))]
    public decimal AllowedAmount { get; set; }
    [JsonConverter(typeof(FlexibleDecimalJsonConverter))]
    public decimal PaidAmount { get; set; }
    [JsonConverter(typeof(FlexibleDecimalJsonConverter))]
    public decimal PatientResponsibility { get; set; }
    public DateTime ServiceDateFrom { get; set; }
    public DateTime ServiceDateTo { get; set; }
    public string? RevenueCode { get; set; } // Institutional
    public List<int> DiagnosisPointers
    {
        get => _diagnosisPointers;
        set => _diagnosisPointers = value ?? new();
    }
    public List<ClaimLineAdjustment> Adjustments
    {
        get => _adjustments;
        set => _adjustments = value ?? new();
    }
    public string? LineStatus { get; set; }
    [JsonPropertyName("adjudicationResult")]
    public ClaimLineAdjudicationProjection? AdjudicationResult
    {
        set
        {
            if (value is null)
            {
                return;
            }

            AllowedAmount = value.AllowedAmount;
            PaidAmount = value.PaidAmount;
            PatientResponsibility = value.PatientResponsibility;
        }
    }
}

public class ClaimAdjudicationProjection
{
    public string? NetworkTier { get; set; }
    public decimal AllowedAmount { get; set; }
    public decimal DeductibleAmount { get; set; }
    public decimal CoinsuranceAmount { get; set; }
    public decimal CopayAmount { get; set; }
    public decimal PatientResponsibility { get; set; }
    public decimal PayerPayment { get; set; }
    public string? CheckNumber { get; set; }
    public DateTime? PaymentDate { get; set; }
    public string? DenialReason { get; set; }
}

public class ClaimLineAdjudicationProjection
{
    public decimal AllowedAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal PatientResponsibility { get; set; }
}

public class ClaimLineAdjustment
{
    public string GroupCode { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
    [JsonConverter(typeof(FlexibleDecimalJsonConverter))]
    public decimal Amount { get; set; }
    public string Description { get; set; } = string.Empty;
}

public class ClaimAdjustmentInfo
{
    public string AdjustmentType { get; set; } = string.Empty; // Reversal, Adjustment, Correction
    public string? OriginalClaimId { get; set; }
    public string? RelatedClaimId { get; set; }
    [JsonConverter(typeof(FlexibleDecimalJsonConverter))]
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
    public string? RunId { get; set; }
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
    public string Status { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayStatus => string.IsNullOrWhiteSpace(CoverageStatus) ? Status : CoverageStatus;
}

public class MemberDetails : MemberSummary
{
    public string Gender { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? ZipCode { get; set; }
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
    public string? Id { get; set; }
    public string? CoverageId { get; set; }
    public string? MemberId { get; set; }
    public string? PlanId { get; set; }
    public string? PlanName { get; set; }
    public string? GroupNumber { get; set; }
    public string? CoverageLevel { get; set; }
    public string? InsuranceLineCode { get; set; }
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
    [System.Text.Json.Serialization.JsonConverter(typeof(CoverageStatusValueConverter))]
    public int Status { get; set; }

    [System.Text.Json.Serialization.JsonConverter(typeof(CoverageLineOfBusinessValueConverter))]
    public int LineOfBusiness { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string StatusText => Status switch { 1 => "Active", 2 => "Pending", 3 => "Terminated", 4 => "Suspended", 5 => "COBRA", _ => "Unknown" };
}

public sealed class CoverageStatusValueConverter : System.Text.Json.Serialization.JsonConverter<int>
{
    public override int Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType == System.Text.Json.JsonTokenType.Number && reader.TryGetInt32(out var numericValue))
        {
            return numericValue;
        }

        if (reader.TokenType == System.Text.Json.JsonTokenType.String)
        {
            return reader.GetString()?.Trim().ToLowerInvariant() switch
            {
                "active" => 1,
                "pending" => 2,
                "terminated" => 3,
                "suspended" => 4,
                "cobra" => 5,
                _ => 0
            };
        }

        return 0;
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, int value, System.Text.Json.JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}

public sealed class CoverageLineOfBusinessValueConverter : System.Text.Json.Serialization.JsonConverter<int>
{
    public override int Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType == System.Text.Json.JsonTokenType.Number && reader.TryGetInt32(out var numericValue))
        {
            return numericValue;
        }

        if (reader.TokenType == System.Text.Json.JsonTokenType.String)
        {
            return reader.GetString()?.Trim().ToLowerInvariant() switch
            {
                "commercial" => 1,
                "medicare" => 2,
                "medicaid" => 3,
                "exchange" => 4,
                "tricare" => 5,
                "va" => 6,
                _ => 0
            };
        }

        return 0;
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, int value, System.Text.Json.JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}

public class AuthorizationSummary
{
    // Fields matching the Authorization API response
    public string Id { get; set; } = string.Empty;
    public string AuthorizationNumber { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string PatientFirstName { get; set; } = string.Empty;
    public string PatientLastName { get; set; } = string.Empty;
    public string RequestingProviderName { get; set; } = string.Empty;
    public string ServiceTypeCode { get; set; } = string.Empty;
    public int Status { get; set; }
    public DateTime SubmittedDate { get; set; }
    public DateTime? ReviewedDate { get; set; }

    // Computed display properties
    [System.Text.Json.Serialization.JsonIgnore]
    public string AuthorizationId => AuthorizationNumber;
    [System.Text.Json.Serialization.JsonIgnore]
    public string MemberName => $"{PatientFirstName} {PatientLastName}".Trim();
    [System.Text.Json.Serialization.JsonIgnore]
    public string ProviderName => RequestingProviderName;
    [System.Text.Json.Serialization.JsonIgnore]
    public string ServiceType => ServiceTypeCode;
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime RequestDate => SubmittedDate;
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime? DecisionDate => ReviewedDate;
    [System.Text.Json.Serialization.JsonIgnore]
    public string StatusText => Status switch
    {
        1 => "Submitted", 2 => "InReview", 3 => "Pended",
        4 => "Approved", 5 => "Modified", 6 => "Denied",
        7 => "Expired", 8 => "Cancelled", _ => "Unknown"
    };
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

    // Cached integrity projection (capability 5.10). Populated by
    // ProviderIntegrityProjectionService in provider-service; null
    // until the projection worker has produced a score for the
    // provider. Rendered by IntegrityBadge on the provider grid.
    public int? IntegrityScore { get; set; }
    public string? IntegrityRating { get; set; }
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

    // Detail-only integrity projection metadata (capability 5.10).
    // The provider list grid uses IntegrityScore + IntegrityRating
    // (inherited from ProviderListItem); the detail card additionally
    // surfaces verification timing for operator visibility.
    public DateTimeOffset? LastVerifiedAt { get; set; }
    public DateTimeOffset? NextVerificationDue { get; set; }
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

    // ── Panel-gating projection (capability 5.5) ───────────────────────
    // Surfaced read-only on the portal so operators can audit whether
    // each participation has been touched by panel-gating-aware code.
    // Authoring still happens via the provider-service API; future
    // capability adds inline edit capability.
    public string LineOfBusiness { get; set; } = string.Empty;
    public int? PanelLimit { get; set; }
    public bool? PanelAccepted { get; set; }
    public List<string> AcceptedLobs { get; set; } = new();
    public int? MinAcceptedAgeYears { get; set; }
    public int? MaxAcceptedAgeYears { get; set; }
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
    public decimal MonthlyPremium { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
}

public class BenefitPlanDetails : BenefitPlanListItem
{
    public string VersionId { get; set; } = string.Empty;
    public int VersionNumber { get; set; }
    public string VersionState { get; set; } = string.Empty;
    public string MetalTier { get; set; } = string.Empty; // Bronze, Silver, Gold, Platinum
    public decimal IndividualDeductible { get; set; }
    public decimal FamilyDeductible { get; set; }
    public decimal IndividualOOPMax { get; set; }
    public decimal FamilyOOPMax { get; set; }
    public decimal Coinsurance { get; set; }
    public string PlanYear { get; set; } = string.Empty;
    public List<PlanBenefit> Benefits { get; set; } = new();
    public List<PlanBenefit> Exclusions { get; set; } = new();
    public List<PlanNetworkTier> NetworkTiers { get; set; } = new();
}

public class PlanNetworkTier
{
    public string Id { get; set; } = string.Empty;
    public string TierName { get; set; } = string.Empty;
    public int TierLevel { get; set; } = 1;
    public string NetworkId { get; set; } = string.Empty;
}

public class PlanBenefit
{
    public string BenefitId { get; set; } = string.Empty;
    public string BenefitType { get; set; } = "medical";
    public string ServiceCategory { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsCovered { get; set; } = true;
    public string ServiceType { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public decimal? Copay { get; set; }
    public decimal? CoinsurancePercent { get; set; }
    public decimal? OutNetworkCopay { get; set; }
    public decimal? OutNetworkCoinsurancePercent { get; set; }
    public decimal? CoveragePercent { get; set; }
    public int? AnnualLimit { get; set; }
    public string? VisitLimitPeriod { get; set; }
    public bool DeductibleApplies { get; set; }
    public bool OopApplies { get; set; }
    public bool PriorAuthRequired { get; set; }
    public List<string> CptCodes { get; set; } = new();
}

public class UpsertPlanBenefitRequest
{
    public string BenefitType { get; set; } = "medical";
    public string ServiceCategory { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsCovered { get; set; } = true;
    public List<string> CptCodes { get; set; } = new();
    public decimal? InNetworkCopay { get; set; }
    public decimal? OutNetworkCopay { get; set; }
    public decimal? InNetworkCoinsurancePercent { get; set; }
    public decimal? OutNetworkCoinsurancePercent { get; set; }
    public bool DeductibleApplies { get; set; } = true;
    public bool OopApplies { get; set; } = true;
    public bool PriorAuthRequired { get; set; }
    public int? VisitLimit { get; set; }
    public string? VisitLimitPeriod { get; set; }
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
    // Coverage status (EB*1 / AAA)
    public bool IsCovered { get; set; }
    public string? StatusCode { get; set; }  // EB06: 1=Active, 6=Inactive
    public string? RejectionReason { get; set; }  // AAA03-4 / MSG

    // Plan info (EB / REF / DTP)
    public string InsurancePlanName { get; set; } = string.Empty;
    public string GroupNumber { get; set; } = string.Empty;  // REF*1L
    public string CoverageLevel { get; set; } = string.Empty;  // EB03: EMP/FAM/IND
    public string? InsuranceType { get; set; }  // EB02: HLT/DEN/VIS
    public string? LineOfBusiness { get; set; }  // Commercial/Medicare/Medicaid
    public DateTime? CoverageBeginDate { get; set; }  // DTP*348
    public DateTime? CoverageEndDate { get; set; }  // DTP*349

    // Deductible & OOP (EB*C / EB*G / EB*D)
    public DeductibleInfo? Deductible { get; set; }
    public OutOfPocketInfo? OutOfPocket { get; set; }

    // Benefits (EB segments per service type)
    public List<Benefit>? Benefits { get; set; }

    // COB / Other Insurance (SB/OI loops)
    public List<AdditionalInsuranceInfo>? AdditionalInsurances { get; set; }
}

public class DeductibleInfo
{
    [JsonPropertyName("individualDeductible")]
    public decimal IndividualAmount { get; set; }  // EB*C*30*HLT*IND

    [JsonPropertyName("individualDeductibleMet")]
    public decimal IndividualMet { get; set; }  // EB*C accumulated

    [JsonPropertyName("individualDeductibleRemaining")]
    public decimal IndividualRemaining { get; set; }  // EB*D remaining

    [JsonPropertyName("familyDeductible")]
    public decimal FamilyAmount { get; set; }  // EB*C*30*HLT*FAM

    [JsonPropertyName("familyDeductibleMet")]
    public decimal FamilyMet { get; set; }

    [JsonPropertyName("familyDeductibleRemaining")]
    public decimal FamilyRemaining { get; set; }

    public string TimePeriod { get; set; } = "Calendar Year";  // EB06: 29=Year
}

public class OutOfPocketInfo
{
    [JsonPropertyName("individualOOPMax")]
    public decimal IndividualAmount { get; set; }  // EB*G*30*HLT*IND

    [JsonPropertyName("individualOOPMet")]
    public decimal IndividualMet { get; set; }

    [JsonPropertyName("individualOOPRemaining")]
    public decimal IndividualRemaining { get; set; }

    [JsonPropertyName("familyOOPMax")]
    public decimal FamilyAmount { get; set; }  // EB*G*30*HLT*FAM

    [JsonPropertyName("familyOOPMet")]
    public decimal FamilyMet { get; set; }

    [JsonPropertyName("familyOOPRemaining")]
    public decimal FamilyRemaining { get; set; }

    public string TimePeriod { get; set; } = "Calendar Year";
}

public class Benefit
{
    public string ServiceTypeName { get; set; } = string.Empty;  // EB01 description
    public string? ServiceTypeCode { get; set; }  // EB01: 30, 33, 42, etc.
    public decimal? MonetaryAmount { get; set; }  // EB07: Copay amount
    public decimal? Percentage { get; set; }  // EB08: Coinsurance %
    public decimal? Quantity { get; set; }  // EB10: Visit/unit limit
    public string? QuantityQualifier { get; set; }  // EB09: VS=Visits, DA=Days
    public string? TimePeriod { get; set; }  // EB06: 26=Visit, 29=Year
    public string? NetworkIndicator { get; set; }  // EB12: Y=In, N=Out
    public bool AuthorizationRequired { get; set; }  // MSG segment
    public DateTime? BenefitBeginDate { get; set; }
    public DateTime? BenefitEndDate { get; set; }
}

public class AdditionalInsuranceInfo
{
    public string PayerName { get; set; } = string.Empty;
    public string? PayerId { get; set; }
    public string CoverageSequence { get; set; } = string.Empty;  // P/S/T
    public string? GroupNumber { get; set; }
    public DateTime? CoverageBeginDate { get; set; }
    public DateTime? CoverageEndDate { get; set; }
    public bool IsMedicare { get; set; }
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

[BsonIgnoreExtraElements]
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

    /// <summary>
    /// Member-scoped premium rollup consumed by the portal Member Details
    /// dialog (Premium tab). Returns null if the member has no invoices.
    /// </summary>
    Task<MemberPremiumSummary?> GetMemberPremiumSummaryAsync(string memberId);
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

/// <summary>
/// Portal projection of enrollment-import-service's <c>EnrollmentEvent</c>. Surfaced via
/// the member-service proxy at GET /members/{id}/enrollment-events so consent /
/// audit / tenant filtering happens on the same boundary as every other member read.
/// </summary>
public class EnrollmentEvent
{
    public string EventId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public int Version { get; set; }
    public DateTime OccurredAt { get; set; }
    public DateTime? EventDate { get; set; }
    public DateTime? RetroEffectiveDate { get; set; }
    public string? SourceBatchId { get; set; }
    public string? TransactionId { get; set; }
    public string? MaintenanceType { get; set; }
    public string? MaintenanceReason { get; set; }
    public string? Source { get; set; }

    /// <summary>Raw JSON payload (changed fields, plan ids, etc.). Rendered as-is for now.</summary>
    public System.Text.Json.JsonElement? Payload { get; set; }

    /// <summary>Raw 834 (or manual JSON) snippet captured at write time, for audit display.</summary>
    public string? RawSegment { get; set; }
}

public class EnrollmentEventPage
{
    public List<EnrollmentEvent> Items { get; set; } = new();
    public string? ContinuationToken { get; set; }
}

public sealed record EnrollmentEventFilter(
    string? Type = null,
    DateTime? From = null,
    DateTime? To = null,
    int Limit = 50,
    string? ContinuationToken = null);

public class AssignPcpRequest
{
    public string MemberId { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string? ProviderNpi { get; set; }
    public DateTime EffectiveDate { get; set; }
    public string? Reason { get; set; }

    /// <summary>MemberChoice (default), AutoAssigned, or AdminAssigned.</summary>
    public string? AssignmentSource { get; set; }
}

public class PcpAssignmentOutcome
{
    public MemberPcp? Pcp { get; set; }
    public PcpValidationProblem? ValidationError { get; set; }
    public bool IsSuccess => Pcp != null;
}

/// <summary>
/// Mirror of coverage-service's <c>PcpValidationError</c>. <see cref="Code"/>
/// values are stable — see docs/architecture/pcp-assignment.md "Validation
/// ladder" for the canonical list.
/// </summary>
public class PcpValidationProblem
{
    public string Code { get; set; } = string.Empty;
    public string Field { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Severity { get; set; } = "Error";
}

public class PcpAssignmentHistoryItem
{
    public string Id { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string CoverageId { get; set; } = string.Empty;
    public string ProviderNpi { get; set; } = string.Empty;
    public string? ProviderId { get; set; }
    public string? ProviderName { get; set; }
    public DateTime EffectiveDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string? AssignmentReason { get; set; }
    public string AssignmentSource { get; set; } = "MemberChoice";
    public string NetworkStatusAtAssignment { get; set; } = "Unknown";
    public string? AssignedBy { get; set; }
    public DateTime CreatedDate { get; set; }
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
    public string LineOfBusiness { get; set; } = string.Empty;
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
    [Required]
    public string LineOfBusiness { get; set; } = string.Empty;
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
    Task ResolvePendedClaimAsync(
        string claimId,
        string disposition,
        string reason,
        string? aiExaminerAgreement,
        string examinerUserId);
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
    public string? AiRecommendedDisposition { get; set; }
    public double? AiConfidenceScore { get; set; }
    public string? AiRationale { get; set; }
    public List<string> AiPolicyCitations { get; set; } = new();
    public string? AiExaminerAgreement { get; set; }
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
    Task<List<CapRunSummary>> GetRunsAsync(DateTime? from = null, DateTime? to = null, string? lineOfBusiness = null);
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

public class CapitationRateConfigSummary
{
    public string Id { get; set; } = string.Empty;
    public string RateConfigNumber { get; set; } = string.Empty;
    public string ContractId { get; set; } = string.Empty;
    public string ContractNumber { get; set; } = string.Empty;
    public string ProviderNPI { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public string ProviderType { get; set; } = "Individual";
    public string ContractType { get; set; } = "PrimaryCareOnly";
    public string LineOfBusiness { get; set; } = "Commercial";
    public DateTime? LastDenormSyncAt { get; set; }
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

/// <summary>Legacy alias — use CapitationRateConfigSummary going forward</summary>
public class CapitationContractSummary : CapitationRateConfigSummary { }

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
    public string? Description { get; set; }
    public CapRunCriteriaSummary? Criteria { get; set; }
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

public class CapRunCriteriaSummary
{
    public string? LineOfBusiness { get; set; }
    public string? ProviderNPI { get; set; }
    public string? ContractType { get; set; }
    public DateTime? OriginalPeriod { get; set; }
}

public class CreateCapRunRequest
{
    public string RunType { get; set; } = "Monthly";
    public DateTime CapitationPeriod { get; set; }
    public CreateCapRunCriteria Criteria { get; set; } = new();
    public string? CreatedBy { get; set; }
    public string? Description { get; set; }
}

public class CreateCapRunCriteria
{
    public string LineOfBusiness { get; set; } = "Commercial";
    public string? ProviderNPI { get; set; }
    public string? ContractType { get; set; }
    public DateTime? OriginalPeriod { get; set; }
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

// ── Terminology Service ─────────────────────────────────────────────────────

public interface ITerminologyService
{
    Task<TermTranslateResult> TranslateAsync(string system, string code, string targetSystem,
        string? tenantId = null, int? age = null, string? gender = null, string? state = null);
    Task<List<TermMapVersionSummary>> GetMapVersionsAsync();
    Task<TermHealthStatus> GetHealthAsync();
}

public class TermTranslateResult
{
    public bool Result { get; set; }
    public string? Message { get; set; }
    public List<TermTranslateMatch> Matches { get; set; } = new();
    public string? MapVersionId { get; set; }
    public DateTime TranslatedAt { get; set; }
}

public class TermTranslateMatch
{
    public string Equivalence { get; set; } = string.Empty;
    public TermCoding Concept { get; set; } = new();
    public bool IsContextResolved { get; set; }
    public bool IsOverride { get; set; }
    public string Source { get; set; } = string.Empty;
}

public class TermCoding
{
    public string System { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Display { get; set; } = string.Empty;
}

public class TermMapVersionSummary
{
    public string Id { get; set; } = string.Empty;
    public string MapName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string SourceSystem { get; set; } = string.Empty;
    public string TargetSystem { get; set; } = string.Empty;
    public DateTime ImportedAt { get; set; }
    public bool IsActive { get; set; }
    public int EntryCount { get; set; }
    public string? SourceChecksum { get; set; }
}

public class TermHealthStatus
{
    public string Status { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public int TotalActiveEntries { get; set; }
    public List<TermMapVersionSummary> ActiveMaps { get; set; } = new();
    public DateTime Timestamp { get; set; }
}

// ── Provider Contracts ──────────────────────────────────────────────────────

public interface IProviderContractsService
{
    Task<List<ProviderContractSummary>> GetContractsAsync(
        string? npi = null, string? lob = null,
        string? status = null, string? paymentMethodology = null,
        string? networkStatus = null);
    Task<ProviderContractSummary?> GetContractByIdAsync(string id);
    Task<ProviderContractSummary?> GetContractByNumberAsync(string number);
    Task<string> CreateContractAsync(ProviderContractSummary contract);
    Task UpdateContractAsync(string id, ProviderContractSummary contract);
    Task ActivateContractAsync(string id);
    Task SuspendContractAsync(string id, string reason);
    Task TerminateContractAsync(string id, string reason, DateTime? terminationDate = null);
    Task ReinstateContractAsync(string id);
    Task AddAmendmentAsync(string id, ContractAmendmentSummary amendment);
    Task SyncChildrenAsync(string id);
    Task<List<string>> GetRateConfigIdsAsync(string id);
}

public class ProviderContractSummary
{
    public string Id { get; set; } = string.Empty;
    public string ContractNumber { get; set; } = string.Empty;
    public string ProviderNPI { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public string? ProviderTin { get; set; }
    public string ProviderType { get; set; } = "Individual";
    public string LineOfBusiness { get; set; } = "Commercial";
    public List<string> PlanIds { get; set; } = new();
    public string PaymentMethodology { get; set; } = "FullCapitation";
    public string NetworkStatus { get; set; } = "Participating";
    public string? ContractOwner { get; set; }
    public string? SignatoryName { get; set; }
    public DateTime? SignedDate { get; set; }
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
    public string? TerminationReason { get; set; }
    public bool AutoRenews { get; set; }
    public int? RenewalTermMonths { get; set; }
    public int? NoticeRequiredDays { get; set; }
    public List<ContractAmendmentSummary> Amendments { get; set; } = new();
    public string Status { get; set; } = "Draft";
    public List<string> CapitationRateConfigIds { get; set; } = new();
    public List<string> FfsRateConfigIds { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime LastUpdatedAt { get; set; }
}

public class ContractAmendmentSummary
{
    public string Id { get; set; } = string.Empty;
    public DateTime EffectiveDate { get; set; }
    public string AmendmentType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? ApprovedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

// ── AR Service ──────────────────────────────────────────────────────────────

public interface IArService
{
    // GL Accounts
    Task<List<GlAccountSummary>> GetAccountsAsync(string? accountType = null, string? lob = null, string? status = null);
    Task<GlAccountSummary?> GetAccountByIdAsync(string id);
    Task<string> CreateAccountAsync(GlAccountSummary account);
    Task UpdateAccountAsync(string id, GlAccountSummary account);
    Task ActivateAccountAsync(string id);
    Task DeactivateAccountAsync(string id);

    // Balances
    Task<List<ArBalanceSummary>> GetBalancesAsync(string? accountId = null, DateTime? period = null, bool? isReconciled = null);
    Task<ArBalanceSummary?> GetBalanceByIdAsync(string id);
    Task<List<ArBalanceSummary>> GetBalancesByAccountAsync(string accountId);
    Task<ArAgingSummary> GetAgingSummaryAsync();
    Task ReconcileBalanceAsync(string id);

    // Cash Posting
    Task<List<CashPostingSummary>> GetCashPostingsAsync(string? payerType = null, string? status = null, DateTime? dateFrom = null, DateTime? dateTo = null);
    Task<CashPostingSummary?> GetCashPostingByIdAsync(string id);
    Task<string> CreateCashPostingAsync(CashPostingSummary posting);
    Task ApplyCashPostingAsync(string id);
    Task VoidCashPostingAsync(string id);

    // Adjustments
    Task<List<ArAdjustmentSummary>> GetAdjustmentsAsync(string? type = null, string? status = null, DateTime? period = null, string? accountId = null);
    Task<ArAdjustmentSummary?> GetAdjustmentByIdAsync(string id);
    Task<string> CreateAdjustmentAsync(ArAdjustmentSummary adjustment);
    Task ApproveAdjustmentAsync(string id);
    Task RejectAdjustmentAsync(string id, string reason);
    Task PostAdjustmentAsync(string id);
    Task ReverseAdjustmentAsync(string id);

    // Batch Rules
    Task<List<ArBatchRuleSummary>> GetBatchRulesAsync(string? trigger = null, string? status = null);
    Task<ArBatchRuleSummary?> GetBatchRuleByIdAsync(string id);
    Task<string> CreateBatchRuleAsync(ArBatchRuleSummary rule);
    Task UpdateBatchRuleAsync(string id, ArBatchRuleSummary rule);
    Task<ArBatchRuleTestResult> TestBatchRuleAsync(string id, decimal sampleAmount);

    /// <summary>
    /// Member-scoped AR rollup consumed by the portal Member Details dialog
    /// (AR tab). Read-only; no payments initiated from this surface.
    /// </summary>
    Task<MemberArSummary?> GetMemberArSummaryAsync(string memberId);
}

// ── Member-Linkage DTOs (consumed by MemberDetailsDialog) ────────────────────

/// <summary>
/// Filter the Claims tab can apply when calling
/// <see cref="IClaimsService.SearchClaimsByMemberAsync"/>. Mirrors the
/// claims-service v1 query string so it serializes 1:1 to the wire.
/// </summary>
public class MemberClaimsFilter
{
    public DateTime? ServiceDateFrom { get; set; }
    public DateTime? ServiceDateTo { get; set; }
    /// <summary>One of the ClaimStatus enum names (e.g., "Approved").</summary>
    public string? Status { get; set; }
    public string? ProviderNPI { get; set; }
    /// <summary>One of the ClaimType enum names (e.g., "Professional").</summary>
    public string? ClaimType { get; set; }
    public decimal? AmountMin { get; set; }
    public decimal? AmountMax { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

/// <summary>
/// Wire shape returned by claims-service <c>/api/v1/claims</c> — the
/// resources array is FHIR ExplanationOfBenefit JSON and stays opaque to the
/// portal (which only renders count + drill-out link).
/// </summary>
public class EobSearchResponse
{
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    /// <summary>FHIR ExplanationOfBenefit resources, untyped on this side.</summary>
    public List<System.Text.Json.JsonElement> Resources { get; set; } = new();
}

public class MemberArSummary
{
    public string MemberId { get; set; } = string.Empty;
    public decimal CurrentBalance { get; set; }
    public AgedBuckets Aged { get; set; } = new();
    public List<ArChargeRow> RecentCharges { get; set; } = new();
    public List<ArPaymentRow> RecentPayments { get; set; } = new();
    public DateTime AsOfUtc { get; set; }
}

public class AgedBuckets
{
    public decimal Bucket0_30 { get; set; }
    public decimal Bucket31_60 { get; set; }
    public decimal Bucket61_90 { get; set; }
    public decimal Bucket91Plus { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public decimal Total => Bucket0_30 + Bucket31_60 + Bucket61_90 + Bucket91Plus;
}

public class ArChargeRow
{
    public string EntryId { get; set; } = string.Empty;
    public DateTime PostedAt { get; set; }
    public decimal Amount { get; set; }
    /// <summary>ar-service ArPostingSource enum name.</summary>
    public string Source { get; set; } = string.Empty;
    public string? SourceReferenceNumber { get; set; }
    public string? Memo { get; set; }
}

public class ArPaymentRow
{
    public string EntryId { get; set; } = string.Empty;
    public DateTime PostedAt { get; set; }
    public decimal Amount { get; set; }
    public string Source { get; set; } = string.Empty;
    public string? SourceReferenceNumber { get; set; }
    public string? Memo { get; set; }
}

public class MemberPremiumSummary
{
    public string MemberId { get; set; } = string.Empty;
    public PremiumInvoiceView? CurrentInvoice { get; set; }
    public DateTime? NextBillDate { get; set; }
    public bool AutopayEnabled { get; set; }
    public GracePeriodState Grace { get; set; } = new();
    public List<PremiumInvoiceView> Last12 { get; set; } = new();
}

public class PremiumInvoiceView
{
    public string Id { get; set; } = string.Empty;
    public string InvoiceNumber { get; set; } = string.Empty;
    public string GroupNumber { get; set; } = string.Empty;
    public string SponsorName { get; set; } = string.Empty;
    public DateTime BillingPeriodStart { get; set; }
    public DateTime BillingPeriodEnd { get; set; }
    public DateTime DueDate { get; set; }
    /// <summary>InvoiceStatus enum name.</summary>
    public string Status { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public decimal TotalPaid { get; set; }
    public decimal BalanceDue { get; set; }
    public bool IsAptcSubsidized { get; set; }
    public decimal AptcMonthlyAmount { get; set; }
    /// <summary>"Standard" or "AptcThreeMonth".</summary>
    public string GraceType { get; set; } = "Standard";
}

public class GracePeriodState
{
    public bool IsInGrace { get; set; }
    /// <summary>"Standard" or "AptcThreeMonth".</summary>
    public string GraceType { get; set; } = "Standard";
    public int DaysRemaining { get; set; }
    public DateTime? ExpiresOn { get; set; }
}

public class SponsorMemberView
{
    public string GroupNumber { get; set; } = string.Empty;
    public string SponsorName { get; set; } = string.Empty;
    /// <summary>LineOfBusiness enum name.</summary>
    public string LineOfBusiness { get; set; } = string.Empty;
    /// <summary>SponsorStatus enum name.</summary>
    public string Status { get; set; } = string.Empty;
    public ContactCard? PrimaryContact { get; set; }
    public BrokerCard? Broker { get; set; }
    public OpenEnrollmentCard? OpenEnrollment { get; set; }
}

public class ContactCard
{
    public string? Name { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
}

public class BrokerCard
{
    public string? AgencyName { get; set; }
    public string? Name { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Npn { get; set; }
}

public class OpenEnrollmentCard
{
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    /// <summary>"Upcoming" / "Open" / "Closed" — server-computed.</summary>
    public string Status { get; set; } = "Closed";
}

// ── AR DTOs ─────────────────────────────────────────────────────────────────

public class GlAccountSummary
{
    public string Id { get; set; } = string.Empty;
    public string AccountNumber { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string AccountType { get; set; } = "Asset";
    public string NormalBalance { get; set; } = "Debit";
    public string? SubType { get; set; }
    public string? StatementSection { get; set; }
    public string Segments { get; set; } = string.Empty;
    public GlSegmentCodesSummary SegmentCodes { get; set; } = new();
    public List<string> LineOfBusinessMapping { get; set; } = new();
    public PremiumSplitSummary? PremiumSplit { get; set; }
    public bool IsReconciliationAccount { get; set; }
    public string? ReconciliationPairAccountId { get; set; }
    public bool IsIntercompany { get; set; }
    public string? IntercompanyEntityCode { get; set; }
    public List<string> BatchRuleIds { get; set; } = new();
    public string Status { get; set; } = "Active";
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
}

public class GlSegmentCodesSummary
{
    public string Company { get; set; } = string.Empty;
    public string Fund { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string Program { get; set; } = string.Empty;
    public string Account { get; set; } = string.Empty;
    public string SubAccount { get; set; } = string.Empty;
}

public class PremiumSplitSummary
{
    public decimal SponsorPercentage { get; set; }
    public decimal MemberPercentage { get; set; }
    public bool IsPlanSpecific { get; set; }
}

public class ArBalanceSummary
{
    public string Id { get; set; } = string.Empty;
    public string GlAccountId { get; set; } = string.Empty;
    public string AccountNumber { get; set; } = string.Empty;
    public DateTime Period { get; set; }
    public decimal OpeningBalance { get; set; }
    public decimal TotalDebits { get; set; }
    public decimal TotalCredits { get; set; }
    public decimal ClosingBalance { get; set; }
    public decimal SponsorBalance { get; set; }
    public decimal MemberBalance { get; set; }
    public decimal Current { get; set; }
    public decimal Days31To60 { get; set; }
    public decimal Days61To90 { get; set; }
    public decimal Days91To120 { get; set; }
    public decimal Over120Days { get; set; }
    public bool IsReconciled { get; set; }
    public DateTime? ReconciledAt { get; set; }
}

public class ArAgingSummary
{
    public decimal Current { get; set; }
    public decimal Days31To60 { get; set; }
    public decimal Days61To90 { get; set; }
    public decimal Days91To120 { get; set; }
    public decimal Over120Days { get; set; }
    public decimal TotalOutstanding { get; set; }
}

public class CashPostingSummary
{
    public string Id { get; set; } = string.Empty;
    public string PostingNumber { get; set; } = string.Empty;
    public DateTime ReceiptDate { get; set; }
    public decimal Amount { get; set; }
    public string PaymentMethod { get; set; } = "Check";
    public string? CheckNumber { get; set; }
    public string? BankReference { get; set; }
    public string PayerType { get; set; } = "Sponsor";
    public string PayerReferenceId { get; set; } = string.Empty;
    public string? PayerName { get; set; }
    public decimal AppliedAmount { get; set; }
    public decimal UnappliedAmount { get; set; }
    public string Status { get; set; } = "Pending";
}

public class ArAdjustmentSummary
{
    public string Id { get; set; } = string.Empty;
    public string AdjustmentNumber { get; set; } = string.Empty;
    public string AdjustmentType { get; set; } = "ManualCorrection";
    public string GlAccountId { get; set; } = string.Empty;
    public string ArBalanceId { get; set; } = string.Empty;
    public DateTime Period { get; set; }
    public decimal Amount { get; set; }
    public string Direction { get; set; } = "Debit";
    public string ReasonCode { get; set; } = string.Empty;
    public string? Narrative { get; set; }
    public string? AuthorizedBy { get; set; }
    public DateTime? AuthorizedAt { get; set; }
    public string? SourceType { get; set; }
    public string? SourceReferenceId { get; set; }
    public string Status { get; set; } = "Pending";
}

public class ArBatchRuleSummary
{
    public string Id { get; set; } = string.Empty;
    public string RuleCode { get; set; } = string.Empty;
    public string RuleName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Trigger { get; set; } = string.Empty;
    public List<string> ApplicableLobs { get; set; } = new();
    public string DebitAccountId { get; set; } = string.Empty;
    public string CreditAccountId { get; set; } = string.Empty;
    public string SplitBehavior { get; set; } = "NoSplit";
    public decimal? AutoApproveThreshold { get; set; }
    public int ExecutionOrder { get; set; }
    public string Status { get; set; } = "Active";
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
}

public class ArBatchRuleTestResult
{
    public string RuleCode { get; set; } = string.Empty;
    public decimal SampleAmount { get; set; }
    public string DebitAccountId { get; set; } = string.Empty;
    public decimal DebitAmount { get; set; }
    public string CreditAccountId { get; set; } = string.Empty;
    public decimal CreditAmount { get; set; }
    public decimal? SponsorSplitAmount { get; set; }
    public decimal? MemberSplitAmount { get; set; }
}
