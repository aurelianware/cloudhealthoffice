namespace CloudHealthOffice.ClinicalCriteria.Domain;

/// <summary>
/// What the criteria provider concluded about medical necessity.
///
/// <see cref="Indeterminate"/> is the default and the fail-open value: a
/// provider that errors, times out, or is not configured yields it, and an
/// indeterminate evaluation always routes to a human reviewer. Clinical
/// criteria never deny on an infrastructure failure.
/// </summary>
public enum CriteriaRecommendation
{
    /// <summary>No conclusion. Route to clinical review.</summary>
    Indeterminate = 0,

    /// <summary>The request meets the guideline's criteria for the requested service.</summary>
    MeetsCriteria = 1,

    /// <summary>
    /// The request does not meet the guideline's criteria. Never an automatic
    /// denial on its own: an adverse determination needs a reviewer of the same
    /// or greater clinical qualification, so this routes to physician review.
    /// </summary>
    DoesNotMeetCriteria = 2,

    /// <summary>
    /// The service is excluded from the benefit, so clinical criteria do not
    /// apply. A coverage answer, not a clinical one.
    /// </summary>
    NotCovered = 3,

    /// <summary>
    /// The provider holds no guideline for this service, so it has nothing to
    /// say. Distinct from <see cref="Indeterminate"/>, which means it could not
    /// answer; this means there is nothing to answer with.
    /// </summary>
    NoGuidelineFound = 4
}

/// <summary>Where in the stay or episode the review sits.</summary>
public enum CriteriaReviewType
{
    /// <summary>Before the service — ordinary prior authorization.</summary>
    Prospective = 0,

    /// <summary>During an inpatient stay or a course of treatment.</summary>
    Concurrent = 1,

    /// <summary>After the service was rendered.</summary>
    Retrospective = 2,

    /// <summary>Re-review of an adverse determination under appeal.</summary>
    Appeal = 3
}
