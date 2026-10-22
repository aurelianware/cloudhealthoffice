using AuthorizationService.Models;

namespace AuthorizationService.Services.CareManagement;

/// <summary>A discharge that may warrant a transitions-of-care referral.</summary>
public sealed record CareReferralCommand
{
    public required string TenantId         { get; init; }
    public required string MemberId         { get; init; }
    public string? MemberFirstName          { get; init; }
    public string? MemberLastName           { get; init; }
    public required string StayId           { get; init; }
    public required DateTime AdmissionDate  { get; init; }
    public required DateTime DischargeDate  { get; init; }
    public string? DischargeDisposition     { get; init; }
    public string? PrincipalDiagnosisCode   { get; init; }
    public string? FacilityName             { get; init; }

    /// <summary>Days in the facility. Length of stay is the usual acuity signal at discharge.</summary>
    public required int LengthOfStayDays    { get; init; }

    /// <summary>Acuity the new case should start at, as care management names them.</summary>
    public required string Acuity           { get; init; }
}

/// <summary>
/// Refers a discharged member to care management.
///
/// Best-effort by contract: the discharge is recorded whether or not the
/// referral lands. A care management service that is down must not stop a UM
/// nurse closing a stay.
/// </summary>
public interface ICareReferralGateway
{
    /// <summary>Refers the member and returns the case id, or null when nothing was referred.</summary>
    Task<string?> ReferOnDischargeAsync(CareReferralCommand command, CancellationToken ct = default);
}

/// <summary>Used when no care-management-service is configured. Refers nothing.</summary>
public sealed class NullCareReferralGateway : ICareReferralGateway
{
    public Task<string?> ReferOnDischargeAsync(CareReferralCommand command, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);
}

/// <summary>
/// Which discharges are worth a transitions-of-care referral, and how urgent
/// the case is.
///
/// Kept apart from the transport so the judgement is testable and can be
/// argued with. The rule is deliberately plain: a discharge to home after a
/// short uncomplicated stay is the one case that does not need a care
/// manager, and everything else does. A plan that wants a narrower rule
/// changes it here rather than in an HTTP client.
/// </summary>
public static class CareReferralPolicy
{
    /// <summary>Stays at or below this many days, discharged home, are not referred.</summary>
    public const int ShortStayDays = 2;

    /// <summary>UB-04 FL17 discharge statuses that mean the member went home without services.</summary>
    private static readonly HashSet<string> DischargedHome = new(StringComparer.OrdinalIgnoreCase) { "01" };

    /// <summary>UB-04 FL17 statuses where a referral would have no one to refer.</summary>
    private static readonly HashSet<string> NoMemberToRefer = new(StringComparer.OrdinalIgnoreCase)
    {
        "20", // expired
        "40", // expired at home
        "41", // expired in a medical facility
        "42", // expired, place unknown
        "07", // left against medical advice — a referral belongs to outreach, not transitions of care
    };

    public static bool ShouldRefer(InpatientStay stay, int lengthOfStayDays)
    {
        ArgumentNullException.ThrowIfNull(stay);

        var disposition = stay.DischargeDisposition;

        if (disposition is { Length: > 0 } && NoMemberToRefer.Contains(disposition)) return false;

        // Home after a short, uncomplicated stay: nothing for a care manager to do.
        if (lengthOfStayDays <= ShortStayDays
            && disposition is { Length: > 0 } home && DischargedHome.Contains(home)) return false;

        return true;
    }

    /// <summary>
    /// How urgent the new case is. A discharge to another facility or with
    /// services, or a long stay, is where readmissions come from.
    /// </summary>
    public static string AcuityFor(InpatientStay stay, int lengthOfStayDays)
    {
        var disposition = stay.DischargeDisposition;

        if (lengthOfStayDays >= 10) return "High";
        if (disposition is { Length: > 0 } && !DischargedHome.Contains(disposition)) return "High";
        if (lengthOfStayDays >= 5) return "Moderate";
        return "Low";
    }
}
