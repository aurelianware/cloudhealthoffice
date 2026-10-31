using AuthorizationService.Exceptions;
using AuthorizationService.Models;
using AuthorizationService.Repositories;
using CloudHealthOffice.Infrastructure.Data;
using CloudHealthOffice.Infrastructure.Logging;

namespace AuthorizationService.Import;

/// <summary>
/// Loads authorizations another system decided (the plan's UM platform or
/// core) into CHO, so the CMS-0057-F APIs (Patient Access, Provider Access,
/// Payer-to-Payer and the Prior Authorization API's status) serve them. CHO
/// does not review these; it stores the source's answer, read-only.
/// </summary>
public interface IAuthorizationImportService
{
    Task<AuthorizationImportResult> ImportCsvAsync(
        string tenantId, string content, AuthorizationImportOptions options, string? actorId, CancellationToken ct = default);
}

/// <summary>
/// <inheritdoc cref="IAuthorizationImportService"/>
///
/// <para><b>The source's authorization number is the identity.</b> It is
/// what providers and members quote, and what the PDex EOB id is built from
/// (<c>pa-{number}</c>). A number that belongs to an authorization CHO
/// received or decided itself is refused: an import never overwrites native
/// work.</para>
///
/// <para><b>Reloads are idempotent.</b> Each authorization's source rows are
/// hashed. The same hash changes nothing; a different hash (a decision came
/// in, dates were extended, a line was corrected) replaces the content and
/// appends a status-history entry when the status moved, so the history shows
/// what the source said and when CHO learned it.</para>
/// </summary>
public sealed class AuthorizationImportService : IAuthorizationImportService
{
    /// <summary>The status-history source and actor on imported changes.</summary>
    public const string HistorySource = "import";

    private readonly IAuthorizationRepository _repository;
    private readonly ILogger<AuthorizationImportService> _logger;
    private readonly TimeProvider _clock;

    public AuthorizationImportService(
        IAuthorizationRepository repository, ILogger<AuthorizationImportService> logger, TimeProvider? clock = null)
    {
        _repository = repository;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<AuthorizationImportResult> ImportCsvAsync(
        string tenantId, string content, AuthorizationImportOptions options, string? actorId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(options);

        var parsed = AuthorizationCsvReader.Read(content, options);
        var result = NewResult(AuthorizationCsvReader.Format, options);
        result.Delimiter = parsed.Delimiter == '\t' ? "tab" : parsed.Delimiter.ToString();
        result.RowsRead = parsed.RowsRead;
        result.UnrecognizedColumns = parsed.UnrecognizedColumns;
        result.FileErrors.AddRange(parsed.FileErrors);

        await StoreAsync(tenantId, AuthorizationCsvReader.Format, parsed.Candidates, options, actorId, result, ct);
        return result;
    }

    private AuthorizationImportResult NewResult(string format, AuthorizationImportOptions options) => new()
    {
        BatchId = Guid.NewGuid().ToString("N"),
        Format = format,
        FileName = options.FileName,
        SourceSystem = options.SourceSystem,
        DryRun = options.DryRun,
        StartedAt = _clock.GetUtcNow().UtcDateTime,
        ExtractedAt = options.ExtractedAt,
    };

    private async Task StoreAsync(
        string tenantId, string format, IReadOnlyList<AuthorizationImportCandidate> candidates,
        AuthorizationImportOptions options, string? actorId, AuthorizationImportResult result, CancellationToken ct)
    {
        result.AuthorizationsInFile = candidates.Count;
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var item = new AuthorizationImportItem
            {
                AuthorizationNumber = candidate.AuthorizationNumber,
                MemberId = candidate.SourceMemberId,
                FileLines = candidate.FileLines,
                Errors = candidate.Errors,
                Warnings = candidate.Warnings,
            };

            if (candidate.Authorization is null || candidate.Errors.Count > 0)
                item.Outcome = AuthorizationImportOutcome.Rejected;
            else
                await StoreOneAsync(tenantId, format, candidate, candidate.Authorization, options, actorId, result, item);

            result.Authorizations.Add(item);
            switch (item.Outcome)
            {
                case AuthorizationImportOutcome.Created: result.Created++; break;
                case AuthorizationImportOutcome.Updated: result.Updated++; break;
                case AuthorizationImportOutcome.Unchanged: result.Unchanged++; break;
                default: result.Rejected++; break;
            }
        }

        result.CompletedAt = _clock.GetUtcNow().UtcDateTime;
        _logger.LogInformation(
            "Authorization import {BatchId} ({Format}) for tenant {TenantId}: {Count} authorizations, {Created} created, " +
            "{Updated} updated, {Unchanged} unchanged, {Rejected} rejected (dry run: {DryRun})",
            result.BatchId, format, LogSanitizer.Sanitize(tenantId), result.AuthorizationsInFile, result.Created,
            result.Updated, result.Unchanged, result.Rejected, result.DryRun);
    }

    private async Task StoreOneAsync(
        string tenantId, string format, AuthorizationImportCandidate candidate, Authorization incoming,
        AuthorizationImportOptions options, string? actorId, AuthorizationImportResult result, AuthorizationImportItem item)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var actor = actorId ?? HistorySource;
        item.Status = incoming.Status.ToString();

        var existing = await _repository.GetByAuthorizationNumberAsync(candidate.AuthorizationNumber);
        if (existing is not null && existing.ExternalSource is null)
        {
            item.Outcome = AuthorizationImportOutcome.Rejected;
            item.Errors.Add(
                "CHO already holds this authorization number for an authorization it received or decided itself; " +
                "an import never replaces native work.");
            return;
        }

        if (existing is not null && existing.ExternalSource!.ContentHash == candidate.ContentHash)
        {
            item.Outcome = AuthorizationImportOutcome.Unchanged;
            item.ChoAuthorizationId = existing.Id;
            return;
        }

        incoming.TenantId = tenantId;
        incoming.ExternalSource = new AuthorizationExternalSource
        {
            Format = format,
            SourceSystem = options.SourceSystem,
            SourceMemberId = candidate.SourceMemberId,
            ContentHash = candidate.ContentHash,
            ImportBatchId = result.BatchId,
            FileName = options.FileName,
            FirstImportedAt = existing?.ExternalSource?.FirstImportedAt ?? now,
            ImportedAt = now,
            ExtractedAt = options.ExtractedAt,
        };
        incoming.LastUpdatedDate = now;
        incoming.LastUpdatedBy = actor;

        if (existing is null)
        {
            incoming.Id = ImportedId(tenantId, candidate.AuthorizationNumber);
            incoming.CreatedDate = now;
            incoming.CreatedBy = actor;
            incoming.StatusHistory = InitialHistory(incoming, now);
            item.ChoAuthorizationId = incoming.Id;
            item.Outcome = AuthorizationImportOutcome.Created;
            if (options.DryRun)
                return;
            try
            {
                await _repository.CreateAsync(incoming);
            }
            catch (DuplicateAuthorizationNumberException)
            {
                item.Outcome = AuthorizationImportOutcome.Rejected;
                item.Errors.Add("Another write created this authorization number at the same time; reload the file.");
            }
            return;
        }

        incoming.Id = existing.Id;
        incoming.Revision = existing.Revision;
        incoming.CreatedDate = existing.CreatedDate;
        incoming.CreatedBy = existing.CreatedBy;
        incoming.ReportingLedger = existing.ReportingLedger;
        incoming.DecisionReceivedAt = null;
        incoming.DecisionDueAt = null;
        incoming.DecisionDeadlinePolicy = null;
        incoming.StatusHistory = [.. existing.StatusHistory ?? []];
        if (existing.Status != incoming.Status || existing.ReviewDecision != incoming.ReviewDecision)
            incoming.StatusHistory.Add(Change(incoming, incoming.ReviewedDate ?? now, now));

        item.ChoAuthorizationId = existing.Id;
        item.Outcome = AuthorizationImportOutcome.Updated;
        if (options.DryRun)
            return;
        try
        {
            await _repository.UpdateAsync(incoming);
        }
        catch (ConcurrencyConflictException)
        {
            item.Outcome = AuthorizationImportOutcome.Rejected;
            item.Errors.Add("The authorization changed while it was being loaded; reload the file.");
        }
    }

    /// <summary>
    /// Received, then the decision when there is one: what the source's
    /// dates say happened, so decision timeframes report from the source's
    /// own receipt and decision times.
    /// </summary>
    private static List<AuthorizationStatusChange> InitialHistory(Authorization authorization, DateTime now)
    {
        var history = new List<AuthorizationStatusChange>
        {
            new()
            {
                Status = AuthorizationStatus.Submitted, ChangedAt = authorization.SubmittedDate,
                ChangedBy = HistorySource, Source = HistorySource, Reason = "Received (as reported by the source system).",
            },
        };
        if (authorization.Status != AuthorizationStatus.Submitted)
            history.Add(Change(authorization, authorization.ReviewedDate ?? now, now));
        return history;
    }

    private static AuthorizationStatusChange Change(Authorization authorization, DateTime at, DateTime now) => new()
    {
        Status = authorization.Status,
        ReviewDecision = authorization.ReviewDecision,
        ChangedAt = at > now ? now : at,
        ChangedBy = HistorySource,
        Source = HistorySource,
        Reason = authorization.DenialReason ?? "As reported by the source system.",
    };

    /// <summary>
    /// The CHO id of an imported authorization: deterministic, so rebuilding
    /// the store gives every authorization the id it had.
    /// </summary>
    public static string ImportedId(string tenantId, string authorizationNumber)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{tenantId}\n{authorizationNumber}"));
        return "imp-" + Convert.ToHexString(hash).ToLowerInvariant()[..32];
    }
}
