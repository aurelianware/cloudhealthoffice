using CloudHealthOffice.ClinicalCriteria.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;

namespace CloudHealthOffice.ClinicalCriteria.Models;

/// <summary>
/// Everything a criteria provider is given about one review.
///
/// This is the whole of CHO's half of the contract: a licensed-content
/// connector maps these fields onto its vendor request and nothing else, so
/// adding InterQual or MCG never reaches back into the services that call it.
/// </summary>
public record CriteriaRequest
{
    // ── Routing ───────────────────────────────────────────────────
    public required string TenantId                         { get; init; }
    public required string StateCode                        { get; init; }
    public required PaLineOfBusiness Lob                    { get; init; }

    /// <summary>State program within the LOB ("STAR", "STARPlus", …); null elsewhere.</summary>
    public string? Program                                  { get; init; }

    public CriteriaReviewType ReviewType                    { get; init; } = CriteriaReviewType.Prospective;

    /// <summary>The authorization under review, when there is one. Carried through to the audit record.</summary>
    public string? AuthorizationId                          { get; init; }

    // ── Clinical subject ──────────────────────────────────────────
    public required string MemberId                         { get; init; }
    public DateOnly? MemberDateOfBirth                      { get; init; }
    public required DateOnly ServiceDate                    { get; init; }
    public required IReadOnlyList<string> ProcedureCodes    { get; init; }
    public required IReadOnlyList<string> DiagnosisCodes    { get; init; }
    public string? PlaceOfServiceCode                       { get; init; }
    public decimal RequestedUnits                           { get; init; } = 1;

    /// <summary>
    /// Requested level of care, where the review is about one: e.g.
    /// "inpatient-acute", "observation", "snf", "ltac", "inpatient-rehab".
    /// Free text by design — the vocabulary differs per criteria vendor, and
    /// the connector maps it.
    /// </summary>
    public string? LevelOfCare                              { get; init; }

    /// <summary>Day of the stay for a concurrent review; null for a prospective one.</summary>
    public int? StayDay                                     { get; init; }

    // ── Providers ─────────────────────────────────────────────────
    public required string RequestingProviderNpi            { get; init; }
    public required string ServicingProviderNpi             { get; init; }
    public string? ServicingProviderTaxonomy                { get; init; }
    public string? FacilityNpi                              { get; init; }

    /// <summary>
    /// Clinical findings the reviewer or an attachment supplied, as
    /// provider-neutral key/value pairs (vital signs, functional scores,
    /// prior conservative therapy). Keys are whatever the calling workflow
    /// collects; the connector maps the ones its guideline asks for and
    /// reports the rest as unanswered questions.
    /// </summary>
    public IReadOnlyDictionary<string, string> ClinicalIndicators { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public decimal EstimatedCost                            { get; init; }
}

/// <summary>The guideline a provider applied, named so a decision can cite it.</summary>
public record CriteriaGuidelineReference
{
    public required string GuidelineId  { get; init; }
    public required string Title        { get; init; }

    /// <summary>Content version or edition, e.g. an InterQual annual release.</summary>
    public string? Version              { get; init; }

    /// <summary>Where a reviewer can read the guideline, when the provider gives a link.</summary>
    public string? Url                  { get; init; }
}

/// <summary>A question the guideline asks that the request did not answer.</summary>
public record CriteriaQuestion
{
    public required string Id       { get; init; }
    public required string Text     { get; init; }

    /// <summary>True when the guideline cannot conclude without an answer.</summary>
    public bool Required            { get; init; } = true;
}

/// <summary>
/// One provider's answer, normalized. Persisted against the authorization:
/// CMS-0057-F denial rationale and the appeals path both have to cite the
/// criteria that were applied, so this is an audit record, not a transient.
/// </summary>
public record CriteriaEvaluation
{
    public required string ProviderId                           { get; init; }
    public string? ProviderDisplayName                          { get; init; }
    public required CriteriaRecommendation Recommendation       { get; init; }

    public CriteriaGuidelineReference? Guideline                { get; init; }

    /// <summary>Why, in the provider's words. Shown to the reviewer and quoted in a denial.</summary>
    public IReadOnlyList<string> Rationale                      { get; init; } = [];

    /// <summary>What the guideline still needs answered before it can conclude.</summary>
    public IReadOnlyList<CriteriaQuestion> OutstandingQuestions  { get; init; } = [];

    /// <summary>Guideline-recommended length of stay in days, where the guideline gives one.</summary>
    public int? RecommendedLengthOfStayDays                     { get; init; }

    /// <summary>Days until the next concurrent review the guideline expects.</summary>
    public int? NextReviewInDays                                { get; init; }

    /// <summary>
    /// True when the outcome may not stand without a physician reviewer.
    /// Always true for <see cref="CriteriaRecommendation.DoesNotMeetCriteria"/>.
    /// </summary>
    public bool RequiresPhysicianReview                         { get; init; }

    /// <summary>
    /// Why there is no conclusion, when <see cref="Recommendation"/> is
    /// <see cref="CriteriaRecommendation.Indeterminate"/>: "not-configured",
    /// "timeout", "provider-error:…". Null on a conclusive evaluation.
    /// </summary>
    public string? FailureReason                                { get; init; }

    /// <summary>
    /// The provider's own response, verbatim, for the audit trail. A licensed
    /// connector stores what the vendor returned so a determination can be
    /// reconstructed years later, after the content version has moved on.
    /// </summary>
    public string? AuditPayload                                 { get; init; }

    public DateTimeOffset EvaluatedAtUtc                        { get; init; }
    public long ElapsedMs                                       { get; init; }

    /// <summary>True when a human has to look at this before anything is decided.</summary>
    public bool RoutesToReview =>
        Recommendation is CriteriaRecommendation.Indeterminate
                       or CriteriaRecommendation.DoesNotMeetCriteria
                       or CriteriaRecommendation.NoGuidelineFound
        || RequiresPhysicianReview;

    /// <summary>The fail-open answer: no conclusion, and a reason on the record.</summary>
    public static CriteriaEvaluation Inconclusive(
        string providerId, string failureReason, string? displayName = null) => new()
    {
        ProviderId          = providerId,
        ProviderDisplayName = displayName,
        Recommendation      = CriteriaRecommendation.Indeterminate,
        FailureReason       = failureReason,
        EvaluatedAtUtc      = DateTimeOffset.UtcNow,
    };
}

/// <summary>One registered provider, for an operations/diligence status endpoint.</summary>
public record CriteriaProviderStatus
{
    public required string ProviderId   { get; init; }
    public required string DisplayName  { get; init; }

    /// <summary>False when the provider is registered but has no usable configuration or licence.</summary>
    public required bool IsConfigured   { get; init; }

    /// <summary>True for the provider used when no routing rule matches.</summary>
    public required bool IsDefault      { get; init; }
}
