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
        var ctx = new BatchContext(tenantId, batchId, enrollment, result);

        // Parser warnings (e.g. a pre-X220A1 LS/LE "dependent" block whose
        // contents could not be imported) go on the run's error list so an
        // operator sees them, rather than the data silently disappearing.
        foreach (var warning in enrollment.ParseWarnings)
        {
            _logger.LogWarning("834 parse warning for {FileName}: {Warning}",
                SanitizeForLog(enrollment.FileName), SanitizeForLog(warning));
            result.Errors.Add(warning);
        }

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
                await ProcessMemberEnrollmentAsync(memberEnrollment, ctx, transactionId);
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
        for (var k = 0; k < enrollment.DependentEnrollments.Count; k++)
        {
            await ProcessStandaloneDependentAsync(enrollment.DependentEnrollments[k], ctx, k);
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
                CoverageRecordsUpdated = result.CoverageRecordsUpdated,
                CoverageRecordsReinstated = result.CoverageRecordsReinstated,
                CoverageRecordsTerminated = result.CoverageRecordsTerminated,
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
        MemberEnrollment memberEnrollment,
        string? dependentMemberId = null)
    {
        // Subscriber events are keyed by SubscriberId; a dependent's event by
        // the dependent's own member id (with SubscriberId in the payload).
        var memberId = dependentMemberId ?? memberEnrollment.SubscriberId ?? string.Empty;
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
        if (dependentMemberId is not null)
        {
            payload["subscriberId"] = memberEnrollment.SubscriberId;
        }

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
        string status,
        string? dependentMemberId = null)
    {
        try
        {
            var memberId = dependentMemberId ?? memberEnrollment.SubscriberId ?? string.Empty;
            var firstName = memberEnrollment.Demographics?.FirstName ?? string.Empty;
            var lastName = memberEnrollment.Demographics?.LastName ?? string.Empty;

            await _transactions.CreateAsync(new EnrollmentTransaction
            {
                TenantId = tenantId,
                BatchId = batchId,
                // Capped at 40 chars; a short caller-supplied batchId must not
                // make Substring throw (which silently dropped the row).
                TransactionId = Truncate($"{batchId}-{Guid.NewGuid():N}", 40),
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

    /// <summary>Batch-level context threaded through member, dependent and coverage processing.</summary>
    private sealed record BatchContext(string TenantId, string BatchId, Enrollment834 Batch, ImportResult Result);

    private async Task ProcessMemberEnrollmentAsync(
        MemberEnrollment enrollment, BatchContext ctx, string transactionId)
    {
        var tenantId = ctx.TenantId;
        var result = ctx.Result;
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

        // Whether the subscriber is on file afterwards: its coverage and its
        // dependents are only ever reconciled against a member that exists.
        var subscriberOnFile = true;

        switch (enrollment.MaintenanceType)
        {
            case "021": // Addition
                if (memberExists)
                {
                    // Re-import of the same add: leave the subscriber alone,
                    // but still reconcile coverage and dependents — both are
                    // keyed deterministically, so a replay changes nothing.
                    _logger.LogWarning("Member {SubscriberId} already exists, skipping addition",
                        SanitizeForLog(enrollment.SubscriberId));
                    result.SkippedCount++;
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
        // platform's PlanId. Each HD line's own maintenance type drives the
        // change (see ApplyCoverageAsync); a member-level 024 terminates
        // every line regardless of HD01.
        if (subscriberOnFile)
        {
            await ApplyMemberCoverageAsync(
                memberId, enrollment.Coverage, enrollment.MaintenanceType, enrollment.MaintenanceReason,
                enrollment.EnrollmentDate, enrollment.TerminationDate, enrollment.GroupNumber, ctx);
        }

        // 4. Process Dependents — each is its own member (its own 834 Loop
        // 2000) with its own id, maintenance type, transaction-log row and
        // enrollment event; nothing here writes to the subscriber's record.
        if (enrollment.Dependents.Count == 0)
        {
            return;
        }
        if (!subscriberOnFile)
        {
            _logger.LogWarning(
                "Subscriber {SubscriberId} not on file; skipping {Count} dependent(s)",
                SanitizeForLog(enrollment.SubscriberId), enrollment.Dependents.Count);
            result.Errors.Add(
                $"Subscriber {enrollment.SubscriberId}: not on file; {enrollment.Dependents.Count} dependent(s) not applied");
            return;
        }
        for (var j = 0; j < enrollment.Dependents.Count; j++)
        {
            await ProcessDependentTransactionAsync(
                enrollment.Dependents[j], memberId, enrollment, ctx, $"{transactionId}-D{j:D2}");
        }
    }

    /// <summary>
    /// A dependent Loop 2000 that arrived without its subscriber's INS loop.
    /// Counted as its own top-level transaction in Success/Failed/Skipped.
    /// </summary>
    private async Task ProcessStandaloneDependentAsync(Dependent dependent, BatchContext ctx, int index)
    {
        var result = ctx.Result;
        var subscriberId = dependent.SubscriberId;
        if (string.IsNullOrWhiteSpace(subscriberId) || string.IsNullOrWhiteSpace(dependent.MaintenanceType))
        {
            result.Errors.Add(
                $"Dependent of subscriber {subscriberId}: subscriberId (REF*0F) and maintenanceType (INS03) are required");
            result.FailedCount++;
            await RecordTransactionAsync(ctx.TenantId, ctx.BatchId, ctx.Batch,
                ToMemberEnrollment(dependent, null, dependent.MaintenanceType), "Rejected");
            return;
        }

        bool exists;
        try
        {
            exists = await _memberClient.ExistsAsync(ctx.TenantId, subscriberId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error looking up subscriber {SubscriberId} for dependent",
                SanitizeForLog(subscriberId));
            result.Errors.Add($"Subscriber {subscriberId} dependent: {ex.Message}");
            result.FailedCount++;
            return;
        }

        if (!exists)
        {
            var projected = ToMemberEnrollment(dependent, null, dependent.MaintenanceType);
            if (dependent.MaintenanceType == "024")
            {
                _logger.LogWarning(
                    "Subscriber {SubscriberId} not found for dependent termination, skipping",
                    SanitizeForLog(subscriberId));
                result.SkippedCount++;
                // Same status the subscriber loop records for a not-found 024.
                await RecordTransactionAsync(ctx.TenantId, ctx.BatchId, ctx.Batch, projected, "Accepted");
            }
            else
            {
                result.Errors.Add($"Subscriber {subscriberId}: not on file; cannot apply dependent maintenance");
                result.FailedCount++;
                await RecordTransactionAsync(ctx.TenantId, ctx.BatchId, ctx.Batch, projected, "Rejected");
            }
            return;
        }

        var outcome = await ProcessDependentTransactionAsync(
            dependent, subscriberId, null, ctx, $"{ctx.BatchId}-S{index:D4}-{subscriberId}");
        switch (outcome)
        {
            case DependentOutcome.Applied: result.SuccessCount++; break;
            case DependentOutcome.Skipped: result.SkippedCount++; break;
            default: result.FailedCount++; break;
        }
    }

    private enum DependentOutcome { Applied, Skipped, Failed }

    /// <summary>
    /// Applies one dependent and writes its audit trail the same way the
    /// subscriber loop does: an EnrollmentEvent (deterministic id keyed on
    /// the dependent's own member id) and an EnrollmentTransaction row. A
    /// failure is contained to this dependent.
    /// </summary>
    private async Task<DependentOutcome> ProcessDependentTransactionAsync(
        Dependent dependent, string subscriberMemberId, MemberEnrollment? subscriber, BatchContext ctx,
        string transactionId)
    {
        var maintenanceType = dependent.MaintenanceType ?? subscriber?.MaintenanceType;
        var projected = ToMemberEnrollment(dependent, subscriber, maintenanceType);
        projected.SubscriberId ??= subscriberMemberId;
        projected.TransactionId = transactionId;

        var dependentMemberId = BuildDependentMemberId(subscriberMemberId, dependent);
        if (dependentMemberId is null)
        {
            ctx.Result.Errors.Add(
                $"Subscriber {subscriberMemberId}: dependent has no member identifier (REF*23) and no first name + date of birth to key it by; not applied");
            await RecordTransactionAsync(ctx.TenantId, ctx.BatchId, ctx.Batch, projected, "Rejected");
            return DependentOutcome.Failed;
        }

        try
        {
            var applied = await ProcessDependentAsync(
                dependent, dependentMemberId, maintenanceType, subscriberMemberId, subscriber, ctx);
            await PublishEnrollmentEventAsync(
                ctx.TenantId, ctx.BatchId, transactionId, ctx.Batch, projected, dependentMemberId);
            await RecordTransactionAsync(
                ctx.TenantId, ctx.BatchId, ctx.Batch, projected, "Accepted", dependentMemberId);
            return applied ? DependentOutcome.Applied : DependentOutcome.Skipped;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing dependent {DependentId} of subscriber {SubscriberId}",
                SanitizeForLog(dependentMemberId), SanitizeForLog(subscriberMemberId));
            ctx.Result.Errors.Add($"Dependent {dependentMemberId}: {ex.Message}");
            await RecordTransactionAsync(
                ctx.TenantId, ctx.BatchId, ctx.Batch, projected, "Rejected", dependentMemberId);
            return DependentOutcome.Failed;
        }
    }

    /// <summary>
    /// Projects a dependent onto the <see cref="MemberEnrollment"/> shape the
    /// event classifier, event payload and transaction log already consume,
    /// inheriting from the subscriber only what the dependent didn't send.
    /// </summary>
    private static MemberEnrollment ToMemberEnrollment(
        Dependent dependent, MemberEnrollment? subscriber, string? maintenanceType) => new()
    {
        Relationship = dependent.Relationship ?? string.Empty,
        MaintenanceType = maintenanceType ?? string.Empty,
        MaintenanceReason = dependent.MaintenanceReason ?? subscriber?.MaintenanceReason,
        BenefitStatus = dependent.BenefitStatus ?? subscriber?.BenefitStatus ?? string.Empty,
        SubscriberId = dependent.SubscriberId ?? subscriber?.SubscriberId,
        GroupNumber = dependent.GroupNumber ?? subscriber?.GroupNumber,
        EnrollmentDate = dependent.EnrollmentDate ?? subscriber?.EnrollmentDate,
        TerminationDate = dependent.TerminationDate ?? subscriber?.TerminationDate,
        EventId = subscriber?.EventId,
        Demographics = new Demographics
        {
            EntityType = dependent.EntityType,
            FirstName = dependent.FirstName,
            LastName = dependent.LastName,
            MiddleName = dependent.MiddleName,
            Suffix = dependent.Suffix,
            IdQualifier = dependent.IdQualifier,
            Id = dependent.Id,
            Address1 = dependent.Address1,
            Address2 = dependent.Address2,
            City = dependent.City,
            State = dependent.State,
            Zip = dependent.Zip,
            DateOfBirth = dependent.DateOfBirth,
            Gender = dependent.Gender
        },
        Coverage = dependent.Coverage ?? new()
    };

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

    /// <summary>
    /// INS05 benefit status: A=Active, C=COBRA, S=Surviving Insured,
    /// T=TEFRA. None of these means "terminated" — termination is INS03=024
    /// (member-service TerminateAsync) plus coverage end dates — so only
    /// COBRA maps to a distinct member-service status.
    /// </summary>
    private static string MapStatus(string? benefitStatus) =>
        benefitStatus == "C" ? "COBRA" : "Active";

    /// <summary>
    /// Reconciles one member's 834 coverage lines (Loop 2300) against what
    /// coverage-service already holds for that member. Each HD's own HD01
    /// maintenance type drives the change; a member-level 024 terminates
    /// every line (and, when no HD was sent, every open coverage).
    /// </summary>
    private async Task ApplyMemberCoverageAsync(
        string memberId,
        IReadOnlyList<CoverageDetail>? coverage,
        string? memberMaintenanceType,
        string? maintenanceReason,
        string? memberEffectiveDate,
        string? memberTerminationDate,
        string? groupNumber,
        BatchContext ctx)
    {
        List<CoverageRecordDto>? existing = null;
        async Task<List<CoverageRecordDto>> Existing() =>
            existing ??= (await _coverageClient.GetMemberCoverageAsync(ctx.TenantId, memberId) ?? []).ToList();

        if (coverage is null || coverage.Count == 0)
        {
            if (memberMaintenanceType == "024")
            {
                var termDate = ParseDate(memberTerminationDate) ?? DateTime.UtcNow.Date;
                foreach (var open in (await Existing()).Where(e =>
                             !string.IsNullOrEmpty(e.Id)
                             && e.EffectiveDate.Date <= termDate
                             && (e.TerminationDate is null || e.TerminationDate.Value.Date > termDate)))
                {
                    await TerminateCoverageAsync(open, termDate, maintenanceReason, ctx);
                }
            }
            return;
        }

        foreach (var line in coverage)
        {
            var planId = await ResolvePlanIdAsync(memberId, ctx.TenantId, line, groupNumber);
            if (planId is null)
            {
                ctx.Result.CoverageMappingsUnresolved++;

                // A termination must not be lost to a missing plan-code
                // mapping (that would leave the coverage paying claims):
                // end the open coverage(s) on the same insurance line.
                if (memberMaintenanceType == "024" || line.MaintenanceType == "024")
                {
                    var termDate = ParseDate(line.BenefitEndDate)
                        ?? ParseDate(memberTerminationDate)
                        ?? DateTime.UtcNow.Date;
                    foreach (var open in (await Existing()).Where(e =>
                                 !string.IsNullOrEmpty(e.Id)
                                 && string.Equals(e.InsuranceLineCode, line.InsuranceLineCode, StringComparison.OrdinalIgnoreCase)
                                 && e.EffectiveDate.Date <= termDate
                                 && (e.TerminationDate is null || e.TerminationDate.Value.Date > termDate)).ToList())
                    {
                        await TerminateCoverageAsync(open, termDate, maintenanceReason, ctx);
                    }
                }
                continue;
            }

            await ApplyCoverageAsync(
                memberId, line, planId, groupNumber!, memberMaintenanceType, maintenanceReason,
                memberEffectiveDate, memberTerminationDate, await Existing(), ctx);
        }
    }

    /// <summary>
    /// One HD line. Matching existing coverage = same deterministic coverage
    /// key (member + insurance line + resolved PlanId) with an overlapping
    /// span, so re-importing the same file is a no-op:
    /// <list type="bullet">
    /// <item>021 creates only if no matching coverage exists.</item>
    /// <item>001 updates the matching coverage's plan/level (falling back
    /// to the single open coverage on the same insurance line, for a plan
    /// change), creating only when nothing matches; an HD end date (DTP*349)
    /// is applied as a termination date.</item>
    /// <item>025 reinstates: see <see cref="ApplyReinstatementAsync"/>.</item>
    /// <item>024 (on the HD or the member) sets the matching coverage's
    /// termination date — it never creates coverage.</item>
    /// </list>
    /// </summary>
    private async Task ApplyCoverageAsync(
        string memberId,
        CoverageDetail line,
        string planId,
        string groupNumber,
        string? memberMaintenanceType,
        string? maintenanceReason,
        string? memberEffectiveDate,
        string? memberTerminationDate,
        List<CoverageRecordDto> existing,
        BatchContext ctx)
    {
        var maintenanceType = memberMaintenanceType == "024"
            ? "024"
            : line.MaintenanceType ?? memberMaintenanceType;
        var begin = ParseDate(line.BenefitBeginDate) ?? ParseDate(memberEffectiveDate);
        var end = ParseDate(line.BenefitEndDate);
        var key = BuildCoverageKey(memberId, line.InsuranceLineCode, planId);

        var match = existing
            .Where(e => BuildCoverageKey(memberId, e.InsuranceLineCode, e.PlanId) == key && Overlaps(e, begin, end))
            .OrderByDescending(e => e.EffectiveDate)
            .FirstOrDefault();

        switch (maintenanceType)
        {
            case "021":
                if (match is not null)
                {
                    _logger.LogInformation(
                        "Coverage {CoverageKey} already on file for member {MemberId}; addition is a no-op",
                        key, SanitizeForLog(memberId));
                    return;
                }
                await CreateCoverageAsync(memberId, line, planId, groupNumber, begin, end, "021", existing, ctx);
                return;

            case "025":
                await ApplyReinstatementAsync(
                    memberId, line, planId, groupNumber, maintenanceReason, begin, end, key, match, existing, ctx);
                return;

            case "001":
                if (match is null || string.IsNullOrEmpty(match.Id))
                {
                    // A plan change sent as 001: the one open coverage on the
                    // same insurance line is the one being changed.
                    var sameLine = existing
                        .Where(e => !string.IsNullOrEmpty(e.Id)
                                    && string.Equals(e.InsuranceLineCode, line.InsuranceLineCode, StringComparison.OrdinalIgnoreCase)
                                    && Overlaps(e, begin, end)
                                    && (e.TerminationDate is null || e.TerminationDate.Value.Date >= DateTime.UtcNow.Date))
                        .ToList();
                    match = match is null && sameLine.Count == 1 ? sameLine[0] : match;
                }
                if (match is null)
                {
                    await CreateCoverageAsync(memberId, line, planId, groupNumber, begin, end, maintenanceType, existing, ctx);
                    return;
                }
                if (string.IsNullOrEmpty(match.Id))
                {
                    return; // created earlier in this same pass
                }

                var level = line.CoverageLevel ?? match.CoverageLevel;
                if (!string.Equals(match.PlanId, planId, StringComparison.Ordinal)
                    || !string.Equals(match.CoverageLevel, level, StringComparison.Ordinal))
                {
                    await _coverageClient.UpdateAsync(ctx.TenantId, match.Id, new UpdateCoverageRequestDto
                    {
                        PlanId = planId,
                        CoverageLevel = level
                    });
                    match.PlanId = planId;
                    match.CoverageLevel = level;
                    ctx.Result.CoverageRecordsUpdated++;
                }
                if (end is not null && match.TerminationDate?.Date != end.Value.Date)
                {
                    await TerminateCoverageAsync(match, end.Value, maintenanceReason, ctx);
                }
                return;

            case "024":
                if (match is null || string.IsNullOrEmpty(match.Id))
                {
                    _logger.LogWarning(
                        "No matching coverage {CoverageKey} on file for member {MemberId}; termination not applied",
                        key, SanitizeForLog(memberId));
                    return;
                }
                var terminationDate = end ?? ParseDate(memberTerminationDate) ?? DateTime.UtcNow.Date;
                if (match.TerminationDate?.Date == terminationDate.Date)
                {
                    return; // already terminated as of this date — replay
                }
                await TerminateCoverageAsync(match, terminationDate, maintenanceReason, ctx);
                return;

            default:
                _logger.LogWarning(
                    "Unsupported coverage maintenance type {MaintenanceType} for member {MemberId}; skipped",
                    SanitizeForLog(maintenanceType), SanitizeForLog(memberId));
                return;
        }
    }

    /// <summary>
    /// 834 INS03/HD01 = 025 (reinstatement). A reinstatement reverses a
    /// termination as if it had not happened, so when it continues a
    /// terminated coverage — no DTP*348, or a begin date on or before the day
    /// after that coverage's termination date — the same coverage record is
    /// reinstated in coverage-service (termination date cleared, status back
    /// to Active; its original effective date keeps the span unbroken). When
    /// the 025's DTP*348 begins after a gap, the gap was genuinely uncovered,
    /// and reinstating the old record would make it eligible: a new coverage
    /// span is created from that date instead. An HD end date (DTP*349) is
    /// then applied as the termination date, as for a change. Plan/level are
    /// updated as for a change. Replaying the file changes nothing: the
    /// reinstated coverage is open (or already ends on DTP*349), and a span
    /// created after a gap is found by the overlap match.
    /// </summary>
    private async Task ApplyReinstatementAsync(
        string memberId,
        CoverageDetail line,
        string planId,
        string groupNumber,
        string? maintenanceReason,
        DateTime? begin,
        DateTime? end,
        string key,
        CoverageRecordDto? match,
        List<CoverageRecordDto> existing,
        BatchContext ctx)
    {
        if (match is null)
        {
            // Nothing overlaps the reinstated span; the coverage it continues
            // is the latest same-key one ending before it.
            var previous = existing
                .Where(e => !string.IsNullOrEmpty(e.Id)
                            && BuildCoverageKey(memberId, e.InsuranceLineCode, e.PlanId) == key
                            && e.TerminationDate is not null)
                .OrderByDescending(e => e.EffectiveDate)
                .FirstOrDefault();

            if (previous is null || (begin is not null && begin.Value.Date > previous.TerminationDate!.Value.Date.AddDays(1)))
            {
                if (previous is not null)
                {
                    _logger.LogInformation(
                        "Reinstatement {CoverageKey} for member {MemberId} begins {Begin:yyyy-MM-dd}, after a gap since {Termination:yyyy-MM-dd}; creating a new span",
                        key, SanitizeForLog(memberId), begin, previous.TerminationDate);
                }
                await CreateCoverageAsync(memberId, line, planId, groupNumber, begin, end, "025", existing, ctx);
                return;
            }
            match = previous;
        }

        if (string.IsNullOrEmpty(match.Id))
        {
            return; // created earlier in this same pass
        }

        var level = line.CoverageLevel ?? match.CoverageLevel;
        if (!string.Equals(match.PlanId, planId, StringComparison.Ordinal)
            || !string.Equals(match.CoverageLevel, level, StringComparison.Ordinal))
        {
            await _coverageClient.UpdateAsync(ctx.TenantId, match.Id, new UpdateCoverageRequestDto
            {
                PlanId = planId,
                CoverageLevel = level
            });
            match.PlanId = planId;
            match.CoverageLevel = level;
            ctx.Result.CoverageRecordsUpdated++;
        }

        if (end is not null)
        {
            // Reinstated through DTP*349: moving the termination date is the
            // whole change (coverage-service derives the status from it).
            if (match.TerminationDate?.Date != end.Value.Date)
            {
                await TerminateCoverageAsync(match, end.Value, maintenanceReason, ctx);
            }
            return;
        }

        if (match.TerminationDate is not null)
        {
            await _coverageClient.ReinstateAsync(ctx.TenantId, match.Id, maintenanceReason);
            match.TerminationDate = null;
            ctx.Result.CoverageRecordsReinstated++;
        }
    }

    private async Task CreateCoverageAsync(
        string memberId, CoverageDetail line, string planId, string groupNumber, DateTime? begin, DateTime? end,
        string? maintenanceType, List<CoverageRecordDto> existing, BatchContext ctx)
    {
        var request = new CreateCoverageRequestDto
        {
            MemberId = memberId,
            GroupNumber = groupNumber,
            PlanId = planId,
            InsuranceLineCode = line.InsuranceLineCode,
            CoverageLevel = line.CoverageLevel ?? "EMP",
            // Loop 2300 DTP*348 is this coverage's own benefit begin; the
            // member-level date is only a fallback when the file omits it.
            EffectiveDate = begin ?? DateTime.UtcNow,
            TerminationDate = end,
            MaintenanceTypeCode = maintenanceType
        };
        await _coverageClient.CreateAsync(ctx.TenantId, request);
        ctx.Result.CoverageRecordsCreated++;

        // Visible to later lines in this same pass (no id: coverage-service
        // assigns it), so a duplicate HD in one file doesn't double-create.
        existing.Add(new CoverageRecordDto
        {
            MemberId = memberId,
            GroupNumber = groupNumber,
            PlanId = planId,
            InsuranceLineCode = line.InsuranceLineCode,
            CoverageLevel = request.CoverageLevel,
            EffectiveDate = request.EffectiveDate,
            TerminationDate = end
        });
    }

    private async Task TerminateCoverageAsync(
        CoverageRecordDto coverage, DateTime terminationDate, string? reasonCode, BatchContext ctx)
    {
        await _coverageClient.TerminateAsync(ctx.TenantId, coverage.Id, terminationDate.Date, reasonCode);
        coverage.TerminationDate = terminationDate.Date;
        ctx.Result.CoverageRecordsTerminated++;
    }

    private static bool Overlaps(CoverageRecordDto e, DateTime? begin, DateTime? end) =>
        e.EffectiveDate.Date <= (end ?? DateTime.MaxValue).Date
        && (e.TerminationDate ?? DateTime.MaxValue).Date >= (begin ?? DateTime.MinValue).Date;

    /// <summary>
    /// Deterministic coverage key — same approach as
    /// <see cref="BuildDependentMemberId"/>: a hash of member + insurance
    /// line + resolved PlanId. Coverage level is deliberately excluded so a
    /// 001 level change (EMP→FAM) updates the coverage instead of adding a
    /// second one.
    /// </summary>
    public static string BuildCoverageKey(string memberId, string? insuranceLineCode, string planId)
    {
        var key = $"{memberId}|{(insuranceLineCode ?? string.Empty).Trim().ToUpperInvariant()}|{planId}";
        return $"COV-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0, 6)}";
    }

    /// <summary>
    /// Resolves the 834's own plan code (HD04) to benefit-plan-service's PlanId
    /// via the plan-code-mapping crosswalk. Returns null — without touching
    /// Coverage — when there's no group number/plan code to resolve with, or
    /// no mapping exists yet; the caller surfaces this as
    /// <see cref="ImportResult.CoverageMappingsUnresolved"/> rather than
    /// silently defaulting the PlanId, which just hid the same gap downstream.
    /// </summary>
    private async Task<string?> ResolvePlanIdAsync(
        string memberId, string tenantId, CoverageDetail coverageDetail, string? groupNumber)
    {
        var externalPlanCode = coverageDetail.PlanCoverageDescription;
        if (string.IsNullOrWhiteSpace(externalPlanCode) || string.IsNullOrWhiteSpace(groupNumber))
        {
            _logger.LogWarning(
                "Coverage for member {MemberId} is missing group number or plan code (HD04); cannot resolve PlanId",
                SanitizeForLog(memberId));
            return null;
        }

        var planId = await _benefitPlanClient.ResolvePlanIdAsync(
            tenantId, groupNumber, coverageDetail.InsuranceLineCode, externalPlanCode);
        if (planId is null)
        {
            _logger.LogWarning(
                "No plan-code mapping for group {GroupNumber} line {InsuranceLineCode} code {ExternalCode}; skipping coverage for {MemberId}",
                SanitizeForLog(groupNumber), SanitizeForLog(coverageDetail.InsuranceLineCode),
                SanitizeForLog(externalPlanCode), SanitizeForLog(memberId));
        }
        return planId;
    }

    /// <summary>
    /// Applies one dependent's own maintenance (its INS03, inheriting the
    /// subscriber's only when a JSON caller didn't send one) to the
    /// dependent's own member record, then reconciles its coverage. Returns
    /// false when the member record needed no change (already added, not
    /// found for termination, unknown type).
    /// </summary>
    private async Task<bool> ProcessDependentAsync(
        Dependent dependent, string dependentMemberId, string? maintenanceType, string subscriberMemberId,
        MemberEnrollment? subscriber, BatchContext ctx)
    {
        var tenantId = ctx.TenantId;
        var result = ctx.Result;
        var groupNumber = dependent.GroupNumber ?? subscriber?.GroupNumber;
        var exists = await _memberClient.ExistsAsync(tenantId, dependentMemberId);
        bool applied;

        switch (maintenanceType)
        {
            case "021": // Addition
                if (exists)
                {
                    // Replay: leave the member alone, still reconcile coverage
                    // (idempotent by coverage key).
                    _logger.LogWarning("Dependent {DependentId} already exists, skipping addition",
                        SanitizeForLog(dependentMemberId));
                    applied = false;
                    break;
                }
                await CreateDependentAsync(dependentMemberId, dependent, tenantId, subscriberMemberId, groupNumber);
                result.DependentsCreated++;
                applied = true;
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
                applied = true;
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
                applied = true;
                break;

            default:
                _logger.LogWarning("Unknown maintenance type {MaintenanceType} for dependent {DependentId}",
                    SanitizeForLog(maintenanceType), SanitizeForLog(dependentMemberId));
                return false;
        }

        await ApplyMemberCoverageAsync(
            dependentMemberId, dependent.Coverage, maintenanceType,
            dependent.MaintenanceReason ?? subscriber?.MaintenanceReason,
            dependent.EnrollmentDate ?? subscriber?.EnrollmentDate,
            dependent.TerminationDate ?? subscriber?.TerminationDate,
            groupNumber, ctx);
        return applied;
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

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value.Substring(0, maxLength);

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
    public int CoverageRecordsUpdated { get; set; }
    public int CoverageRecordsReinstated { get; set; }
    public int CoverageRecordsTerminated { get; set; }
    public int CoverageMappingsUnresolved { get; set; }
    public List<string> Errors { get; set; } = new();
}
