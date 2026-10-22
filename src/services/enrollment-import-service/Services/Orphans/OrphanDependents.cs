using CloudHealthOffice.Infrastructure.Logging;
using EnrollmentImportService.Clients;
using EnrollmentImportService.Models;
using EnrollmentImportService.Repositories;
using EnrollmentImportService.Services.Settings;

namespace EnrollmentImportService.Services.Orphans;

/// <summary>
/// Dependents whose subscriber is not on file (enrollment-operations.md §10,
/// decision 5): held for a grace period, retried when the subscriber arrives,
/// and opened to the Matching queue when the grace period ends.
/// </summary>
public static class OrphanDependents
{
    /// <summary>The actor every automatic action is recorded under.</summary>
    public const string Actor = "system:orphan-retry";

    public const string AutoHeldAction = "AutoHeld";
    public const string AutoRetriedAction = "AutoRetried";
    public const string GraceEndedAction = "GraceEnded";

    /// <summary>An exception the orphan rules apply to: a dependent rejected because its subscriber is not on file.</summary>
    public static bool IsOrphan(EnrollmentException e) =>
        e.SourceKind == EnrollmentExceptionSourceKind.Dependent
        && e.ReasonCode == EnrollmentRejectionReasons.SubscriberNotFound;
}

public interface IOrphanGracePolicy
{
    /// <summary>
    /// The grace period for an orphaned dependent, through the settings chain
    /// (line of business → benefit plan → sponsor). Never throws: when the
    /// sponsor or plan cannot be looked up, those levels are skipped, and an
    /// unknown line of business has no default (0 days, so the exception opens).
    /// </summary>
    Task<ResolvedEnrollmentSetting> ResolveAsync(string tenantId, Dependent dependent, CancellationToken ct = default);
}

public sealed class OrphanGracePolicy(
    ISponsorServiceClient sponsors,
    IBenefitPlanServiceClient plans,
    EnrollmentSettingsResolver settings,
    ILogger<OrphanGracePolicy> logger) : IOrphanGracePolicy
{
    public async Task<ResolvedEnrollmentSetting> ResolveAsync(string tenantId, Dependent dependent, CancellationToken ct = default)
    {
        var group = string.IsNullOrWhiteSpace(dependent.GroupNumber) ? null : dependent.GroupNumber.Trim();
        string? lineOfBusiness = null;
        string? planId = null;
        if (group is not null)
        {
            try
            {
                lineOfBusiness = LinesOfBusiness.Normalize(await sponsors.GetLineOfBusinessAsync(tenantId, group, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not read the line of business for group {GroupNumber}; resolving the orphan grace period without it",
                    LogSanitizer.Sanitize(group));
            }

            var coverage = dependent.Coverage?.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.PlanCoverageDescription));
            if (coverage is not null)
            {
                try
                {
                    planId = await plans.ResolvePlanIdAsync(tenantId, group, coverage.InsuranceLineCode, coverage.PlanCoverageDescription!, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Could not resolve the benefit plan for group {GroupNumber}; resolving the orphan grace period without it",
                        LogSanitizer.Sanitize(group));
                }
            }
        }

        try
        {
            return await settings.ResolveAsync(tenantId, EnrollmentSettingKeys.OrphanGraceDays, lineOfBusiness, planId, group, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Open rather than hold when the settings cannot be read: a person sees it.
            logger.LogWarning(ex, "Could not read enrollment settings; the orphaned dependent opens without a grace period");
            return new ResolvedEnrollmentSetting(EnrollmentSettingKeys.OrphanGraceDays, 0, "Unavailable", null);
        }
    }
}

/// <summary>
/// Reprocesses orphaned dependents whose subscriber has now arrived. Run after
/// an import (never inside it): a failure is logged and does not touch the
/// import that triggered it.
/// </summary>
public sealed class OrphanDependentRetrier(
    IEnrollmentExceptionRepository exceptions,
    IServiceProvider services,
    ILogger<OrphanDependentRetrier> logger)
{
    /// <summary>
    /// For each subscriber that is now on file, reprocesses that tenant's held
    /// or open orphans naming it. Returns how many resolved.
    /// </summary>
    public async Task<int> RetryForSubscribersAsync(string tenantId, IReadOnlyCollection<string> subscriberIds, string? arrivedIn, CancellationToken ct = default)
    {
        var resolved = 0;
        foreach (var subscriberId in subscriberIds.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal))
        {
            IReadOnlyList<EnrollmentException> orphans;
            try
            {
                orphans = await exceptions.ListOrphansForSubscriberAsync(tenantId, subscriberId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not look up orphaned dependents for subscriber {SubscriberId}", LogSanitizer.MaskId(subscriberId));
                continue;
            }
            foreach (var orphan in orphans)
            {
                var note = string.IsNullOrWhiteSpace(arrivedIn)
                    ? "Automatic retry: the subscriber is now on file."
                    : $"Automatic retry: the subscriber arrived in {arrivedIn}.";
                if (await RetryAsync(tenantId, orphan.Id, note, ct)) resolved++;
            }
        }
        return resolved;
    }

    /// <summary>Retries one orphan; true when it resolved. Never throws (except cancellation).</summary>
    public async Task<bool> RetryAsync(string tenantId, string exceptionId, string note, CancellationToken ct = default)
    {
        try
        {
            var service = services.GetRequiredService<EnrollmentExceptionService>();
            var result = await service.AutoRetryOrphanAsync(tenantId, exceptionId, note, ct);
            return result.Outcome == ExceptionActionOutcome.Done
                   && result.Exception?.Status == EnrollmentExceptionStatus.Resolved;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Automatic retry of enrollment exception {ExceptionId} failed", LogSanitizer.Sanitize(exceptionId));
            return false;
        }
    }
}

/// <summary>
/// The import, followed by the orphan retry for every subscriber the import
/// put on file. Registered as <see cref="IEnrollmentImportService"/> so every
/// path (import units, manual enrollment, a specialist's reprocess) triggers it.
/// </summary>
public sealed class OrphanRetryingImportService(
    global::EnrollmentImportService.Services.EnrollmentImportService inner,
    OrphanDependentRetrier retrier,
    ILogger<OrphanRetryingImportService> logger) : IEnrollmentImportService
{
    public async Task<ImportResult> ImportEnrollmentAsync(Enrollment834 enrollment, string tenantId)
    {
        var result = await inner.ImportEnrollmentAsync(enrollment, tenantId);
        if (result.SubscribersOnFile.Count == 0) return result;
        try
        {
            var arrivedIn = !string.IsNullOrWhiteSpace(enrollment.FileName) ? enrollment.FileName : enrollment.BatchId;
            await retrier.RetryForSubscribersAsync(tenantId, result.SubscribersOnFile, arrivedIn);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Orphaned-dependent retry after import {BatchId} failed; the import itself completed",
                LogSanitizer.Sanitize(result.BatchId));
        }
        return result;
    }
}
