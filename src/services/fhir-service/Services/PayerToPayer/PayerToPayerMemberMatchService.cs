using FhirService.Models;
using FhirService.Models.PayerToPayer;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Infrastructure.Logging;

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
/// surface over it, so no matching logic lives in the controller.
///
/// Consent (D-13, ADR 022): before any member is looked at, the request must
/// carry the member's HRex consent naming the requesting payer (the client's
/// registered payer). A match records it for that payer, so the export that
/// follows is served to that payer only.
/// </summary>
public interface IPayerToPayerMemberMatchService
{
    Task<MemberMatchResult> MatchAsync(MemberMatchRequest request, CancellationToken ct = default);
}

/// <summary>
/// One member of a PDex <c>$bulk-member-match</c>, matched the way the single
/// operation matches but sorted the way the bulk one reports (G-35).
/// </summary>
public interface IPayerToPayerBulkMemberMatcher
{
    /// <summary>
    /// Matched, <see cref="MemberMatchOutcome.ConsentNotAccepted"/> for a member
    /// that matched but whose consent cannot be honoured, or another outcome
    /// for a member not matched.
    /// </summary>
    Task<MemberMatchResult> MatchMemberOfBulkAsync(MemberMatchRequest request, CancellationToken ct = default);
}

public sealed class PayerToPayerMemberMatchService : IPayerToPayerMemberMatchService, IPayerToPayerBulkMemberMatcher
{
    private readonly IPayerToPayerMemberMatchSource _source;
    private readonly IPayerToPayerConsentAttestationStore _attestations;
    private readonly string _localPayerId;
    private readonly TimeProvider _time;
    private readonly ILogger<PayerToPayerMemberMatchService> _logger;

    public PayerToPayerMemberMatchService(
        IPayerToPayerMemberMatchSource source,
        IPayerToPayerConsentAttestationStore attestations,
        Microsoft.Extensions.Options.IOptions<Outbound.PayerToPayerDirectoryOptions> directory,
        TimeProvider time,
        ILogger<PayerToPayerMemberMatchService> logger)
    {
        _source = source;
        _attestations = attestations;
        _localPayerId = directory.Value.LocalPayerId;
        _time = time;
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

        var now = _time.GetUtcNow().UtcDateTime;
        var consent = PdexConsentValidator.Validate(request.Consent, request.ReceivingPayerId, _localPayerId, now);
        if (!consent.Valid)
            return Failed(request, MemberMatchOutcome.ConsentNotAccepted, consentFailure: consent.Failure);

        var criteria = MemberMatchCriteria.From(request);
        if (!criteria.IsSufficient)
            return Failed(request, MemberMatchOutcome.InsufficientCriteria);

        var resolved = await ResolveAsync(tenantId, criteria, ct);
        if (resolved.Outcome != MemberMatchOutcome.Matched)
            return Failed(request, resolved.Outcome, resolved.Member?.MemberId);

        // The consent must be this member's: its patient bears one of the member's
        // identifiers, or agrees on family name and birth date. Otherwise one
        // consent could be replayed for any member. Answered as no match, so a
        // match that a mismatched consent spoiled reveals nothing more.
        if (!NamesMember(consent, resolved))
            return Failed(request, MemberMatchOutcome.NoMatch, consentFailure: PdexConsentFailure.PatientNotBound);

        return await AcceptAsync(request, tenantId, consent, resolved, now, ct);
    }

    /// <summary>
    /// One member of a PDex <c>$bulk-member-match</c>. The bulk operation sorts
    /// each member into matched, not matched, or matched but consent-constrained,
    /// so here the member is resolved first and the consent judged after:
    /// <see cref="MemberMatchOutcome.ConsentNotAccepted"/> means the member
    /// matched and its consent cannot be honoured (unusable, or naming another
    /// person). Every outcome other than Matched and ConsentNotAccepted is a
    /// member not matched. Nothing is recorded unless the consent is accepted.
    /// </summary>
    public async Task<MemberMatchResult> MatchMemberOfBulkAsync(MemberMatchRequest request, CancellationToken ct = default)
    {
        var tenantId = request.TenantId?.Trim() ?? string.Empty;
        if (!string.Equals(tenantId, _source.ServedTenantId, StringComparison.Ordinal))
            return Failed(request, MemberMatchOutcome.TenantMismatch);

        var criteria = MemberMatchCriteria.From(request);
        if (!criteria.IsSufficient)
            return Failed(request, MemberMatchOutcome.InsufficientCriteria);

        var resolved = await ResolveAsync(tenantId, criteria, ct);
        if (resolved.Outcome != MemberMatchOutcome.Matched)
            return Failed(request, resolved.Outcome, resolved.Member?.MemberId);

        var now = _time.GetUtcNow().UtcDateTime;
        var consent = PdexConsentValidator.Validate(request.Consent, request.ReceivingPayerId, _localPayerId, now);
        if (!consent.Valid)
            return Failed(request, MemberMatchOutcome.ConsentNotAccepted, resolved.Member!.MemberId, consent.Failure);
        if (!NamesMember(consent, resolved))
            return Failed(request, MemberMatchOutcome.ConsentNotAccepted, resolved.Member!.MemberId,
                PdexConsentFailure.PatientNotBound);

        return await AcceptAsync(request, tenantId, consent, resolved, now, ct);
    }

    /// <summary>
    /// The member and coverage the criteria resolve to, decided fail-safe:
    /// anything short of exactly one member with one coverage context is not a
    /// match.
    /// </summary>
    private async Task<Resolution> ResolveAsync(string tenantId, MemberMatchCriteria criteria, CancellationToken ct)
    {
        var candidates = await _source.GetCandidatesAsync(tenantId, criteria, ct);
        // The source cannot look these criteria up (an SSN-only request where
        // SSNs cannot be searched, ADR 020): no match can be proven either way.
        if (!candidates.Searchable)
            return new(MemberMatchOutcome.InsufficientCriteria);
        // More candidates than the source reads: a unique match cannot be
        // proven, so the request is answered as ambiguous rather than guessed.
        if (!candidates.Complete)
            return new(MemberMatchOutcome.AmbiguousMatch);
        // A supplied SSN the source cannot compare could contradict the member on
        // file (another person sharing a name and birth date): ignoring it would
        // let that contradiction pass, so the request is answered as ambiguous.
        if (criteria.Ssn is not null && !candidates.SsnVerifiable)
            return new(MemberMatchOutcome.AmbiguousMatch);
        var members = candidates.Members;

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

        if (strong.Count == 0) return new(MemberMatchOutcome.NoMatch);
        if (strong.Count > 1) return new(MemberMatchOutcome.AmbiguousMatch);

        var (matched, matchedCoverages) = strong[0];
        // Coverage selection always needs the matched member's coverages; fetch
        // them now if the match path above did not already.
        if (!needsCoveragesForMatch)
            matchedCoverages = await _source.GetCoveragesAsync(tenantId, matched.MemberId, ct);

        var selection = PayerToPayerCoverageSelector.Select(matchedCoverages, criteria);
        if (selection.Outcome == CoverageSelectionOutcome.Ambiguous)
            return new(MemberMatchOutcome.AmbiguousCoverage, matched);

        return new(MemberMatchOutcome.Matched, matched, matchedCoverages, selection.Coverage);
    }

    private static bool NamesMember(PdexConsentValidation consent, Resolution resolved)
        => consent.NamesMember(
            resolved.Coverages.Select(c => c.SubscriberId).Append(resolved.Member!.MemberId),
            resolved.Member.LastName, resolved.Member.Dob);

    /// <summary>
    /// Records the accepted consent for the requesting payer, so the export it
    /// asks for next is served to that payer, for this member, while the consent
    /// is in force.
    /// </summary>
    private async Task<MemberMatchResult> AcceptAsync(
        MemberMatchRequest request, string tenantId, PdexConsentValidation consent, Resolution resolved, DateTime now,
        CancellationToken ct)
    {
        var matched = resolved.Member!;
        await _attestations.RecordAsync(new PayerToPayerConsentAttestation
        {
            TenantId = tenantId,
            MemberId = matched.MemberId,
            ReceivingPayerId = request.ReceivingPayerId,
            Policy = consent.Policy!,
            PeriodStartUtc = consent.PeriodStartUtc!.Value,
            PeriodEndUtc = consent.PeriodEndUtc,
            ConsentFingerprint = consent.Fingerprint!,
            RecordedAtUtc = now,
            RecordedBy = request.InitiatedBy ?? "unknown",
        }, ct);

        var audit = Audit(request, MemberMatchOutcome.Matched, matched.MemberId, resolved.Coverage?.CoverageId,
            consent.Fingerprint);
        _logger.LogInformation(
            "P2P member-match: tenant={Tenant} receivingPayer={Payer} member={Member} coverage={Coverage}",
            LogSanitizer.Sanitize(audit.TenantId), LogSanitizer.Sanitize(audit.ReceivingPayerId), LogSanitizer.MaskId(audit.MatchedMemberId), LogSanitizer.Sanitize(audit.SelectedCoverageId));

        return MemberMatchResult.Matched(matched, resolved.Coverage, audit);
    }

    private sealed record Resolution(
        MemberMatchOutcome Outcome,
        ChoMember? Member = null,
        IReadOnlyList<ChoCoverage>? MatchedCoverages = null,
        ChoCoverage? Coverage = null)
    {
        public IReadOnlyList<ChoCoverage> Coverages => MatchedCoverages ?? [];
    }

    private MemberMatchResult Failed(
        MemberMatchRequest request, MemberMatchOutcome outcome, string? matchedMemberId = null,
        PdexConsentFailure? consentFailure = null)
    {
        var audit = Audit(request, outcome, matchedMemberId, selectedCoverageId: null, consentFailure: consentFailure);
        _logger.LogInformation(
            "P2P member-match declined: tenant={Tenant} receivingPayer={Payer} outcome={Outcome} consent={Consent}",
            LogSanitizer.Sanitize(audit.TenantId), LogSanitizer.Sanitize(audit.ReceivingPayerId), audit.Outcome,
            audit.ConsentFailure ?? "n/a");
        return MemberMatchResult.Failure(outcome, audit);
    }

    private static MemberMatchAuditEntry Audit(
        MemberMatchRequest request, MemberMatchOutcome outcome, string? matchedMemberId, string? selectedCoverageId,
        string? consentFingerprint = null, PdexConsentFailure? consentFailure = null) =>
        new()
        {
            // Record the trimmed tenant — the same value scoping treats as
            // authoritative — so audit/log entries are stable and correlatable.
            TenantId = request.TenantId?.Trim() ?? string.Empty,
            ReceivingPayerId = request.ReceivingPayerId,
            InitiatedBy = request.InitiatedBy,
            MatchedMemberId = matchedMemberId,
            SelectedCoverageId = selectedCoverageId,
            Outcome = outcome.ToString(),
            ConsentFingerprint = consentFingerprint,
            ConsentFailure = consentFailure?.ToString(),
        };
}
