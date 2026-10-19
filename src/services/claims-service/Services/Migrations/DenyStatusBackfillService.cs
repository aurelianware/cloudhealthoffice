using ClaimsService.Models;
using ClaimsService.Repositories;
using CloudHealthOffice.Infrastructure.Logging;

namespace ClaimsService.Services.Migrations;

/// <summary>Settings of the Deny status backfill (section <c>Claims:DenyStatusBackfill</c>).</summary>
public sealed class DenyStatusBackfillOptions
{
    public const string SectionName = "Claims:DenyStatusBackfill";

    /// <summary>Opt-in: the endpoint answers 503 until this is true.</summary>
    public bool Enabled { get; set; }
}

/// <summary>A Deny backfill run for the request's tenant.</summary>
public sealed class DenyStatusBackfillRequest
{
    /// <summary>Default true: list what would move, write nothing.</summary>
    public bool DryRun { get; set; } = true;

    /// <summary>At most this many candidates per run (1..5000, default 500). Rerun until none are left.</summary>
    public int Limit { get; set; } = 500;

    /// <summary>
    /// Claim ids payment-service already remitted in an 835 (non-reversal
    /// <c>EraEnvelopes.ClaimIds</c>, see the runbook). They stay Denied:
    /// their provider was told. Claims-service cannot see that itself.
    /// </summary>
    public List<string> RemittedClaimIds { get; set; } = new();
}

public sealed class DenyStatusBackfillResult
{
    public bool DryRun { get; init; }
    public string TenantId { get; init; } = string.Empty;
    /// <summary>Denied claims with no payment-cycle evidence found by this run.</summary>
    public int Candidates { get; set; }
    /// <summary>Candidates moved (or, in a dry run, that would move) to Deny.</summary>
    public List<string> Reopened { get; } = new();
    /// <summary>Candidates left Denied because payment-service remitted them.</summary>
    public List<string> SkippedRemitted { get; } = new();
    /// <summary>Candidates the conditional write no longer matched (changed meanwhile).</summary>
    public List<string> SkippedChanged { get; } = new();
}

/// <summary>
/// One-time, idempotent backfill for the Pay/Deny lifecycle: claims the
/// pipeline set <see cref="ClaimStatus.Denied"/> at adjudication, before
/// <see cref="ClaimStatus.Deny"/> existed, and that were never remitted, move
/// to <see cref="ClaimStatus.Deny"/> (VersionState Adjudicated): editable
/// until the payment run remits and finalizes them. See
/// docs/operations/CLAIM-DENY-STATUS-BACKFILL.md.
///
/// <para>
/// "Never remitted" is not provable from claims-service: before this change
/// the payment run remitted denials without writing back, so a Denied row
/// carries no check / 835 trace either way. The run is therefore opt-in
/// (<see cref="DenyStatusBackfillOptions.Enabled"/>), dry-run by default, and
/// takes payment-service's remitted claim ids to exclude. A claim moved by
/// mistake is not re-remitted: the payment run only finalizes a Deny claim
/// already in an 835.
/// </para>
/// </summary>
public sealed class DenyStatusBackfillService
{
    public const int MaxLimit = 5000;

    private readonly IClaimRepository _claims;
    private readonly ILogger<DenyStatusBackfillService> _logger;

    public DenyStatusBackfillService(IClaimRepository claims, ILogger<DenyStatusBackfillService> logger)
    {
        _claims = claims;
        _logger = logger;
    }

    public async Task<DenyStatusBackfillResult> RunAsync(
        string tenantId, DenyStatusBackfillRequest request, string? actor, CancellationToken ct)
    {
        var limit = Math.Clamp(request.Limit, 1, MaxLimit);
        var remitted = new HashSet<string>(request.RemittedClaimIds ?? [], StringComparer.Ordinal);
        var candidates = await _claims.FindLegacyUnremittedDeniedAsync(limit, ct);
        var result = new DenyStatusBackfillResult { DryRun = request.DryRun, TenantId = tenantId, Candidates = candidates.Count };

        foreach (var claim in candidates)
        {
            if (remitted.Contains(claim.Id))
            {
                result.SkippedRemitted.Add(claim.Id);
                continue;
            }
            if (request.DryRun)
            {
                result.Reopened.Add(claim.Id);
                continue;
            }
            if (await _claims.TryReopenLegacyDeniedAsync(tenantId, claim.Id, ct))
            {
                result.Reopened.Add(claim.Id);
                _logger.LogInformation(
                    "AUDIT deny-backfill: claim {ClaimId} (tenant {TenantId}) Denied -> Deny by {Actor}",
                    LogSanitizer.Sanitize(claim.Id), LogSanitizer.Sanitize(tenantId), LogSanitizer.Sanitize(actor));
            }
            else
            {
                result.SkippedChanged.Add(claim.Id);
            }
        }

        _logger.LogInformation(
            "Deny backfill (tenant {TenantId}, dryRun {DryRun}) by {Actor}: {Candidates} candidate(s), {Reopened} {Verb}, " +
            "{Remitted} left Denied as remitted, {Changed} changed meanwhile",
            LogSanitizer.Sanitize(tenantId), request.DryRun, LogSanitizer.Sanitize(actor), result.Candidates, result.Reopened.Count,
            request.DryRun ? "would move" : "moved", result.SkippedRemitted.Count, result.SkippedChanged.Count);
        return result;
    }
}
