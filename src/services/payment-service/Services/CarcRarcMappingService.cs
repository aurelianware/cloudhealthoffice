using PaymentService.Models;

namespace PaymentService.Services;

/// <summary>
/// Maps a claim's adjudication state to 835 CAS segment data
/// (claim-level 2100 loop and per-line 2110 loop). Pure mapper —
/// stateless, no I/O — Singleton in DI.
///
/// Two distinct surfaces with their own emission rules per 5.10
/// Decision 6:
///
/// <c>MapClaimAdjustments</c> (header CAS, 2100 loop):
///   1. Standard adjudication adjustments from
///      <c>AdjudicationResult.AdjustmentReasons</c> (typed CARC objects,
///      group + reason + amount) emit first — covers normal payment
///      scenarios (e.g., PR-1 deductible $500, PR-2 coinsurance $80,
///      CO-45 contractual).
///   2. Header-level denial from <c>AdjudicationResult.DenialReasonCode</c>
///      appends as a <c>CO</c> entry only when no entry from step 1
///      already carries that reason code — avoids double-CAS for the
///      same denial reason.
///
/// <c>MapLineAdjustments</c> (per-line CAS, 2110 loop, keyed by
/// <c>AffectedLineNumbers</c>):
///   1. Per-line edits from <c>PendDetails.EditFailures</c> with
///      <c>SuggestedCarc</c>/<c>SuggestedRarc</c> populated by 5.7's
///      <c>NcciEditsStage</c>. Each affected line emits one CAS group
///      with the suggested CARC and (optionally) RARC.
///
/// Fallback CARC <c>237</c> mirrors the 5.11 EOB projector default
/// when a per-line edit failure populates <c>SuggestedCarc=null</c>
/// (unknown adjustment reason); RARC is omitted entirely when null.
/// The 237 fallback only fires inside <c>MapLineAdjustments</c> — the
/// header path always carries explicit CARCs from the adjudication
/// pipeline.
/// </summary>
public interface ICarcRarcMappingService
{
    /// <summary>
    /// Build the claim-level CAS data (835 2100 loop).
    /// </summary>
    IReadOnlyList<ClaimAdjustment> MapClaimAdjustments(ClaimAdjudicationSnapshot snapshot);

    /// <summary>
    /// Build per-line CAS data keyed by service-line number (835 2110
    /// loop). A line with no adjustments returns an empty list under
    /// its line number; lines with no entry in the result are emitted
    /// without CAS segments at all.
    /// </summary>
    IReadOnlyDictionary<int, IReadOnlyList<ServiceLineAdjustment>> MapLineAdjustments(ClaimAdjudicationSnapshot snapshot);
}

/// <summary>
/// Read-only snapshot of the adjudication-side fields the CARC/RARC
/// mapper consumes. Detached from <c>ClaimDto</c> so the mapper can be
/// unit-tested without dragging in HTTP plumbing, and so future
/// upstream model changes don't ripple through the mapper.
/// </summary>
public class ClaimAdjudicationSnapshot
{
    public string ClaimId { get; set; } = string.Empty;

    /// <summary>Header-level denial code (CO/PI group). Null on paid/partially-paid claims.</summary>
    public string? DenialReasonCode { get; set; }

    /// <summary>Header-level denial description for human-readable trace; not emitted in 835 directly.</summary>
    public string? DenialReason { get; set; }

    /// <summary>Standard claim-level adjustments (PR-1 deductible, PR-2 coinsurance, PR-3 copay, CO-45 contractual).</summary>
    public List<ClaimAdjustmentReasonView> AdjustmentReasons { get; set; } = new();

    /// <summary>Header-level remark codes (LQ segment in 835).</summary>
    public List<string> RemarkCodes { get; set; } = new();

    /// <summary>Per-line edit failures populated by 5.7 NcciEditsStage. Empty on claims that didn't pend on edits.</summary>
    public List<EditFailureView> EditFailures { get; set; } = new();
}

/// <summary>
/// Payment-service-local mirror of <c>ClaimsService.Models.ClaimAdjustmentReason</c>.
/// Keeps the mapper input independent of claims-service DLL coupling.
/// </summary>
public class ClaimAdjustmentReasonView
{
    public string GroupCode { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string? Description { get; set; }
}

/// <summary>
/// Payment-service-local mirror of <c>ClaimsService.Models.NcciEditFailureSnapshot</c>.
/// Carries the SuggestedCarc / SuggestedRarc / AffectedLineNumbers fields
/// the mapper actually consumes.
/// </summary>
public class EditFailureView
{
    public string EditType { get; set; } = string.Empty;
    public string RuleId { get; set; } = string.Empty;
    public string? Message { get; set; }
    public List<int> AffectedLineNumbers { get; set; } = new();
    public string? SuggestedCarc { get; set; }
    public string? SuggestedRarc { get; set; }
}

public class CarcRarcMappingService : ICarcRarcMappingService
{
    /// <summary>
    /// Fallback CARC when an edit failure populates <c>SuggestedCarc=null</c>.
    /// Mirrors the 5.11 EOB projector default ("Adjustment for administrative
    /// cost"). Only used as a last resort within the per-line edit branch —
    /// the standard adjudication branch already carries explicit CARCs.
    /// </summary>
    public const string FallbackCarc = "237";

    private const decimal EditFailureAmountPlaceholder = 0m;

    private readonly ILogger<CarcRarcMappingService> _logger;

    public CarcRarcMappingService(ILogger<CarcRarcMappingService> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<ClaimAdjustment> MapClaimAdjustments(ClaimAdjudicationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var output = new List<ClaimAdjustment>();

        // 1. Standard adjudication adjustments (PR-1, PR-2, PR-3, CO-45).
        //    These are explicit CARC entries from the adjudication
        //    pipeline (5.5/5.7) and always emit at the header.
        foreach (var reason in snapshot.AdjustmentReasons)
        {
            if (reason is null) continue;
            output.Add(new ClaimAdjustment
            {
                GroupCode = reason.GroupCode,
                ReasonCode = reason.ReasonCode,
                Amount = reason.Amount,
                ReasonDescription = reason.Description
            });
        }

        // 2. Header-level denial. Only emitted when no explicit
        //    adjustment carries the same CARC already (avoid double-CAS
        //    for the same denial reason).
        if (!string.IsNullOrEmpty(snapshot.DenialReasonCode))
        {
            var alreadyEmitted = output.Any(a =>
                string.Equals(a.ReasonCode, snapshot.DenialReasonCode, StringComparison.Ordinal));

            if (!alreadyEmitted)
            {
                output.Add(new ClaimAdjustment
                {
                    GroupCode = "CO",
                    ReasonCode = snapshot.DenialReasonCode,
                    Amount = 0m,
                    ReasonDescription = snapshot.DenialReason
                });
            }
        }

        return output;
    }

    public IReadOnlyDictionary<int, IReadOnlyList<ServiceLineAdjustment>> MapLineAdjustments(ClaimAdjudicationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var byLine = new Dictionary<int, List<ServiceLineAdjustment>>();

        foreach (var failure in snapshot.EditFailures)
        {
            if (failure is null || failure.AffectedLineNumbers is null) continue;

            var carc = string.IsNullOrEmpty(failure.SuggestedCarc) ? FallbackCarc : failure.SuggestedCarc!;
            var rarc = string.IsNullOrEmpty(failure.SuggestedRarc) ? null : failure.SuggestedRarc;

            if (carc == FallbackCarc && string.IsNullOrEmpty(failure.SuggestedCarc))
            {
                _logger.LogDebug(
                    "Edit failure {RuleId} ({EditType}) has no SuggestedCarc; using fallback {Fallback}",
                    failure.RuleId, failure.EditType, FallbackCarc);
            }

            foreach (var lineNumber in failure.AffectedLineNumbers)
            {
                if (!byLine.TryGetValue(lineNumber, out var list))
                {
                    list = new List<ServiceLineAdjustment>();
                    byLine[lineNumber] = list;
                }

                list.Add(new ServiceLineAdjustment
                {
                    GroupCode = "CO",
                    ReasonCode = carc,
                    Amount = EditFailureAmountPlaceholder,
                    RemarkCode = rarc,
                    ReasonDescription = failure.Message
                });
            }
        }

        return byLine.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<ServiceLineAdjustment>)kvp.Value);
    }
}
