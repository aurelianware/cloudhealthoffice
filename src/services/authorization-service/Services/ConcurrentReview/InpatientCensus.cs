using AuthorizationService.Models;

namespace AuthorizationService.Services.ConcurrentReview;

/// <summary>One stay as the census shows it.</summary>
public sealed record CensusRow
{
    public required string StayId                   { get; init; }
    public required string MemberId                 { get; init; }
    public string? MemberName                       { get; init; }
    public string? AuthorizationNumber              { get; init; }
    public required string FacilityNPI              { get; init; }
    public string? FacilityName                     { get; init; }
    public string? AttendingProviderName            { get; init; }
    public required DateTime AdmissionDate          { get; init; }
    public string? PrincipalDiagnosisCode           { get; init; }
    public string? LevelOfCare                      { get; init; }

    /// <summary>Days in the facility so far, counting the admission day.</summary>
    public required int ElapsedDays                 { get; init; }

    public required int CertifiedDays               { get; init; }
    public DateTime? CertifiedThroughDate           { get; init; }

    /// <summary>Elapsed days nobody has certified.</summary>
    public required int UncertifiedDays             { get; init; }

    public DateTime? NextReviewDueDate              { get; init; }

    /// <summary>Days until the next review; negative when it is overdue.</summary>
    public int? DaysUntilReview                     { get; init; }

    public required bool ReviewOverdue              { get; init; }
    public required bool CertificationLapsed        { get; init; }

    /// <summary>Reviews recorded so far, so the row shows whether anyone has looked.</summary>
    public required int ReviewCount                 { get; init; }
}

/// <summary>The census as of one date, with the counts a UM manager reads first.</summary>
public sealed record InpatientCensus
{
    public required DateTime AsOfDate               { get; init; }
    public required int TotalStays                  { get; init; }
    public required int ReviewsDueToday             { get; init; }
    public required int ReviewsOverdue              { get; init; }
    public required int StaysWithLapsedCertification { get; init; }
    public required IReadOnlyList<CensusRow> Stays  { get; init; }
}

/// <summary>
/// Builds the inpatient census: the screen a UM nurse opens in the morning.
///
/// Ordering is the point of it. Stays whose certification has already lapsed
/// come first, then overdue reviews, then reviews falling due soonest — so
/// the work that is costing the plan unauthorized bed days is at the top
/// rather than buried in an alphabetical list.
/// </summary>
public static class InpatientCensusBuilder
{
    public static InpatientCensus Build(IEnumerable<InpatientStay> stays, DateTime asOfUtc)
    {
        ArgumentNullException.ThrowIfNull(stays);

        var asOf = asOfUtc.Date;

        var rows = stays
            .Where(s => s.Status == InpatientStayStatus.Admitted)
            .Select(s => ToRow(s, asOf))
            .OrderByDescending(r => r.CertificationLapsed)
            .ThenByDescending(r => r.ReviewOverdue)
            .ThenBy(r => r.DaysUntilReview ?? int.MaxValue)
            .ThenBy(r => r.AdmissionDate)
            .ToList();

        return new InpatientCensus
        {
            AsOfDate                     = asOf,
            TotalStays                   = rows.Count,
            ReviewsDueToday              = rows.Count(r => r.DaysUntilReview == 0),
            ReviewsOverdue               = rows.Count(r => r.ReviewOverdue),
            StaysWithLapsedCertification = rows.Count(r => r.CertificationLapsed),
            Stays                        = rows,
        };
    }

    private static CensusRow ToRow(InpatientStay stay, DateTime asOf) => new()
    {
        StayId                 = stay.Id,
        MemberId               = stay.MemberId,
        MemberName             = FormatName(stay),
        AuthorizationNumber    = stay.AuthorizationNumber,
        FacilityNPI            = stay.FacilityNPI,
        FacilityName           = stay.FacilityName,
        AttendingProviderName  = stay.AttendingProviderName,
        AdmissionDate          = stay.AdmissionDate.Date,
        PrincipalDiagnosisCode = stay.PrincipalDiagnosisCode,
        LevelOfCare            = stay.LevelOfCare,
        ElapsedDays            = StayReviewPolicy.ElapsedDays(stay, asOf),
        CertifiedDays          = stay.CertifiedDays,
        CertifiedThroughDate   = stay.CertifiedThroughDate?.Date,
        UncertifiedDays        = StayReviewPolicy.UncertifiedDays(stay, asOf),
        NextReviewDueDate      = stay.NextReviewDueDate?.Date,
        DaysUntilReview        = stay.NextReviewDueDate is { } due ? (due.Date - asOf).Days : null,
        ReviewOverdue          = StayReviewPolicy.IsReviewOverdue(stay, asOf),
        CertificationLapsed    = StayReviewPolicy.IsCertificationLapsed(stay, asOf),
        ReviewCount            = stay.Reviews?.Count ?? 0,
    };

    private static string? FormatName(InpatientStay stay)
    {
        var name = $"{stay.MemberLastName}, {stay.MemberFirstName}".Trim(' ', ',');
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}
