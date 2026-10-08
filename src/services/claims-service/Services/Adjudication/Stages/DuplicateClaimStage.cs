using System.Diagnostics;
using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Repositories;
using Microsoft.Extensions.Options;

namespace ClaimsService.Services.Adjudication.Stages;

/// <summary>
/// Duplicate-claim detection. Runs at <see cref="Order"/> = 120 — after
/// ScrubbingStage (100) has rejected structurally invalid claims and before
/// any stage that calls out to provider / network / benefit services, so an
/// exact duplicate is denied without spending those round trips.
///
/// <para>
/// Each incoming service line is compared to the lines of the member's
/// prior <em>live</em> claims whose service period overlaps
/// (<see cref="IClaimRepository.FindDuplicateCandidatesAsync"/>):
/// </para>
/// <list type="bullet">
///   <item><description><b>Exact duplicate</b> — same tenant, member,
///     billing provider NPI, line service dates, procedure code, revenue
///     code, modifier set (order-insensitive), units and line charge.
///     Default <see cref="DuplicateClaimEnforcementMode.Deny"/> with CARC 18
///     (group CO).</description></item>
///   <item><description><b>Suspect duplicate</b> — same member, line
///     service dates and procedure (or, with no procedure, revenue) code,
///     but a different billing provider, modifiers, units or charge.
///     Default <see cref="DuplicateClaimEnforcementMode.PendForReview"/>
///     with pend code <see cref="DuplicatePendCode"/>.</description></item>
/// </list>
///
/// <para>
/// <b>Never flagged:</b> replacement (frequency 7) and void (frequency 8)
/// claims, and adjustment versions that reference the version they amend
/// (<see cref="AdapterClaim.PredecessorVersionId"/>) — those are supposed
/// to look like the original. The claim never matches itself or any other
/// version of its own chain (<see cref="AdapterClaim.ClaimVersionId"/>).
/// Prior claims that are Draft, Denied, Voided, superseded, or are
/// themselves void requests are not live and never count as originals.
/// </para>
///
/// <para>
/// <b>Ordering.</b> Only priors submitted before the incoming claim
/// (ties broken by id) count, so two copies racing through the pipeline
/// flag the later copy rather than whichever happens to adjudicate second.
/// </para>
///
/// <para>
/// <b>Claim vs line.</b> The pipeline has no partial line-denial path ahead
/// of benefit calculation, so a claim-level Deny is issued only when every
/// line resolves to Deny; a claim mixing duplicate and clean lines pends
/// with the per-line findings (and suggested CARC 18) on
/// <see cref="PendDetails.DuplicateFindings"/> — a collection separate from
/// the NCCI-specific <see cref="PendDetails.EditFailures"/>, mirrored on
/// <see cref="ClaimAdjudicationContext.DuplicateFindings"/> so
/// PersistenceStage can re-attach it if a later stage replaces PendDetails.
/// </para>
/// </summary>
public sealed class DuplicateClaimStage : IClaimAdjudicationStage
{
    public const string StageName = "DuplicateClaim";

    /// <summary>CARC 18 — exact duplicate claim/service.</summary>
    public const string DuplicateCarc = "18";

    /// <summary>CAS group code for <see cref="DuplicateCarc"/> (contractual obligation).</summary>
    public const string DuplicateGroupCode = "CO";

    public const string DuplicatePendCode = "DUPLICATE";
    public const string ExactRuleId = "DUP001";
    public const string SuspectRuleId = "DUP002";
    public const string ExactDuplicateType = "Exact";
    public const string SuspectDuplicateType = "Suspect";

    private const string ReplacementFrequencyCode = "7";
    private const string VoidFrequencyCode = "8";

    private static readonly ActivitySource ActivitySource = new("ClaimsService.Adjudication");

    private readonly IClaimRepository _repository;
    private readonly TenantEnforcementPolicyOptions _options;
    private readonly ILogger<DuplicateClaimStage> _logger;

    public DuplicateClaimStage(
        IClaimRepository repository,
        IOptions<TenantEnforcementPolicyOptions> options,
        ILogger<DuplicateClaimStage> logger)
    {
        _repository = repository;
        _options = options.Value;
        _logger = logger;
    }

    public string Name => StageName;
    public int Order => 120;
    public bool IsRequired => false;

    public async Task<ClaimAdjudicationStageResult> ExecuteAsync(
        ClaimAdjudicationContext context,
        CancellationToken ct)
    {
        using var activity = ActivitySource.StartActivity(
            "Adjudication.DuplicateClaim",
            ActivityKind.Internal);
        activity?.SetTag("claim.versionId", context.ClaimVersionId);
        activity?.SetTag("tenant.id", context.TenantId);
        activity?.SetTag("duplicate.exact_mode", _options.ExactDuplicateMode.ToString());
        activity?.SetTag("duplicate.suspect_mode", _options.SuspectDuplicateMode.ToString());

        var claim = context.Claim;

        if (IsExemptFromDuplicateCheck(claim, out var exemptReason))
        {
            activity?.SetTag("duplicate.outcome", "exempt");
            activity?.SetTag("duplicate.exempt_reason", exemptReason);
            return ClaimAdjudicationStageResult.Pass(StageName);
        }

        var lines = claim.ClaimLines
            .Select(l => LineKey.Build(l, claim))
            .Where(k => k is not null)
            .Select(k => k!)
            .ToList();
        if (lines.Count == 0)
        {
            activity?.SetTag("duplicate.outcome", "no_comparable_lines");
            return ClaimAdjudicationStageResult.Pass(StageName);
        }

        var windowFrom = lines.Min(l => l.From);
        var windowTo = lines.Max(l => l.To);
        var chainKey = ChainKey(claim.ClaimVersionId, claim.Id);

        IReadOnlyList<Claim> candidates;
        try
        {
            candidates = await _repository
                .FindDuplicateCandidatesAsync(
                    context.TenantId, claim.MemberId, windowFrom, windowTo, chainKey, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The orchestrator would turn an escaped exception into a
            // terminal Reject; an unavailable duplicate check is not a
            // reason to reject a claim, but it is also not evidence the
            // claim is clean.
            _logger.LogError(ex,
                "Duplicate candidate lookup failed for claim {ClaimVersionId}; degrading to mode-driven outcome",
                SanitizeForLog(context.ClaimVersionId));
            activity?.SetTag("duplicate.lookup_status", "exception");
            if (_options.ExactDuplicateMode == DuplicateClaimEnforcementMode.SoftValidation
                && _options.SuspectDuplicateMode == DuplicateClaimEnforcementMode.SoftValidation)
            {
                activity?.SetTag("duplicate.outcome", "softvalidation");
                return ClaimAdjudicationStageResult.Pass(StageName);
            }
            const string reason = "Duplicate-claim check could not be completed; pended for review.";
            context.PendDetails = new PendDetails
            {
                PendCode = DuplicatePendCode,
                PendReason = reason,
                PendedAt = DateTime.UtcNow,
            };
            activity?.SetTag("duplicate.outcome", "pend");
            return ClaimAdjudicationStageResult.Pend(StageName, reason);
        }

        var priors = candidates
            .Where(c => IsLivePrior(c, claim, context.TenantId, chainKey))
            .ToList();
        activity?.SetTag("duplicate.candidates", candidates.Count);
        activity?.SetTag("duplicate.priors", priors.Count);

        var findings = FindDuplicates(claim, lines, priors);
        activity?.SetTag("duplicate.exact_lines", findings.Count(f => f.IsExact));
        activity?.SetTag("duplicate.suspect_lines", findings.Count(f => !f.IsExact));

        if (findings.Count == 0)
        {
            activity?.SetTag("duplicate.outcome", "approve");
            return ClaimAdjudicationStageResult.Pass(StageName);
        }

        var decisions = findings
            .Select(f => (Finding: f, Mode: f.IsExact ? _options.ExactDuplicateMode : _options.SuspectDuplicateMode))
            .ToList();

        _logger.LogInformation(
            "DuplicateClaimStage found {Exact} exact and {Suspect} suspect duplicate line(s) of {Lines} on claim {ClaimVersionId}",
            findings.Count(f => f.IsExact),
            findings.Count(f => !f.IsExact),
            claim.ClaimLines.Count,
            SanitizeForLog(context.ClaimVersionId));

        var allLinesDeny = findings.Count == claim.ClaimLines.Count
            && decisions.All(d => d.Mode == DuplicateClaimEnforcementMode.Deny);
        if (allLinesDeny)
        {
            ApplyDenial(context, findings);
            activity?.SetTag("duplicate.outcome", "deny");
            return ClaimAdjudicationStageResult.Deny(StageName, context.AdjudicationResult.DenialReason!);
        }

        var actionable = decisions
            .Where(d => d.Mode != DuplicateClaimEnforcementMode.SoftValidation)
            .Select(d => d.Finding)
            .ToList();
        if (actionable.Count == 0)
        {
            activity?.SetTag("duplicate.outcome", "softvalidation");
            return ClaimAdjudicationStageResult.Pass(StageName);
        }

        var pendReason = BuildPendReason(actionable);
        var snapshots = actionable.Select(ToSnapshot).ToList();
        // Also kept on the context: a later stage (e.g. NCCI) may replace
        // PendDetails wholesale, and PersistenceStage re-attaches these.
        context.DuplicateFindings.AddRange(snapshots);
        context.PendDetails = new PendDetails
        {
            PendCode = DuplicatePendCode,
            PendReason = TruncatePendReason(pendReason),
            PendedAt = DateTime.UtcNow,
            DuplicateFindings = snapshots.ToList(),
        };
        activity?.SetTag("duplicate.outcome", "pend");
        return ClaimAdjudicationStageResult.Pend(StageName, TruncatePendReason(pendReason)!);
    }

    /// <summary>
    /// Replacements (freq 7), voids (freq 8) and adjustment versions that
    /// reference the version they amend are expected to mirror an original
    /// claim and must never be flagged as duplicates of it.
    /// </summary>
    internal static bool IsExemptFromDuplicateCheck(AdapterClaim claim, out string reason)
    {
        var frequency = claim.ClaimFrequencyCode?.Trim();
        if (frequency == ReplacementFrequencyCode)
        {
            reason = "replacement";
            return true;
        }
        if (frequency == VoidFrequencyCode)
        {
            reason = "void";
            return true;
        }
        if (!string.IsNullOrWhiteSpace(claim.PredecessorVersionId))
        {
            reason = "references_original";
            return true;
        }
        if (string.IsNullOrWhiteSpace(claim.MemberId))
        {
            reason = "no_member";
            return true;
        }
        reason = string.Empty;
        return false;
    }

    /// <summary>
    /// In-memory mirror of the repository filter (defense in depth — a vendor
    /// or legacy store that ignores part of the filter must still never
    /// match the claim against itself or a dead claim), plus the
    /// submitted-before ordering rule.
    /// </summary>
    internal static bool IsLivePrior(Claim prior, AdapterClaim claim, string tenantId, string chainKey)
    {
        if (!string.Equals(prior.TenantId, tenantId, StringComparison.Ordinal)) return false;
        if (!string.Equals(prior.MemberId, claim.MemberId, StringComparison.Ordinal)) return false;
        if (string.Equals(prior.Id, claim.Id, StringComparison.Ordinal)) return false;
        if (string.Equals(ChainKey(prior.ClaimVersionId, prior.Id), chainKey, StringComparison.Ordinal)) return false;
        if (string.Equals(prior.Id, chainKey, StringComparison.Ordinal)) return false;
        if (string.Equals(prior.Id, claim.PredecessorVersionId, StringComparison.Ordinal)) return false;
        if (prior.Status is ClaimStatus.Denied or ClaimStatus.Voided) return false;
        if (prior.VersionState is ClaimVersionState.Draft or ClaimVersionState.Denied
            or ClaimVersionState.Voided or ClaimVersionState.Adjusted) return false;
        if (prior.SupersededAt is not null) return false;
        if (prior.ClaimFrequencyCode?.Trim() == VoidFrequencyCode) return false;

        // Only claims submitted before this one are originals. An unset
        // incoming SubmittedDate (vendor adapter that doesn't carry it)
        // disables the ordering rule rather than every match.
        if (claim.SubmittedDate != default)
        {
            if (prior.SubmittedDate > claim.SubmittedDate) return false;
            if (prior.SubmittedDate == claim.SubmittedDate
                && string.CompareOrdinal(prior.Id, claim.Id) > 0) return false;
        }
        return true;
    }

    private static List<DuplicateFinding> FindDuplicates(
        AdapterClaim claim,
        IReadOnlyList<LineKey> incomingLines,
        IReadOnlyList<Claim> priors)
    {
        var priorLines = priors
            .SelectMany(p => p.ClaimLines
                .Where(l => !WasDeniedAsDuplicate(l))
                .Select(l => (Prior: p, Line: l, Key: LineKey.Build(l, p))))
            .Where(x => x.Key is not null)
            .Select(x => (x.Prior, x.Line, Key: x.Key!))
            .ToList();

        var billingNpi = Normalize(claim.BillingProviderNPI);
        var consumed = new HashSet<int>();
        var findings = new List<DuplicateFinding>();

        foreach (var line in incomingLines)
        {
            var exactIndex = -1;
            for (var i = 0; i < priorLines.Count; i++)
            {
                if (consumed.Contains(i)) continue;
                var p = priorLines[i];
                if (Normalize(p.Prior.BillingProviderNPI) == billingNpi && line.IsExactMatch(p.Key))
                {
                    exactIndex = i;
                    break;
                }
            }

            if (exactIndex >= 0)
            {
                consumed.Add(exactIndex);
                var p = priorLines[exactIndex];
                findings.Add(new DuplicateFinding(
                    line.LineNumber, true, p.Prior.Id, p.Prior.ClaimNumber, p.Line.LineNumber,
                    "identical billing provider, service date, code, modifiers, units and charge"));
                continue;
            }

            var suspect = priorLines.FirstOrDefault(p => line.IsSuspectMatch(p.Key));
            if (suspect.Key is not null)
            {
                findings.Add(new DuplicateFinding(
                    line.LineNumber, false, suspect.Prior.Id, suspect.Prior.ClaimNumber, suspect.Line.LineNumber,
                    DescribeDifferences(billingNpi, line, suspect.Prior, suspect.Key)));
            }
        }

        return findings;
    }

    private static bool WasDeniedAsDuplicate(ClaimLine line) =>
        line.AdjudicationResult?.AdjustmentReasons.Any(r => r.ReasonCode == DuplicateCarc) == true;

    private static string DescribeDifferences(string billingNpi, LineKey incoming, Claim prior, LineKey priorLine)
    {
        var differences = new List<string>();
        if (Normalize(prior.BillingProviderNPI) != billingNpi) differences.Add("billing provider");
        if (!incoming.Modifiers.SequenceEqual(priorLine.Modifiers)) differences.Add("modifiers");
        if (incoming.ProcedureCode != priorLine.ProcedureCode || incoming.RevenueCode != priorLine.RevenueCode)
            differences.Add("procedure/revenue code");
        if (incoming.Units != priorLine.Units) differences.Add("units");
        if (incoming.Charge != priorLine.Charge) differences.Add("charge");
        return differences.Count == 0
            ? "additional occurrence of an already-matched service"
            : "different " + string.Join(", ", differences);
    }

    private void ApplyDenial(ClaimAdjudicationContext context, IReadOnlyList<DuplicateFinding> findings)
    {
        var claim = context.Claim;
        var original = findings[0];
        var originalLabel = string.IsNullOrWhiteSpace(original.PriorClaimNumber)
            ? original.PriorClaimId
            : original.PriorClaimNumber;
        var reason = findings.All(f => f.IsExact)
            ? $"Exact duplicate of claim {originalLabel} (CARC {DuplicateCarc})."
            : $"Duplicate of claim {originalLabel} (CARC {DuplicateCarc}).";

        context.AdjudicationResult.DenialReasonCode = DuplicateCarc;
        context.AdjudicationResult.DenialReason = reason;
        context.AdjudicationResult.AdjustmentReasons.Add(new ClaimAdjustmentReason
        {
            GroupCode = DuplicateGroupCode,
            ReasonCode = DuplicateCarc,
            Amount = claim.TotalChargeAmount,
            Description = "Exact duplicate claim/service",
        });

        // One entry per claim line, in claim-line order, so the projection
        // bypass (which maps by index when counts match) stamps CO-18 on
        // every line.
        context.LineAdjudicationResults = claim.ClaimLines
            .Select(l => new LineAdjudicationResult
            {
                AllowedAmount = 0m,
                PaidAmount = 0m,
                PatientResponsibility = 0m,
                AdjustmentReasons = new List<ClaimAdjustmentReason>
                {
                    new()
                    {
                        GroupCode = DuplicateGroupCode,
                        ReasonCode = DuplicateCarc,
                        Amount = l.ChargeAmount,
                        Description = "Exact duplicate claim/service",
                    },
                },
            })
            .ToList();

        _logger.LogInformation(
            "DuplicateClaimStage denied claim {ClaimVersionId} as duplicate of {PriorClaimId}",
            SanitizeForLog(context.ClaimVersionId),
            SanitizeForLog(original.PriorClaimId));
    }

    private static string BuildPendReason(IReadOnlyList<DuplicateFinding> findings)
    {
        var first = findings[0];
        var kind = first.IsExact ? "Exact" : "Suspect";
        var label = string.IsNullOrWhiteSpace(first.PriorClaimNumber) ? first.PriorClaimId : first.PriorClaimNumber;
        var head = $"{kind} duplicate: line {first.LineNumber} matches claim {label} line {first.PriorLineNumber} ({first.Detail})";
        return findings.Count == 1
            ? head + "."
            : $"{findings.Count} possible duplicate line(s); first: {head}.";
    }

    private static DuplicateFindingSnapshot ToSnapshot(DuplicateFinding finding) => new()
    {
        DuplicateType = finding.IsExact ? ExactDuplicateType : SuspectDuplicateType,
        RuleId = finding.IsExact ? ExactRuleId : SuspectRuleId,
        Message = TruncateMessage(
            $"Line {finding.LineNumber} vs claim {finding.PriorClaimNumber} ({finding.PriorClaimId}) " +
            $"line {finding.PriorLineNumber}: {finding.Detail}"),
        LineNumber = finding.LineNumber,
        MatchedClaimId = finding.PriorClaimId,
        MatchedClaimNumber = finding.PriorClaimNumber,
        MatchedLineNumber = finding.PriorLineNumber,
        SuggestedCarc = DuplicateCarc,
    };

    internal static string ChainKey(string? claimVersionId, string id) =>
        string.IsNullOrEmpty(claimVersionId) ? id : claimVersionId;

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();

    private static string? TruncatePendReason(string? reason) =>
        reason is { Length: > 500 } ? reason[..500] : reason;

    private static string TruncateMessage(string message) =>
        message.Length <= 1000 ? message : message[..1000];

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");

    private sealed record DuplicateFinding(
        int LineNumber,
        bool IsExact,
        string PriorClaimId,
        string PriorClaimNumber,
        int PriorLineNumber,
        string Detail);

    /// <summary>
    /// Normalized comparison key for one service line. Service dates fall
    /// back to the claim header when the line carries none; codes and
    /// modifiers compare case-insensitively, modifiers as an ordered set.
    /// </summary>
    private sealed record LineKey(
        int LineNumber,
        DateTime From,
        DateTime To,
        string ProcedureCode,
        string RevenueCode,
        IReadOnlyList<string> Modifiers,
        decimal Units,
        decimal Charge)
    {
        public static LineKey? Build(AdapterClaimLine line, AdapterClaim claim) => Create(
            line.LineNumber, line.ServiceDateFrom, line.ServiceDateTo, claim.ServiceDateFrom, claim.ServiceDateTo,
            line.ProcedureCode, line.RevenueCode, line.Modifiers, line.Units, line.ChargeAmount);

        public static LineKey? Build(ClaimLine line, Claim claim) => Create(
            line.LineNumber, line.ServiceDateFrom, line.ServiceDateTo, claim.ServiceDateFrom, claim.ServiceDateTo,
            line.ProcedureCode, line.RevenueCode, line.Modifiers, line.Units, line.ChargeAmount);

        private static LineKey? Create(
            int lineNumber,
            DateTime lineFrom,
            DateTime lineTo,
            DateTime claimFrom,
            DateTime claimTo,
            string? procedureCode,
            string? revenueCode,
            IEnumerable<string>? modifiers,
            decimal units,
            decimal charge)
        {
            var procedure = Normalize(procedureCode);
            var revenue = Normalize(revenueCode);
            if (procedure.Length == 0 && revenue.Length == 0) return null;

            var from = (lineFrom == default ? claimFrom : lineFrom).Date;
            var to = (lineTo == default ? (lineFrom == default ? claimTo : lineFrom) : lineTo).Date;
            if (from == default) return null;
            if (to < from) to = from;

            var normalizedModifiers = (modifiers ?? Enumerable.Empty<string>())
                .Select(Normalize)
                .Where(m => m.Length > 0)
                .OrderBy(m => m, StringComparer.Ordinal)
                .ToList();

            return new LineKey(lineNumber, from, to, procedure, revenue, normalizedModifiers, units, charge);
        }

        /// <summary>Code used for suspect matching: procedure when present, otherwise revenue code.</summary>
        private string SuspectCode => ProcedureCode.Length > 0 ? "P:" + ProcedureCode : "R:" + RevenueCode;

        private bool SameDates(LineKey other) => From == other.From && To == other.To;

        public bool IsExactMatch(LineKey other) =>
            SameDates(other)
            && ProcedureCode == other.ProcedureCode
            && RevenueCode == other.RevenueCode
            && Modifiers.SequenceEqual(other.Modifiers)
            && Units == other.Units
            && Charge == other.Charge;

        public bool IsSuspectMatch(LineKey other) =>
            SameDates(other) && SuspectCode == other.SuspectCode;
    }
}
