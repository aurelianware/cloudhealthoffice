namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// The subscriber search criteria a 270 request carries (2100C NM1/DMG).
/// X12 5010 270 lets the information source be searched by
/// (a) member ID alone, (b) member ID plus name, or
/// (c) last name + first name + date of birth with no ID.
/// </summary>
public class Eligibility270Criteria
{
    public string SubscriberId { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public DateTime? DateOfBirth { get; set; }
    public DateTime? ServiceDate { get; set; } = DateTime.Today;
    public string ServiceType { get; set; } = "30";

    public bool HasId => !string.IsNullOrWhiteSpace(SubscriberId);

    public bool HasNameAndDob =>
        !string.IsNullOrWhiteSpace(LastName)
        && !string.IsNullOrWhiteSpace(FirstName)
        && DateOfBirth.HasValue;
}

/// <summary>Outcome of resolving a name + DOB 270 search to a member.</summary>
public enum MemberResolutionStatus
{
    Resolved,
    Ambiguous,
    NotFound
}

public sealed record MemberResolution(MemberResolutionStatus Status, IReadOnlyList<MemberSummary> Candidates)
{
    public MemberSummary? Member => Status == MemberResolutionStatus.Resolved ? Candidates[0] : null;
}

/// <summary>
/// Rules behind the 270/271 Eligibility Simulator page. Kept out of the
/// Razor file so the search-option validation and the name + DOB member
/// resolution are unit-testable on their own.
/// </summary>
public static class EligibilitySimulator
{
    public const string CriteriaRequiredMessage =
        "Enter a Member ID, or Last Name + First Name + Date of Birth (X12 270 alternate search).";

    /// <summary>
    /// AAA03 = 75 "Subscriber/Insured Not Found" — what a 271 returns when
    /// the 2100C search keys match no one.
    /// </summary>
    public const string SubscriberNotFoundReason =
        "AAA*Y**75*C — Subscriber/Insured Not Found. No member matched the last name, first name and date of birth submitted.";

    /// <summary>
    /// Returns null when the criteria form a valid 270 search, otherwise
    /// the message to show. An ID alone is enough; without an ID, last
    /// name, first name and DOB are all required.
    /// </summary>
    public static string? Validate(Eligibility270Criteria criteria)
    {
        if (criteria.HasId || criteria.HasNameAndDob)
        {
            return null;
        }
        return CriteriaRequiredMessage;
    }

    /// <summary>
    /// The backend inquiry (eligibility-service POST /eligibility/inquiry)
    /// keys coverage lookup on SubscriberId only, so a name + DOB search is
    /// resolved here first: search members by last name, keep exact
    /// (case-insensitive) last-name + DOB matches, then narrow by first name
    /// when that leaves at least one.
    /// </summary>
    public static async Task<MemberResolution> ResolveByNameAndDobAsync(
        IMemberService memberService, string firstName, string lastName, DateTime dateOfBirth)
    {
        var last = lastName.Trim();
        var first = firstName.Trim();
        var results = await memberService.SearchMembersAsync(last);

        var matches = results
            .Where(m => string.Equals(m.LastName?.Trim(), last, StringComparison.OrdinalIgnoreCase)
                        && m.DateOfBirth.Date == dateOfBirth.Date)
            .GroupBy(m => m.MemberId)
            .Select(g => g.First())
            .ToList();

        if (!string.IsNullOrEmpty(first))
        {
            var byFirst = matches
                .Where(m => string.Equals(m.FirstName?.Trim(), first, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (byFirst.Count > 0)
            {
                matches = byFirst;
            }
        }

        return matches.Count switch
        {
            0 => new MemberResolution(MemberResolutionStatus.NotFound, matches),
            1 => new MemberResolution(MemberResolutionStatus.Resolved, matches),
            _ => new MemberResolution(MemberResolutionStatus.Ambiguous, matches)
        };
    }

    /// <summary>
    /// Builds the inquiry body using eligibility-service's
    /// <c>EligibilityInquiry</c> property names. DOB is omitted when unknown
    /// because the backend field is a non-nullable DateTime.
    /// </summary>
    public static Dictionary<string, object?> BuildInquiry(Eligibility270Criteria criteria, string subscriberId)
    {
        var body = new Dictionary<string, object?>
        {
            ["subscriberId"] = subscriberId,
            ["subscriberFirstName"] = criteria.FirstName?.Trim() ?? string.Empty,
            ["subscriberLastName"] = criteria.LastName?.Trim() ?? string.Empty,
            ["serviceTypeCode"] = criteria.ServiceType,
            ["serviceDateFrom"] = criteria.ServiceDate,
        };
        if (criteria.DateOfBirth.HasValue)
        {
            body["subscriberDOB"] = criteria.DateOfBirth.Value.Date;
        }
        return body;
    }

    /// <summary>The 271-style rejection shown when a name + DOB search matches no member.</summary>
    public static EligibilityResponse SubscriberNotFound() => new()
    {
        IsCovered = false,
        StatusCode = "75",
        RejectionReason = SubscriberNotFoundReason
    };
}
