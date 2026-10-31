using System.Security.Cryptography;
using System.Text;
using ClaimsService.Models;
using ClaimsService.Repositories;
using CloudHealthOffice.Infrastructure.Logging;

namespace ClaimsService.Import.Cpcds;

/// <summary>
/// Loads a CPCDS claim extract from a plan's core admin system (QNXT, Facets,
/// HealthRules, anything that can write the CARIN data set) into CHO's claim
/// store, so the Patient Access and Provider Access APIs serve the plan's
/// adjudicated claims history. CHO does not adjudicate these claims; it
/// stores the core's answer.
/// </summary>
public interface ICpcdsClaimImportService
{
    Task<CpcdsImportResult> ImportAsync(
        string tenantId, string content, CpcdsImportOptions options, string? actorId, CancellationToken ct = default);
}

/// <summary>
/// <inheritdoc cref="ICpcdsClaimImportService"/>
///
/// <para><b>Reloads are idempotent.</b> A core's nightly extract is usually a
/// rolling window that repeats most of yesterday's claims, so loading the same
/// claim twice must change nothing. Each claim's source rows (and the member id
/// they resolved to) are hashed; a claim whose hash matches its current version
/// is left alone. A claim whose content changed (a late adjustment, a corrected
/// member) gets a new version on the same chain, and the old version is
/// superseded, exactly as a CHO adjustment would be. The EOB id is the chain
/// id, so a member's app sees one EOB that changed rather than two.</para>
///
/// <para><b>Chain ids are deterministic</b>
/// (<c>cpcds-</c> + a hash of tenant and source claim id), so the same claim
/// gets the same EOB id however many times the store is rebuilt.</para>
///
/// <para><b>Native claims are never touched.</b> A source claim id that
/// collides with a claim CHO adjudicated itself is rejected.</para>
/// </summary>
public sealed class CpcdsClaimImportService : ICpcdsClaimImportService
{
    public const string Format = "cpcds";

    private readonly IClaimRepository _claims;
    private readonly IClaimImportTransactionRepository _transactions;
    private readonly ICoreMemberCrosswalk _crosswalk;
    private readonly ILogger<CpcdsClaimImportService> _logger;
    private readonly TimeProvider _clock;

    public CpcdsClaimImportService(
        IClaimRepository claims,
        IClaimImportTransactionRepository transactions,
        ICoreMemberCrosswalk crosswalk,
        ILogger<CpcdsClaimImportService> logger,
        TimeProvider? clock = null)
    {
        _claims = claims;
        _transactions = transactions;
        _crosswalk = crosswalk;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<CpcdsImportResult> ImportAsync(
        string tenantId, string content, CpcdsImportOptions options, string? actorId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(options);

        var result = new CpcdsImportResult
        {
            BatchId = Guid.NewGuid().ToString("N"),
            FileName = options.FileName,
            SourceSystem = options.SourceSystem,
            DryRun = options.DryRun,
            StartedAt = _clock.GetUtcNow().UtcDateTime,
            ExtractedAt = options.ExtractedAt,
        };

        var parsed = CpcdsClaimFileParser.Parse(content, options.ColumnMap);
        result.Delimiter = parsed.Delimiter == '\t' ? "tab" : parsed.Delimiter.ToString();
        result.RowsRead = parsed.RowsRead;
        result.UnrecognizedColumns = parsed.UnrecognizedColumns;
        result.FileErrors.AddRange(parsed.FileErrors);
        if (parsed.OrphanRows.Count > 0)
        {
            result.FileErrors.Add(
                $"{parsed.OrphanRows.Count} row(s) have no \"Claim unique identifier\" and were skipped " +
                $"(file lines {string.Join(", ", parsed.OrphanRows.Take(20))}{(parsed.OrphanRows.Count > 20 ? ", ..." : string.Empty)}).");
        }
        result.ClaimsInFile = parsed.Claims.Count;

        foreach (var record in parsed.Claims)
        {
            ct.ThrowIfCancellationRequested();
            var claimResult = await ImportClaimAsync(tenantId, record, options, actorId, result, ct);
            result.Claims.Add(claimResult);

            switch (claimResult.Outcome)
            {
                case CpcdsClaimOutcome.Created: result.Created++; break;
                case CpcdsClaimOutcome.Updated: result.Updated++; break;
                case CpcdsClaimOutcome.Unchanged: result.Unchanged++; break;
                default: result.Rejected++; break;
            }
        }

        result.CompletedAt = _clock.GetUtcNow().UtcDateTime;
        _logger.LogInformation(
            "CPCDS import {BatchId} for tenant {TenantId}: {Claims} claims, {Created} created, {Updated} updated, " +
            "{Unchanged} unchanged, {Rejected} rejected (dry run: {DryRun})",
            result.BatchId, LogSanitizer.Sanitize(tenantId), result.ClaimsInFile, result.Created, result.Updated,
            result.Unchanged, result.Rejected, result.DryRun);

        return result;
    }

    private async Task<CpcdsClaimResult> ImportClaimAsync(
        string tenantId,
        CpcdsClaimRecord record,
        CpcdsImportOptions options,
        string? actorId,
        CpcdsImportResult report,
        CancellationToken ct)
    {
        var claimResult = new CpcdsClaimResult
        {
            ClaimId = record.ClaimId,
            FileLines = record.Rows.Select(r => r.LineNumber).ToList(),
        };

        var sourceMemberId = record.Rows.Select(r => r.Get(CpcdsClaimColumn.MemberId)).FirstOrDefault(v => v is not null);
        string? memberId = null;
        if (sourceMemberId is not null)
        {
            memberId = await _crosswalk.ResolveAsync(tenantId, options.SourceSystem, sourceMemberId, ct);
            if (memberId is null)
            {
                claimResult.Errors.Add(
                    $"Member '{sourceMemberId}' is not in the member crosswalk for source system '{options.SourceSystem ?? "(none)"}'.");
            }
        }
        claimResult.MemberId = memberId;

        var mapped = CpcdsClaimMapper.Map(record, memberId ?? sourceMemberId ?? string.Empty, options, actorId);
        report.SubmittedAmountInFile += mapped.SubmittedAmount ?? 0m;
        report.PaidAmountInFile += mapped.PaidAmount ?? 0m;
        claimResult.Errors.AddRange(mapped.Errors);
        claimResult.Warnings.AddRange(mapped.Warnings);

        if (claimResult.Errors.Count > 0 || mapped.Claim is null)
            return await Rejected(tenantId, claimResult, options, report, ct);

        var claim = mapped.Claim;
        var now = _clock.GetUtcNow().UtcDateTime;
        var hash = ContentHash(record, memberId!, options);

        var existing = await _claims.GetByClaimNumberAsync(record.ClaimId);
        if (existing is not null && existing.ExternalSource is null)
        {
            claimResult.Errors.Add(
                $"Claim number '{record.ClaimId}' belongs to a claim CHO processed itself ({existing.Id}); " +
                "an imported claim cannot replace it.");
            return await Rejected(tenantId, claimResult, options, report, ct);
        }

        if (existing is not null && existing.ExternalSource!.ContentHash == hash)
        {
            claimResult.Outcome = CpcdsClaimOutcome.Unchanged;
            claimResult.ChoClaimId = existing.ClaimVersionId;
            claimResult.VersionNumber = existing.VersionNumber;
            report.PaidAmountLoaded += existing.AdjudicationResult?.PayerPayment ?? 0m;
            if (!options.DryRun)
                await EnsurePredecessorSupersededAsync(tenantId, existing, actorId, ct);
            await RecordAsync(tenantId, claimResult, options, report, ct);
            return claimResult;
        }

        var chainId = existing?.ClaimVersionId is { Length: > 0 } chain ? chain : ChainId(tenantId, record.ClaimId);
        var versionNumber = (existing?.VersionNumber ?? 0) + 1;

        claim.TenantId = tenantId;
        claim.ClaimVersionId = chainId;
        claim.VersionNumber = versionNumber;
        claim.Id = $"{chainId}-v{versionNumber}";
        claim.PredecessorVersionId = existing?.Id;
        claim.PublishedAt = now;
        claim.CreatedDate = now;
        claim.LastUpdatedDate = now;
        claim.ExternalSource = new ClaimExternalSource
        {
            Format = Format,
            SourceSystem = options.SourceSystem,
            SourceClaimId = record.ClaimId,
            SourceMemberId = sourceMemberId,
            ContentHash = hash,
            ImportBatchId = report.BatchId,
            FileName = options.FileName,
            ImportedAt = now,
            ExtractedAt = options.ExtractedAt,
        };

        claimResult.Outcome = existing is null ? CpcdsClaimOutcome.Created : CpcdsClaimOutcome.Updated;
        claimResult.ChoClaimId = chainId;
        claimResult.VersionNumber = versionNumber;

        if (!options.DryRun)
        {
            await _claims.CreateAsync(claim);

            if (existing is not null && existing.SupersededAt is null)
            {
                await _claims.MarkSupersededProjectionAsync(tenantId, existing.Id, claim.Id, now, actorId, ct);
            }

            // The core replaced this claim with another ("Claim adjusted to
            // identifier"): it stays readable by id, but is no longer a
            // current claim, so search does not show the member both.
            var adjustedTo = record.Rows.Select(r => r.Get(CpcdsClaimColumn.ClaimAdjustedToIdentifier)).FirstOrDefault(v => v is not null);
            if (adjustedTo is not null)
            {
                await _claims.MarkSupersededProjectionAsync(
                    tenantId, claim.Id, ChainId(tenantId, adjustedTo), now, actorId, ct);
            }
        }

        report.PaidAmountLoaded += claim.AdjudicationResult?.PayerPayment ?? 0m;
        await RecordAsync(tenantId, claimResult, options, report, ct);
        return claimResult;
    }

    /// <summary>
    /// Heals a reload that was interrupted between writing a new version and
    /// superseding the one before it: without this, both would read as
    /// current until the claim next changed.
    /// </summary>
    private async Task EnsurePredecessorSupersededAsync(string tenantId, Claim head, string? actorId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(head.PredecessorVersionId)) return;

        var predecessor = await _claims.GetVersionAsync(head.ClaimVersionId, head.PredecessorVersionId);
        if (predecessor is not null && predecessor.SupersededAt is null)
        {
            await _claims.MarkSupersededProjectionAsync(
                tenantId, predecessor.Id, head.Id, _clock.GetUtcNow().UtcDateTime, actorId, ct);
        }
    }

    private async Task<CpcdsClaimResult> Rejected(
        string tenantId, CpcdsClaimResult claimResult, CpcdsImportOptions options, CpcdsImportResult report, CancellationToken ct)
    {
        claimResult.Outcome = CpcdsClaimOutcome.Rejected;
        await RecordAsync(tenantId, claimResult, options, report, ct);
        return claimResult;
    }

    /// <summary>
    /// One import transaction per claim, beside the raw-837 ones, so the admin
    /// console's import log shows CPCDS loads too. A failure to record is
    /// logged and does not fail the load.
    /// </summary>
    private async Task RecordAsync(
        string tenantId, CpcdsClaimResult claimResult, CpcdsImportOptions options, CpcdsImportResult report, CancellationToken ct)
    {
        if (options.DryRun) return;
        try
        {
            await _transactions.CreateAsync(new ClaimImportTransaction
            {
                TenantId = tenantId,
                Format = Format,
                ImportBatchId = report.BatchId,
                ClaimNumber = claimResult.ClaimId,
                ClaimId = claimResult.ChoClaimId,
                MemberId = claimResult.MemberId ?? string.Empty,
                FileName = options.FileName,
                ReceivedAt = report.StartedAt,
                Status = claimResult.Outcome == CpcdsClaimOutcome.Rejected ? "Rejected" : "Accepted",
                Outcome = claimResult.Outcome.ToString(),
                Errors = claimResult.Errors,
                SubmitterId = options.SourceSystem,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to record CPCDS import transaction for claim {ClaimNumber}",
                LogSanitizer.Sanitize(claimResult.ClaimId));
        }
    }

    /// <summary>The claim's chain id (and FHIR id): stable for a tenant and source claim id.</summary>
    internal static string ChainId(string tenantId, string sourceClaimId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId}\n{sourceClaimId}"));
        return "cpcds-" + Convert.ToHexString(bytes, 0, 12).ToLowerInvariant();
    }

    /// <summary>
    /// The hash a reload is compared on: every recognized value of every row,
    /// independent of row order and of columns the importer ignores, plus the
    /// member the claim resolved to and how it was loaded.
    /// </summary>
    internal static string ContentHash(CpcdsClaimRecord record, string memberId, CpcdsImportOptions options)
    {
        var rows = record.Rows
            .Select(r => string.Join('\u001f', r.Values
                .Where(v => !string.IsNullOrWhiteSpace(v.Value))
                .Select(v => $"{v.Key}={v.Value.Trim()}")
                .OrderBy(v => v, StringComparer.Ordinal)))
            .OrderBy(r => r, StringComparer.Ordinal);

        var canonical = string.Join('\u001e', rows)
                        + $"\u001dmember={memberId}\u001dlob={options.LineOfBusiness}\u001dsource={options.SourceSystem}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
