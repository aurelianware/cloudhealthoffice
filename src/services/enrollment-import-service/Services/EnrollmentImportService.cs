using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EnrollmentImportService.Clients;
using EnrollmentImportService.Models;
using EnrollmentImportService.Repositories;

namespace EnrollmentImportService.Services;

public interface IEnrollmentImportService
{
    Task<ImportResult> ImportEnrollmentAsync(Enrollment834 enrollment, string tenantId);
}

public class EnrollmentImportService : IEnrollmentImportService
{
    private static readonly JsonSerializerOptions RawSegmentJsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IMemberServiceClient _memberClient;
    private readonly ISponsorServiceClient _sponsorClient;
    private readonly IBenefitPlanServiceClient _benefitPlanClient;
    private readonly ICoverageServiceClient _coverageClient;
    private readonly IEnrollmentTransactionRepository _transactions;
    private readonly IEnrollmentImportRunRepository _importRuns;
    private readonly IEnrollmentEventPublisher _eventPublisher;
    private readonly IEnrollmentValidator _validator;
    private readonly ILogger<EnrollmentImportService> _logger;

    public EnrollmentImportService(
        IMemberServiceClient memberClient,
        ISponsorServiceClient sponsorClient,
        IBenefitPlanServiceClient benefitPlanClient,
        ICoverageServiceClient coverageClient,
        IEnrollmentTransactionRepository transactions,
        IEnrollmentImportRunRepository importRuns,
        IEnrollmentEventPublisher eventPublisher,
        IEnrollmentValidator validator,
        ILogger<EnrollmentImportService> logger)
    {
        _memberClient = memberClient;
        _sponsorClient = sponsorClient;
        _benefitPlanClient = benefitPlanClient;
        _coverageClient = coverageClient;
        _transactions = transactions;
        _importRuns = importRuns;
        _eventPublisher = eventPublisher;
        _validator = validator;
        _logger = logger;
    }

    public async Task<ImportResult> ImportEnrollmentAsync(Enrollment834 enrollment, string tenantId)
    {
        var result = new ImportResult
        {
            FileName = enrollment.FileName,
            StartedAt = DateTime.UtcNow
        };

        // Stable batch id when a caller pre-supplies it (manual enrollment, replay tests),
        // otherwise generate. A stable batchId keeps replay event-ids deterministic.
        var batchId = !string.IsNullOrEmpty(enrollment.BatchId)
            ? enrollment.BatchId
            : $"BATCH-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}".Substring(0, 40);

        for (int i = 0; i < enrollment.Enrollments.Count; i++)
        {
            var memberEnrollment = enrollment.Enrollments[i];
            var txnStatus = "Accepted";
            var validation = _validator.Validate(memberEnrollment);
            if (!validation.IsValid)
            {
                var flat = string.Join("; ", validation.ToFlatStrings());
                _logger.LogWarning(
                    "Validation failed for subscriber {SubscriberId}: {Errors}",
                    SanitizeForLog(memberEnrollment.SubscriberId), flat);
                result.Errors.AddRange(
                    validation.Errors.Select(e =>
                        $"Subscriber {memberEnrollment.SubscriberId}: [{e.Code}] {e.Field} — {e.Message}"));
                result.FailedCount++;
                txnStatus = "Rejected";
                await RecordTransactionAsync(tenantId, batchId, enrollment, memberEnrollment, txnStatus);
                continue;
            }

            // Deterministic transaction id keyed on batch + position + subscriber so that
            // (a) replays of an identical batch produce identical EventIds, and
            // (b) multiple enrollments for the same subscriber within one batch (e.g.
            //     two separate life events on the same day) do NOT collapse to the
            //     same id. The position is 0-based and stable within a batch.
            var transactionId = !string.IsNullOrEmpty(memberEnrollment.TransactionId)
                ? memberEnrollment.TransactionId
                : $"{batchId}-{i:D4}-{memberEnrollment.SubscriberId ?? "ANON"}";

            try
            {
                await ProcessMemberEnrollmentAsync(memberEnrollment, tenantId, result);
                await PublishEnrollmentEventAsync(tenantId, batchId, transactionId, enrollment, memberEnrollment);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing enrollment for subscriber {SubscriberId}",
                    SanitizeForLog(memberEnrollment.SubscriberId));
                result.Errors.Add($"Subscriber {memberEnrollment.SubscriberId}: {ex.Message}");
                result.FailedCount++;
                txnStatus = "Rejected";
            }

            await RecordTransactionAsync(tenantId, batchId, enrollment, memberEnrollment, txnStatus);
        }

        // Dependent Loop 2000s whose subscriber wasn't resent in this file
        // (e.g. a newborn add or a child's termination on its own). The
        // subscriber must already exist — a dependent is never used to
        // create, or write onto, a subscriber record.
        foreach (var dependent in enrollment.DependentEnrollments)
        {
            await ProcessStandaloneDependentAsync(dependent, tenantId, result);
        }

        result.CompletedAt = DateTime.UtcNow;
        result.BatchId = batchId;
        _logger.LogInformation(
            "Import completed: {SuccessCount} success, {FailedCount} failed, {SkippedCount} skipped",
            result.SuccessCount, result.FailedCount, result.SkippedCount);

        await RecordRunAsync(tenantId, enrollment.ActorId, result);

        return result;
    }

    /// <summary>
    /// Persists the batch-level summary so it can be looked up again later —
    /// the same shape as <see cref="ImportResult"/> already returned
    /// synchronously to the caller, which otherwise only existed for the
    /// moment of the API call. Failure here must not fail the import itself;
    /// same posture as <see cref="RecordTransactionAsync"/>.
    /// </summary>
    private async Task RecordRunAsync(string tenantId, string? actorId, ImportResult result)
    {
        try
        {
            await _importRuns.CreateAsync(new EnrollmentImportRun
            {
                TenantId = tenantId,
                BatchId = result.BatchId,
                FileName = result.FileName,
                ActorId = actorId,
                StartedAt = result.StartedAt,
                CompletedAt = result.CompletedAt,
                SuccessCount = result.SuccessCount,
                FailedCount = result.FailedCount,
                SkippedCount = result.SkippedCount,
                MembersCreated = result.MembersCreated,
                MembersUpdated = result.MembersUpdated,
                MembersTerminated = result.MembersTerminated,
                DependentsCreated = result.DependentsCreated,
                DependentsUpdated = result.DependentsUpdated,
                DependentsTerminated = result.DependentsTerminated,
                CoverageRecordsCreated = result.CoverageRecordsCreated,
                CoverageMappingsUnresolved = result.CoverageMappingsUnresolved,
                Errors = result.Errors
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to persist EnrollmentImportRun for batch {BatchId}",
                SanitizeForLog(result.BatchId));
        }
    }

    private async Task PublishEnrollmentEventAsync(
        string tenantId,
        string batchId,
        string transactionId,
        Enrollment834 batch,
        MemberEnrollment memberEnrollment)
    {
        var memberId = memberEnrollment.SubscriberId ?? string.Empty;
        if (string.IsNullOrEmpty(memberId)) return;

        var eventType = EnrollmentEventClassifier.Classify(memberEnrollment);
        var eventDate = ParseDate(
            memberEnrollment.MaintenanceType == "024"
                ? memberEnrollment.TerminationDate
                : memberEnrollment.EnrollmentDate);

        var retroEffectiveDate = IsRetro(memberEnrollment, eventDate)
            ? eventDate
            : (DateTime?)null;

        var payload = new JsonObject
        {
            ["benefitStatus"] = memberEnrollment.BenefitStatus,
            ["relationship"] = memberEnrollment.Relationship,
            ["groupNumber"] = memberEnrollment.GroupNumber,
            ["enrollmentDate"] = memberEnrollment.EnrollmentDate,
            ["terminationDate"] = memberEnrollment.TerminationDate,
            ["coverageCount"] = memberEnrollment.Coverage?.Count ?? 0,
            ["dependentCount"] = memberEnrollment.Dependents?.Count ?? 0
        };

        var rawSegment = SerializeRawSegment(memberEnrollment);

        // EventId construction differs by source so 834 replays are deterministic
        // (idempotent dedup) while back-to-back manual POSTs from the same batch wrapper
        // never accidentally collide. Manual callers either supply their own EventId for
        // retry safety or get a fresh GUID per POST.
        string eventId;
        if (batch.ManualSource)
        {
            var requestEventId = string.IsNullOrWhiteSpace(memberEnrollment.EventId)
                ? Guid.NewGuid().ToString("N")
                : memberEnrollment.EventId;
            eventId = EnrollmentEvent.BuildManualEventId(requestEventId, memberId);
        }
        else
        {
            eventId = EnrollmentEvent.BuildIngestEventId(batchId, transactionId, memberId);
        }

        var evt = new EnrollmentEvent
        {
            TenantId = tenantId,
            MemberId = memberId,
            EventId = eventId,
            EventType = eventType,
            OccurredAt = DateTime.UtcNow,
            EventDate = eventDate,
            RetroEffectiveDate = retroEffectiveDate,
            SourceBatchId = batchId,
            TransactionId = transactionId,
            MaintenanceType = memberEnrollment.MaintenanceType,
            MaintenanceReason = memberEnrollment.MaintenanceReason,
            Source = batch.ManualSource ? "manual" : "edi834",
            ActorId = batch.ActorId,
            CorrelationId = batch.FileName,
            Payload = payload,
            RawSegment = rawSegment
        };

        try
        {
            await _eventPublisher.PublishAsync(evt);
        }
        catch (Exception ex)
        {
            // Event publication is not allowed to break the import — the transaction log
            // still records the txn. Surface as a warning so it shows up in dashboards.
            _logger.LogWarning(ex,
                "Failed to publish EnrollmentEvent for {Tenant}:{Member} batch {BatchId}",
                SanitizeForLog(tenantId), SanitizeForLog(memberId), SanitizeForLog(batchId));
        }
    }

    private static bool IsRetro(MemberEnrollment e, DateTime? eventDate) =>
        eventDate.HasValue && eventDate.Value.Date < DateTime.UtcNow.Date.AddDays(-30);

    private static string SerializeRawSegment(MemberEnrollment e)
    {
        // PHI lives in here (names, addresses, optionally SSN). Persistence relies on
        // container-level encryption-at-rest — do NOT also re-encrypt at the field level.
        // Telemetry exporters that read this field MUST scrub it the same way they scrub
        // span attributes.
        try
        {
            var raw = JsonSerializer.Serialize(e, RawSegmentJsonOptions);
            // Cap raw snippet so we don't blow Cosmos's 2MB doc limit on huge dependents.
            return raw.Length > 8000 ? raw.Substring(0, 8000) : raw;
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task RecordTransactionAsync(
        string tenantId,
        string batchId,
        Enrollment834 batch,
        MemberEnrollment memberEnrollment,
        string status)
    {
        try
        {
            var memberId = memberEnrollment.SubscriberId ?? string.Empty;
            var firstName = memberEnrollment.Demographics?.FirstName ?? string.Empty;
            var lastName = memberEnrollment.Demographics?.LastName ?? string.Empty;

            await _transactions.CreateAsync(new EnrollmentTransaction
            {
                TenantId = tenantId,
                BatchId = batchId,
                TransactionId = $"{batchId}-{Guid.NewGuid():N}".Substring(0, 40),
                MemberId = memberId,
                SubscriberId = memberEnrollment.SubscriberId,
                MemberName = $"{firstName} {lastName}".Trim(),
                MaintenanceTypeCode = memberEnrollment.MaintenanceType ?? string.Empty,
                TransactionDate = batch.ParsedAt == default ? DateTime.UtcNow : batch.ParsedAt,
                ReceivedAt = DateTime.UtcNow,
                Status = status,
                FileName = batch.FileName
            });
        }
        catch (Exception ex)
        {
            // Transaction-log failures must not bring down the import; log and move on.
            _logger.LogWarning(ex,
                "Failed to persist EnrollmentTransaction for subscriber {SubscriberId}",
                SanitizeForLog(memberEnrollment.SubscriberId));
        }
    }

    private async Task ProcessMemberEnrollmentAsync(MemberEnrollment enrollment, string tenantId, ImportResult result)
    {
        // 1. Ensure the sponsor (employer/group) exists. Sponsor-service keys
        // sponsors by GroupNumber (REF*1L, e.g. "GRP0001") — NOT by the N1
        // segment's own id (typically the employer's FEIN), which is a
        // separate concept mapped to TaxId below.
        if (enrollment.Sponsor != null && !string.IsNullOrEmpty(enrollment.Sponsor.Id))
        {
            await EnsureSponsorExistsAsync(enrollment.Sponsor, enrollment.GroupNumber, tenantId);
        }

        // 2. Process Member (subscriber). MemberId == SubscriberId whenever the
        // 834 supplies one (GenerateMemberId's primary path) — member-service
        // is queried directly by that id rather than via a separate
        // subscriber-id search.
        var memberId = GenerateMemberId(enrollment);
        var memberExists = !string.IsNullOrEmpty(enrollment.SubscriberId) &&
            await _memberClient.ExistsAsync(tenantId, memberId);

        // Whether the subscriber's own coverage lines should be written, and
        // whether the subscriber is on file afterwards (dependents are only
        // ever linked to a subscriber that exists in member-service).
        var syncSubscriberCoverage = enrollment.MaintenanceType != "024";
        var subscriberOnFile = true;

        switch (enrollment.MaintenanceType)
        {
            case "021": // Addition
                if (memberExists)
                {
                    // Re-import of the same add: leave the subscriber alone,
                    // but still reconcile its dependents (each keyed
                    // deterministically, so a replay updates nothing twice).
                    _logger.LogWarning("Member {SubscriberId} already exists, skipping addition",
                        SanitizeForLog(enrollment.SubscriberId));
                    result.SkippedCount++;
                    syncSubscriberCoverage = false;
                    break;
                }
                await CreateMemberFromEnrollmentAsync(memberId, enrollment, tenantId);
                result.MembersCreated++;
                result.SuccessCount++;
                break;

            case "001": // Change
            case "025": // Reinstatement — same member-sync shape as a Change:
                        // create if this is the first we've seen of them,
                        // otherwise update. UpdateMemberFromEnrollmentAsync
                        // derives Status from BenefitStatus, which a
                        // reinstatement's "A" correctly flips back to Active
                        // (or "C" to COBRA) without any special-casing here.
                if (!memberExists)
                {
                    _logger.LogWarning("Member {SubscriberId} not found for change/reinstatement, creating new",
                        SanitizeForLog(enrollment.SubscriberId));
                    await CreateMemberFromEnrollmentAsync(memberId, enrollment, tenantId);
                    result.MembersCreated++;
                }
                else
                {
                    await UpdateMemberFromEnrollmentAsync(memberId, enrollment, tenantId);
                    result.MembersUpdated++;
                }
                result.SuccessCount++;
                break;

            case "024": // Termination
                if (!memberExists)
                {
                    _logger.LogWarning("Member {SubscriberId} not found for termination, skipping",
                        SanitizeForLog(enrollment.SubscriberId));
                    result.SkippedCount++;
                    subscriberOnFile = false;
                    break;
                }
                await _memberClient.TerminateAsync(tenantId, memberId, new TerminateMemberRequestDto
                {
                    MemberId = memberId,
                    CoverageId = string.Empty,
                    TerminationDate = ParseDate(enrollment.TerminationDate) ?? DateTime.UtcNow,
                    ReasonCode = "834"
                });
                result.MembersTerminated++;
                result.SuccessCount++;
                break;

            default:
                _logger.LogWarning("Unknown maintenance type {MaintenanceType}", SanitizeForLog(enrollment.MaintenanceType));
                result.SkippedCount++;
                return;
        }

        // 3. Process Coverage (health plans) — delegated to coverage-service,
        // same as Member/Sponsor above. PlanId is resolved via
        // benefit-plan-service's plan-code-mapping crosswalk first, since the
        // raw 834 only carries the trading partner's own plan code, not this
        // platform's PlanId.
        if (syncSubscriberCoverage)
        {
            foreach (var coverageDetail in enrollment.Coverage)
            {
                await ProcessCoverageAndCountAsync(
                    memberId, tenantId, coverageDetail, enrollment.EnrollmentDate, enrollment.GroupNumber, result);
            }
        }

        // 4. Process Dependents — each is its own member (its own 834 Loop
        // 2000) with its own id and maintenance type; nothing here writes to
        // the subscriber's record.
        if (enrollment.Dependents.Count == 0)
        {
            return;
        }
        if (!subscriberOnFile)
        {
            _logger.LogWarning(
                "Subscriber {SubscriberId} not on file; skipping {Count} dependent(s)",
                SanitizeForLog(enrollment.SubscriberId), enrollment.Dependents.Count);
            return;
        }
        foreach (var dependent in enrollment.Dependents)
        {
            await ProcessDependentAsync(dependent, tenantId, memberId, enrollment, result);
        }
    }

    /// <summary>
    /// A dependent Loop 2000 that arrived without its subscriber's INS loop.
    /// Counted as its own top-level transaction in Success/Failed/Skipped.
    /// </summary>
    private async Task ProcessStandaloneDependentAsync(Dependent dependent, string tenantId, ImportResult result)
    {
        var subscriberId = dependent.SubscriberId;
        if (string.IsNullOrWhiteSpace(subscriberId) || string.IsNullOrWhiteSpace(dependent.MaintenanceType))
        {
            result.Errors.Add(
                $"Dependent of subscriber {subscriberId}: subscriberId (REF*0F) and maintenanceType (INS03) are required");
            result.FailedCount++;
            return;
        }

        try
        {
            if (!await _memberClient.ExistsAsync(tenantId, subscriberId))
            {
                if (dependent.MaintenanceType == "024")
                {
                    _logger.LogWarning(
                        "Subscriber {SubscriberId} not found for dependent termination, skipping",
                        SanitizeForLog(subscriberId));
                    result.SkippedCount++;
                }
                else
                {
                    result.Errors.Add($"Subscriber {subscriberId}: not on file; cannot apply dependent maintenance");
                    result.FailedCount++;
                }
                return;
            }

            if (await ProcessDependentAsync(dependent, tenantId, subscriberId, null, result))
            {
                result.SuccessCount++;
            }
            else
            {
                result.SkippedCount++;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing dependent of subscriber {SubscriberId}",
                SanitizeForLog(subscriberId));
            result.Errors.Add($"Subscriber {subscriberId} dependent: {ex.Message}");
            result.FailedCount++;
        }
    }

    private async Task EnsureSponsorExistsAsync(Sponsor sponsor, string? groupNumber, string tenantId)
    {
        if (string.IsNullOrEmpty(sponsor.Id))
        {
            throw new ArgumentException("Sponsor ID is required");
        }

        if (string.IsNullOrEmpty(groupNumber))
        {
            // No REF*1L group number on this enrollment — sponsor-service keys
            // sponsors by group number, so there's nothing to sync against.
            _logger.LogWarning(
                "Sponsor {SponsorId} has no group number (REF*1L) on this enrollment; skipping sponsor-service sync",
                SanitizeForLog(sponsor.Id));
            return;
        }

        if (await _sponsorClient.ExistsAsync(tenantId, groupNumber))
        {
            return;
        }

        await _sponsorClient.CreateAsync(tenantId, new CreateSponsorRequestDto
        {
            GroupNumber = groupNumber,
            EmployerName = sponsor.Name,
            TaxId = sponsor.IdQualifier == "FI" ? sponsor.Id : null,
            // The 834 sponsor (N1) loop doesn't carry its own effective date —
            // approximate with import time rather than guess at a business date.
            EffectiveDate = DateTime.UtcNow
        });
    }

    private async Task CreateMemberFromEnrollmentAsync(string memberId, MemberEnrollment enrollment, string tenantId)
    {
        await _memberClient.CreateAsync(tenantId, new CreateMemberRequestDto
        {
            MemberId = memberId,
            SSN = enrollment.Demographics?.IdQualifier == "34" ? enrollment.Demographics.Id : null,
            GroupNumber = enrollment.GroupNumber ?? string.Empty,
            IsSubscriber = true,
            FirstName = enrollment.Demographics?.FirstName ?? string.Empty,
            LastName = enrollment.Demographics?.LastName ?? string.Empty,
            MiddleName = enrollment.Demographics?.MiddleName,
            DateOfBirth = ParseDate(enrollment.Demographics?.DateOfBirth) ?? default,
            Gender = enrollment.Demographics?.Gender,
            Address = enrollment.Demographics?.Address1,
            City = enrollment.Demographics?.City,
            State = enrollment.Demographics?.State,
            ZipCode = enrollment.Demographics?.Zip
        });
    }

    private async Task UpdateMemberFromEnrollmentAsync(string memberId, MemberEnrollment enrollment, string tenantId)
    {
        // member-service's PUT only supports address/contact/status fields (see
        // UpdateMemberRequest) — it has no way to update name/DOB/gender via this
        // endpoint. A "001 Change" 834 that corrects demographics can't fully
        // apply through this API today; known limitation of delegating here
        // rather than writing directly, not something this change attempts to
        // paper over.
        await _memberClient.UpdateAsync(tenantId, memberId, new UpdateMemberRequestDto
        {
            Address = enrollment.Demographics?.Address1,
            City = enrollment.Demographics?.City,
            State = enrollment.Demographics?.State,
            ZipCode = enrollment.Demographics?.Zip,
            Status = MapStatus(enrollment.BenefitStatus)
        });
    }

    private static string MapStatus(string? benefitStatus) =>
        benefitStatus == "A" ? "Active" :
        benefitStatus == "C" ? "COBRA" : "Terminated";

    /// <summary>
    /// Resolves the 834's own plan code (HD04) to benefit-plan-service's PlanId
    /// via the plan-code-mapping crosswalk, then writes Coverage. Returns false
    /// — without writing a Coverage record — when there's no group number/plan
    /// code to resolve with, or no mapping exists yet; the caller surfaces this
    /// as <see cref="ImportResult.CoverageMappingsUnresolved"/> rather than
    /// silently defaulting the PlanId, which just hid the same gap downstream.
    /// </summary>
    private async Task<bool> ProcessCoverageAsync(
        string memberId, string tenantId, CoverageDetail coverageDetail, string? effectiveDate, string? groupNumber)
    {
        var externalPlanCode = coverageDetail.PlanCoverageDescription;
        if (string.IsNullOrWhiteSpace(externalPlanCode) || string.IsNullOrWhiteSpace(groupNumber))
        {
            _logger.LogWarning(
                "Coverage for member {MemberId} is missing group number or plan code (HD04); cannot resolve PlanId",
                SanitizeForLog(memberId));
            return false;
        }

        var planId = await _benefitPlanClient.ResolvePlanIdAsync(
            tenantId, groupNumber, coverageDetail.InsuranceLineCode, externalPlanCode);
        if (planId is null)
        {
            _logger.LogWarning(
                "No plan-code mapping for group {GroupNumber} line {InsuranceLineCode} code {ExternalCode}; skipping coverage for {MemberId}",
                SanitizeForLog(groupNumber), SanitizeForLog(coverageDetail.InsuranceLineCode),
                SanitizeForLog(externalPlanCode), SanitizeForLog(memberId));
            return false;
        }

        await _coverageClient.CreateAsync(tenantId, new CreateCoverageRequestDto
        {
            MemberId = memberId,
            GroupNumber = groupNumber,
            PlanId = planId,
            InsuranceLineCode = coverageDetail.InsuranceLineCode,
            CoverageLevel = coverageDetail.CoverageLevel ?? "EMP",
            // Loop 2300 DTP*348 is this coverage's own benefit begin; the
            // member-level date is only a fallback when the file omits it.
            EffectiveDate = ParseDate(coverageDetail.BenefitBeginDate) ?? ParseDate(effectiveDate) ?? DateTime.UtcNow,
            TerminationDate = ParseDate(coverageDetail.BenefitEndDate),
            MaintenanceTypeCode = coverageDetail.MaintenanceType
        });
        return true;
    }

    private async Task ProcessCoverageAndCountAsync(
        string memberId, string tenantId, CoverageDetail coverageDetail, string? effectiveDate,
        string? groupNumber, ImportResult result)
    {
        if (await ProcessCoverageAsync(memberId, tenantId, coverageDetail, effectiveDate, groupNumber))
        {
            result.CoverageRecordsCreated++;
        }
        else
        {
            result.CoverageMappingsUnresolved++;
        }
    }

    /// <summary>
    /// Applies one dependent's own maintenance (its INS03, inheriting the
    /// subscriber's only when a JSON caller didn't send one) to the
    /// dependent's own member record. Returns false when nothing was applied
    /// (already added, not found for termination, unkeyable, unknown type).
    /// </summary>
    private async Task<bool> ProcessDependentAsync(
        Dependent dependent, string tenantId, string subscriberMemberId, MemberEnrollment? subscriber,
        ImportResult result)
    {
        var dependentMemberId = BuildDependentMemberId(subscriberMemberId, dependent);
        if (dependentMemberId is null)
        {
            result.Errors.Add(
                $"Subscriber {subscriberMemberId}: dependent has no member identifier (REF*23) and no first name + date of birth to key it by; skipped");
            return false;
        }

        var maintenanceType = dependent.MaintenanceType ?? subscriber?.MaintenanceType;
        var groupNumber = dependent.GroupNumber ?? subscriber?.GroupNumber;
        var exists = await _memberClient.ExistsAsync(tenantId, dependentMemberId);

        switch (maintenanceType)
        {
            case "021": // Addition
                if (exists)
                {
                    _logger.LogWarning("Dependent {DependentId} already exists, skipping addition",
                        SanitizeForLog(dependentMemberId));
                    return false;
                }
                await CreateDependentAsync(dependentMemberId, dependent, tenantId, subscriberMemberId, groupNumber);
                result.DependentsCreated++;
                break;

            case "001": // Change
            case "025": // Reinstatement
                if (exists)
                {
                    await _memberClient.UpdateAsync(tenantId, dependentMemberId, new UpdateMemberRequestDto
                    {
                        Address = dependent.Address1,
                        City = dependent.City,
                        State = dependent.State,
                        ZipCode = dependent.Zip,
                        Status = MapStatus(dependent.BenefitStatus ?? subscriber?.BenefitStatus)
                    });
                    result.DependentsUpdated++;
                }
                else
                {
                    await CreateDependentAsync(dependentMemberId, dependent, tenantId, subscriberMemberId, groupNumber);
                    result.DependentsCreated++;
                }
                break;

            case "024": // Termination — terminate the matching dependent, never create one.
                if (!exists)
                {
                    _logger.LogWarning("Dependent {DependentId} not found for termination, skipping",
                        SanitizeForLog(dependentMemberId));
                    return false;
                }
                await _memberClient.TerminateAsync(tenantId, dependentMemberId, new TerminateMemberRequestDto
                {
                    MemberId = dependentMemberId,
                    CoverageId = string.Empty,
                    TerminationDate = ParseDate(dependent.TerminationDate)
                        ?? ParseDate(subscriber?.TerminationDate)
                        ?? DateTime.UtcNow,
                    ReasonCode = "834"
                });
                result.DependentsTerminated++;
                return true;

            default:
                _logger.LogWarning("Unknown maintenance type {MaintenanceType} for dependent {DependentId}",
                    SanitizeForLog(maintenanceType), SanitizeForLog(dependentMemberId));
                return false;
        }

        if (dependent.Coverage != null)
        {
            foreach (var coverageDetail in dependent.Coverage)
            {
                await ProcessCoverageAndCountAsync(
                    dependentMemberId, tenantId, coverageDetail,
                    dependent.EnrollmentDate ?? subscriber?.EnrollmentDate, groupNumber, result);
            }
        }
        return true;
    }

    private async Task CreateDependentAsync(
        string dependentMemberId, Dependent dependent, string tenantId, string subscriberMemberId, string? groupNumber)
    {
        await _memberClient.CreateAsync(tenantId, new CreateMemberRequestDto
        {
            MemberId = dependentMemberId,
            SSN = dependent.IdQualifier == "34" ? dependent.Id : null,
            GroupNumber = groupNumber ?? string.Empty,
            IsSubscriber = false,
            // member-service links dependent<->subscriber itself via
            // SubscriberMemberId at create time (its own FamilyRelationship
            // graph) — no separate fetch-subscriber/append-id/write-back
            // needed, unlike the old direct-Mongo path.
            SubscriberMemberId = subscriberMemberId,
            // INS02 individual relationship code (01 spouse, 19 child, ...).
            RelationshipCode = dependent.Relationship,
            FirstName = dependent.FirstName,
            LastName = dependent.LastName,
            MiddleName = dependent.MiddleName,
            DateOfBirth = ParseDate(dependent.DateOfBirth) ?? default,
            Gender = dependent.Gender,
            Address = dependent.Address1,
            City = dependent.City,
            State = dependent.State,
            ZipCode = dependent.Zip
        });
    }

    /// <summary>
    /// Deterministic dependent member id, so re-importing the same dependent
    /// (replay, or a later 001/024 for them) resolves to the same record
    /// instead of minting a new one each pass. Keyed under the subscriber by
    /// the trading partner's member-level identifier (REF*23) when
    /// present, otherwise by normalized first name + date of birth. The key
    /// is hashed so the id has a fixed shape and carries no PHI. Returns
    /// null when there's nothing stable to key on.
    /// </summary>
    public static string? BuildDependentMemberId(string subscriberMemberId, Dependent dependent)
    {
        string key;
        if (!string.IsNullOrWhiteSpace(dependent.MemberIdentifier))
        {
            key = "ID|" + dependent.MemberIdentifier.Trim().ToUpperInvariant();
        }
        else
        {
            var dob = ParseDate(dependent.DateOfBirth);
            if (string.IsNullOrWhiteSpace(dependent.FirstName) || dob is null)
            {
                return null;
            }
            key = $"ND|{dependent.FirstName.Trim().ToUpperInvariant()}|{dob.Value:yyyyMMdd}";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return $"{subscriberMemberId}-D{Convert.ToHexString(hash, 0, 6)}";
    }

    private string GenerateMemberId(MemberEnrollment enrollment)
    {
        // Use SubscriberId if available, otherwise generate
        if (!string.IsNullOrEmpty(enrollment.SubscriberId))
        {
            return enrollment.SubscriberId;
        }

        // Generate from demographics
        var lastName = enrollment.Demographics?.LastName?.Substring(0, Math.Min(3, enrollment.Demographics.LastName.Length)).ToUpper() ?? "UNK";
        var dob = enrollment.Demographics?.DateOfBirth?.Replace("-", "").Substring(2, 6) ?? "000000"; // YYMMDD
        var random = Guid.NewGuid().ToString("N").Substring(0, 4).ToUpper();

        return $"M{lastName}{dob}{random}";
    }

    /// <summary>
    /// Parses an 834 date. The 834 always carries dates in X12's D8 format
    /// (CCYYMMDD, e.g. "19780922") — DateTime.TryParse doesn't recognize
    /// that as a date at all (it looks like a plain number, not any
    /// culture's date format) and silently returns false, which is why
    /// DateOfBirth/EnrollmentDate/TerminationDate were all coming back
    /// null despite being present in the source segment. TryParseExact
    /// with the explicit D8 format fixes all three; the general TryParse
    /// fallback stays for any caller that isn't handing this raw 834 text.
    /// </summary>
    private static DateTime? ParseDate(string? dateString)
    {
        if (string.IsNullOrEmpty(dateString))
            return null;

        if (DateTime.TryParseExact(dateString, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d8Date))
            return d8Date;

        if (DateTime.TryParse(dateString, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;

        return null;
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

public class ImportResult
{
    public string FileName { get; set; } = string.Empty;
    public string BatchId { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int SuccessCount { get; set; }
    public int FailedCount { get; set; }
    public int SkippedCount { get; set; }
    public int MembersCreated { get; set; }
    public int MembersUpdated { get; set; }
    public int MembersTerminated { get; set; }
    public int DependentsCreated { get; set; }
    public int DependentsUpdated { get; set; }
    public int DependentsTerminated { get; set; }
    public int CoverageRecordsCreated { get; set; }
    public int CoverageMappingsUnresolved { get; set; }
    public List<string> Errors { get; set; } = new();
}
