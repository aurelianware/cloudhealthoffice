using AuthorizationService.Models;
using AuthorizationService.Repositories;
using CloudHealthOffice.ClinicalCriteria.Abstractions;
using CloudHealthOffice.ClinicalCriteria.Domain;
using CloudHealthOffice.ClinicalCriteria.Models;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;

namespace AuthorizationService.Services.ConcurrentReview;

/// <summary>The stay a caller asked to open, before it has been checked.</summary>
public sealed record AdmitStayRequest
{
    public required string AuthorizationId  { get; init; }
    public required DateTime AdmissionDate  { get; init; }
    public required string FacilityNPI      { get; init; }
    public string? FacilityName             { get; init; }
    public string? AttendingProviderNPI     { get; init; }
    public string? AttendingProviderName    { get; init; }
    public string? AdmissionType            { get; init; }
    public string? AdmissionSource          { get; init; }
    public string? PrincipalDiagnosisCode   { get; init; }
    public string? LevelOfCare              { get; init; }
    public string? Notes                    { get; init; }
}

/// <summary>Concurrent review, with the criteria call attached where one was asked for.</summary>
public interface IConcurrentReviewService
{
    Task<InpatientStay> AdmitAsync(AdmitStayRequest request, string actor, CancellationToken ct = default);

    /// <summary>
    /// Evaluates clinical criteria for the stay as it currently stands, without
    /// deciding anything. What a reviewer opens before certifying days.
    /// </summary>
    Task<CriteriaEvaluation> EvaluateCriteriaAsync(InpatientStay stay, CancellationToken ct = default);

    Task<InpatientStay> RecordReviewAsync(
        InpatientStay stay, ConcurrentReviewDecision decision, bool evaluateCriteria,
        string actor, CancellationToken ct = default);

    Task<InpatientStay> DischargeAsync(
        InpatientStay stay, DateTime dischargeDate, string? disposition, string actor, CancellationToken ct = default);

    Task<InpatientCensus> GetCensusAsync(DateTime asOfUtc, string? facilityNpi, CancellationToken ct = default);
}

/// <summary>
/// Concurrent review over the stay store.
///
/// The service's one real job beyond persistence is to put the criteria
/// evaluation and the reviewer's decision on the same record. A reviewer may
/// certify days against a criteria recommendation — clinical judgement is
/// theirs — but what the guidelines said, and which guideline version said
/// it, is recorded either way, because that is what an appeal or an audit
/// asks for.
/// </summary>
public sealed class ConcurrentReviewService(
    IInpatientStayRepository stays,
    IAuthorizationRepository authorizations,
    IClinicalCriteriaService criteria,
    TimeProvider? timeProvider = null) : IConcurrentReviewService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<InpatientStay> AdmitAsync(
        AdmitStayRequest request, string actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var authorization = await authorizations.GetByIdAsync(request.AuthorizationId)
            ?? throw new KeyNotFoundException($"Authorization {request.AuthorizationId} was not found.");

        var now = _clock.GetUtcNow().UtcDateTime;

        if (request.AdmissionDate.Date > now.Date)
            throw new ArgumentException("An admission cannot be in the future.");

        var stay = new InpatientStay
        {
            AuthorizationId        = authorization.Id,
            AuthorizationNumber    = authorization.AuthorizationNumber,
            MemberId               = authorization.MemberId,
            MemberFirstName        = authorization.PatientFirstName,
            MemberLastName         = authorization.PatientLastName,
            LineOfBusiness         = authorization.LineOfBusiness,
            FacilityNPI            = request.FacilityNPI,
            FacilityName           = request.FacilityName ?? authorization.FacilityName,
            AttendingProviderNPI   = request.AttendingProviderNPI ?? authorization.ServicingProviderNPI,
            AttendingProviderName  = request.AttendingProviderName ?? authorization.ServicingProviderName,
            AdmissionDate          = request.AdmissionDate.Date,
            AdmissionType          = request.AdmissionType,
            AdmissionSource        = request.AdmissionSource,
            PrincipalDiagnosisCode = request.PrincipalDiagnosisCode
                                     ?? authorization.DiagnosisCodes?.FirstOrDefault()?.Code,
            LevelOfCare            = request.LevelOfCare,
            Notes                  = request.Notes,
            Status                 = InpatientStayStatus.Admitted,
            CreatedBy              = actor,
            LastUpdatedBy          = actor,
        };

        // An approved authorization has already certified a period. Carrying it
        // onto the stay is what makes the first concurrent review an extension
        // rather than a first certification: the census shows day one as
        // covered, and the reviewer is asked for the next days, not the ones
        // the prior authorization already granted.
        var approvedThrough = authorization.ApprovedServiceDateTo ?? authorization.ExpirationDate;
        if (authorization.Status is AuthorizationStatus.Approved or AuthorizationStatus.Modified
            && approvedThrough?.Date >= stay.AdmissionDate)
        {
            stay.CertifiedThroughDate = approvedThrough.Value.Date;
            stay.CertifiedDays = (approvedThrough.Value.Date - stay.AdmissionDate).Days + 1;
            stay.NextReviewDueDate = approvedThrough.Value.Date;
        }
        else
        {
            // Nothing is certified, so the stay is on today's worklist.
            stay.NextReviewDueDate = now.Date;
        }

        return await stays.CreateAsync(stay, ct);
    }

    public Task<CriteriaEvaluation> EvaluateCriteriaAsync(InpatientStay stay, CancellationToken ct = default) =>
        criteria.EvaluateAsync(ToCriteriaRequest(stay), ct);

    public async Task<InpatientStay> RecordReviewAsync(
        InpatientStay stay, ConcurrentReviewDecision decision, bool evaluateCriteria,
        string actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stay);
        ArgumentNullException.ThrowIfNull(decision);

        if (evaluateCriteria)
        {
            var evaluation = await criteria.EvaluateAsync(ToCriteriaRequest(stay), ct);

            // The reviewer's own citation wins where they supplied one: they
            // may be recording a decision made against a guideline consulted
            // outside CHO.
            decision = decision with
            {
                CriteriaProviderId       = decision.CriteriaProviderId ?? evaluation.ProviderId,
                CriteriaGuidelineId      = decision.CriteriaGuidelineId ?? evaluation.Guideline?.GuidelineId,
                CriteriaGuidelineVersion = decision.CriteriaGuidelineVersion ?? evaluation.Guideline?.Version,
                CriteriaRecommendation   = decision.CriteriaRecommendation ?? evaluation.Recommendation.ToString(),
                CriteriaRationale        = decision.CriteriaRationale.Count > 0
                                             ? decision.CriteriaRationale
                                             : evaluation.Rationale,
            };
        }

        StayReviewPolicy.ApplyReview(stay, decision, _clock.GetUtcNow().UtcDateTime, actor);

        return await stays.UpdateAsync(stay, ct);
    }

    public async Task<InpatientStay> DischargeAsync(
        InpatientStay stay, DateTime dischargeDate, string? disposition, string actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stay);

        StayReviewPolicy.Discharge(stay, dischargeDate, disposition, _clock.GetUtcNow().UtcDateTime, actor);

        return await stays.UpdateAsync(stay, ct);
    }

    public async Task<InpatientCensus> GetCensusAsync(
        DateTime asOfUtc, string? facilityNpi, CancellationToken ct = default) =>
        InpatientCensusBuilder.Build(await stays.ListOpenAsync(facilityNpi, ct), asOfUtc);

    /// <summary>
    /// The stay as the criteria seam sees it. The state code is not on the
    /// authorization record, so it comes from the facility's own state when
    /// one is known; until provider-service is consulted for it, routing by
    /// state falls through to the configured default provider.
    /// </summary>
    private static CriteriaRequest ToCriteriaRequest(InpatientStay stay)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var admitted = DateOnly.FromDateTime(stay.AdmissionDate.Date);

        return new CriteriaRequest
        {
            TenantId              = stay.TenantId,
            StateCode             = string.Empty,
            // The two enums share their numeric values by contract (see
            // PaLineOfBusiness). LineOfBusiness.Unknown has no counterpart and
            // maps to no rule set, so an unknown LOB reaches no conclusion and
            // pends — which is the right answer for one.
            Lob                   = (PaLineOfBusiness)(int)stay.LineOfBusiness,
            ReviewType            = CriteriaReviewType.Concurrent,
            AuthorizationId       = stay.AuthorizationId,
            MemberId              = stay.MemberId,
            ServiceDate           = today,
            ProcedureCodes        = [],
            DiagnosisCodes        = stay.PrincipalDiagnosisCode is { Length: > 0 } dx ? [dx] : [],
            PlaceOfServiceCode    = "21",
            LevelOfCare           = stay.LevelOfCare,
            StayDay               = today.DayNumber - admitted.DayNumber + 1,
            RequestingProviderNpi = stay.AttendingProviderNPI ?? stay.FacilityNPI,
            ServicingProviderNpi  = stay.FacilityNPI,
            FacilityNpi           = stay.FacilityNPI,
        };
    }
}
