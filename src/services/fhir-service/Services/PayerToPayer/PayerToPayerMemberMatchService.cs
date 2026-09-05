using FhirService.Models;
using FhirService.Models.PayerToPayer;
using Microsoft.Extensions.Logging;

namespace FhirService.Services.PayerToPayer;

/// <summary>
/// Application service for the Payer-to-Payer <c>$member-match</c> operation
/// (CMS-0057-F P2P-04). Given the identity attributes a receiving payer holds,
/// Cloud Health Office resolves the same person across payer contexts within a
/// tenant and returns the relevant coverage context — deterministically and
/// fail-safe.
///
/// Order of operations (fail closed):
///   1. tenant scope — a request for an unserved tenant resolves nothing;
///   2. sufficiency — weak criteria are refused before any search (anti-enumeration);
///   3. candidate resolution — every strong candidate must agree on all supplied
///      attributes; zero → no match, more than one → ambiguous (no data);
///   4. coverage selection — pick the single relevant coverage or refuse;
///   5. audit — record the outcome without member demographics.
///
/// This is the domain/application layer — the FHIR controller is a thin routing
/// surface over it, so no matching logic lives in the controller. It does NOT
/// gate on consent: member-match is an identity operation that precedes the data
/// exchange; the P2P-01 respond path enforces the member's opt-in when data is
/// actually returned. That keeps P2P-03 (dedicated consent) independent.
/// </summary>
public interface IPayerToPayerMemberMatchService
{
    Task<MemberMatchResult> MatchAsync(MemberMatchRequest request, CancellationToken ct = default);
}

public sealed class PayerToPayerMemberMatchService : IPayerToPayerMemberMatchService
{
    private readonly IPayerToPayerMemberMatchSource _source;
    private readonly ILogger<PayerToPayerMemberMatchService> _logger;

    public PayerToPayerMemberMatchService(
        IPayerToPayerMemberMatchSource source, ILogger<PayerToPayerMemberMatchService> logger)
    {
        _source = source;
        _logger = logger;
    }

    public async Task<MemberMatchResult> MatchAsync(MemberMatchRequest request, CancellationToken ct = default)
    {
        // The tenant comes from the authenticated context (headers/claims) and may
        // not be trimmed upstream; normalize it here so a stray space cannot cause
        // a false TenantMismatch. It never widens scope — it only matches the
        // already-trimmed configured tenant.
        var tenantId = request.TenantId?.Trim() ?? string.Empty;
        if (!string.Equals(tenantId, _source.ServedTenantId, StringComparison.Ordinal))
            return Failed(request, MemberMatchOutcome.TenantMismatch);

        var criteria = MemberMatchCriteria.From(request);
        if (!criteria.IsSufficient)
            return Failed(request, MemberMatchOutcome.InsufficientCriteria);

        var members = await _source.GetMembersAsync(tenantId, ct);

        // A supplied member/subscriber id is the only criterion that needs a
        // candidate's coverages to decide match strength (subscriber ids live on
        // coverages). For demographic-only or SSN requests the policy decides from
        // the member alone, so coverage retrieval is deferred until a single match
        // is found — no per-candidate N+1 fetch.
        var needsCoveragesForMatch = criteria.MemberId is not null;

        var strong = new List<(ChoMember Member, IReadOnlyList<ChoCoverage> Coverages)>();
        foreach (var member in members)
        {
            var coverages = needsCoveragesForMatch
                ? await _source.GetCoveragesAsync(tenantId, member.MemberId, ct)
                : Array.Empty<ChoCoverage>();
            if (MemberMatchPolicy.Evaluate(criteria, member, coverages) == MemberMatchStrength.Strong)
                strong.Add((member, coverages));

            // Two strong candidates already means the outcome is AmbiguousMatch;
            // stop scanning (and stop fetching coverages) rather than evaluate the rest.
            if (strong.Count > 1) break;
        }

        if (strong.Count == 0) return Failed(request, MemberMatchOutcome.NoMatch);
        if (strong.Count > 1) return Failed(request, MemberMatchOutcome.AmbiguousMatch);

        var (matched, matchedCoverages) = strong[0];
        // Coverage selection always needs the matched member's coverages; fetch
        // them now if the match path above did not already.
        if (!needsCoveragesForMatch)
            matchedCoverages = await _source.GetCoveragesAsync(tenantId, matched.MemberId, ct);

        var selection = PayerToPayerCoverageSelector.Select(matchedCoverages, criteria);
        if (selection.Outcome == CoverageSelectionOutcome.Ambiguous)
            return Failed(request, MemberMatchOutcome.AmbiguousCoverage, matched.MemberId);

        var audit = Audit(request, MemberMatchOutcome.Matched, matched.MemberId, selection.Coverage?.CoverageId);
        _logger.LogInformation(
            "P2P member-match: tenant={Tenant} receivingPayer={Payer} member={Member} coverage={Coverage}",
            Clean(audit.TenantId), Clean(audit.ReceivingPayerId), Clean(audit.MatchedMemberId), Clean(audit.SelectedCoverageId));

        return MemberMatchResult.Matched(matched, selection.Coverage, audit);
    }

    private MemberMatchResult Failed(
        MemberMatchRequest request, MemberMatchOutcome outcome, string? matchedMemberId = null)
    {
        var audit = Audit(request, outcome, matchedMemberId, selectedCoverageId: null);
        _logger.LogInformation(
            "P2P member-match declined: tenant={Tenant} receivingPayer={Payer} outcome={Outcome}",
            Clean(audit.TenantId), Clean(audit.ReceivingPayerId), audit.Outcome);
        return MemberMatchResult.Failure(outcome, audit);
    }

    private static MemberMatchAuditEntry Audit(
        MemberMatchRequest request, MemberMatchOutcome outcome, string? matchedMemberId, string? selectedCoverageId) =>
        new()
        {
            TenantId = request.TenantId,
            ReceivingPayerId = request.ReceivingPayerId,
            InitiatedBy = request.InitiatedBy,
            MatchedMemberId = matchedMemberId,
            SelectedCoverageId = selectedCoverageId,
            Outcome = outcome.ToString(),
        };

    /// <summary>
    /// Strips CR/LF from caller-supplied values before they reach a log entry,
    /// preventing log-forging / injection (CWE-117) from the match request.
    /// </summary>
    private static string Clean(string? value)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("\r", string.Empty, StringComparison.Ordinal)
                   .Replace("\n", string.Empty, StringComparison.Ordinal);
}
