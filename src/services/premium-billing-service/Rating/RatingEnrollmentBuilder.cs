using PremiumBillingService.Clients;

namespace PremiumBillingService.Rating;

/// <summary>A coverage that could not be turned into a rated household.</summary>
public sealed record EnrollmentBuildIssue(string? CoverageId, string? MemberId, string Message);

public sealed class RatingEnrollmentBuild
{
    public List<RatingEnrollment> Enrollments { get; } = new();
    public List<EnrollmentBuildIssue> Issues { get; } = new();

    /// <summary>
    /// Subscriber coverages left out because one of their members could not
    /// be rated. They are neither charged nor reconciled (a missing dependent
    /// would bill the wrong tier, and a missing household would credit the
    /// months it was billed); the issue keeps the invoice in Draft.
    /// </summary>
    public HashSet<string> ExcludedCoverageIds { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Turns coverage-service records (one per covered member) and member-service's
/// rating census into households for the rating engine: one
/// <see cref="RatingEnrollment"/> per subscriber coverage record, with the
/// dependents covered under the same plan and line attached with their own
/// coverage dates. Pure: no I/O.
/// </summary>
public static class RatingEnrollmentBuilder
{
    public static RatingEnrollmentBuild Build(IEnumerable<CoverageDto> coverages, IEnumerable<RatingCensusMemberDto> census,
        DateTime windowStart, DateTime windowEnd)
    {
        var build = new RatingEnrollmentBuild();
        var members = new Dictionary<string, RatingCensusMemberDto>(StringComparer.Ordinal);
        foreach (var member in census)
            members.TryAdd(member.MemberId, member);

        var relevant = coverages
            .GroupBy(c => c.CoverageId, StringComparer.Ordinal).Select(g => g.First())
            // A coverage ending before it starts was cancelled: it never covered a day.
            .Where(c => c.TerminationDate is not { } t || t.Date >= c.EffectiveDate.Date)
            .Where(c => c.EffectiveDate.Date <= windowEnd.Date && (c.TerminationDate is not { } t2 || t2.Date >= windowStart.Date))
            .OrderBy(c => c.EffectiveDate).ThenBy(c => c.CoverageId, StringComparer.Ordinal)
            .ToList();

        var dependents = new List<(CoverageDto Coverage, RatedMember Member, string SubscriberId)>();
        var subscriberCoverages = new List<(CoverageDto Coverage, RatingEnrollment Enrollment, string SubscriberId)>();

        foreach (var coverage in relevant)
        {
            if (string.IsNullOrWhiteSpace(coverage.PlanId))
            {
                build.Issues.Add(new(coverage.CoverageId, coverage.MemberId, $"Coverage {coverage.CoverageId} has no plan"));
                continue;
            }
            if (!members.TryGetValue(coverage.MemberId, out var person))
            {
                build.Issues.Add(new(coverage.CoverageId, coverage.MemberId,
                    $"Member {coverage.MemberId} of coverage {coverage.CoverageId} is not in the group's member census"));
                continue;
            }
            if (person.DateOfBirth is not { } dob || dob == default)
            {
                build.Issues.Add(new(coverage.CoverageId, coverage.MemberId, $"Member {coverage.MemberId} has no date of birth"));
                MarkSubscriberExcluded(person, coverage);
                continue;
            }

            var relationship = Relationship(person, out var problem);
            if (relationship == null)
            {
                build.Issues.Add(new(coverage.CoverageId, coverage.MemberId, problem!));
                MarkSubscriberExcluded(person, coverage);
                continue;
            }

            var rated = new RatedMember
            {
                MemberId = person.MemberId,
                MemberName = Name(person),
                Relationship = relationship.Value,
                DateOfBirth = DateTime.SpecifyKind(dob.Date, DateTimeKind.Utc),
                TobaccoUser = person.TobaccoUser == true
            };

            if (relationship == MemberRelationship.Subscriber)
            {
                var clash = subscriberCoverages.FirstOrDefault(s => s.SubscriberId == person.MemberId
                    && SameProduct(s.Coverage, coverage) && Overlaps(s.Coverage, coverage));
                if (clash.Enrollment != null)
                {
                    build.Issues.Add(new(coverage.CoverageId, coverage.MemberId,
                        $"Coverages {clash.Coverage.CoverageId} and {coverage.CoverageId} cover subscriber {person.MemberId} under plan {coverage.PlanId} on the same days"));
                    build.ExcludedCoverageIds.Add(clash.Coverage.CoverageId);
                    build.ExcludedCoverageIds.Add(coverage.CoverageId);
                    continue;
                }
                var enrollment = new RatingEnrollment
                {
                    CoverageId = coverage.CoverageId,
                    PlanId = coverage.PlanId!,
                    EffectiveDate = Utc(coverage.EffectiveDate),
                    TerminationDate = coverage.TerminationDate.HasValue ? Utc(coverage.TerminationDate.Value) : null,
                    InsuranceLineCode = coverage.InsuranceLineCode,
                    Members = new List<RatedMember> { rated }
                };
                subscriberCoverages.Add((coverage, enrollment, person.MemberId));
            }
            else
            {
                rated.EffectiveDate = Utc(coverage.EffectiveDate);
                rated.TerminationDate = coverage.TerminationDate.HasValue ? Utc(coverage.TerminationDate.Value) : null;
                dependents.Add((coverage, rated, person.SubscriberMemberId!));
            }
        }

        foreach (var (coverage, member, subscriberId) in dependents)
        {
            var households = subscriberCoverages
                .Where(s => s.SubscriberId == subscriberId && SameProduct(s.Coverage, coverage) && Overlaps(s.Coverage, coverage))
                .ToList();
            if (households.Count == 0)
            {
                build.Issues.Add(new(coverage.CoverageId, coverage.MemberId,
                    $"Dependent {member.MemberId} (coverage {coverage.CoverageId}) has no subscriber coverage of {subscriberId} under plan {coverage.PlanId} on the same days"));
                continue;
            }
            foreach (var household in households)
            {
                var twice = household.Enrollment.Members.Any(m => m.MemberId == member.MemberId
                    && (m.EffectiveDate ?? DateTime.MinValue) <= (member.TerminationDate ?? DateTime.MaxValue)
                    && (member.EffectiveDate ?? DateTime.MinValue) <= (m.TerminationDate ?? DateTime.MaxValue));
                if (twice)
                {
                    build.Issues.Add(new(coverage.CoverageId, coverage.MemberId,
                        $"Dependent {member.MemberId} is covered twice under coverage {household.Coverage.CoverageId} on the same days"));
                    build.ExcludedCoverageIds.Add(household.Coverage.CoverageId);
                    continue;
                }
                household.Enrollment.Members.Add(new RatedMember
                {
                    MemberId = member.MemberId,
                    MemberName = member.MemberName,
                    Relationship = member.Relationship,
                    DateOfBirth = member.DateOfBirth,
                    TobaccoUser = member.TobaccoUser,
                    EffectiveDate = member.EffectiveDate,
                    TerminationDate = member.TerminationDate
                });
            }
        }

        foreach (var (_, enrollment, _) in subscriberCoverages)
        {
            if (!build.ExcludedCoverageIds.Contains(enrollment.CoverageId))
                build.Enrollments.Add(enrollment);
        }
        return build;

        // A member that cannot be rated taints the subscriber coverages it belongs to.
        void MarkSubscriberExcluded(RatingCensusMemberDto person, CoverageDto coverage)
        {
            var subscriberId = person.IsSubscriber ? person.MemberId : person.SubscriberMemberId;
            if (subscriberId == null)
                return;
            foreach (var other in relevant)
            {
                if (other.MemberId == subscriberId && SameProduct(other, coverage) && Overlaps(other, coverage))
                    build.ExcludedCoverageIds.Add(other.CoverageId);
            }
        }
    }

    /// <summary>
    /// X12 834 INS02 to a rating relationship: 18 self; 01 spouse and 53 life
    /// partner; 19 child, 09 adopted, 10 foster, 17 stepchild, 05 grandchild,
    /// 15 ward rate as children; any other code is another dependent (rated as a child).
    /// </summary>
    public static MemberRelationship? Relationship(RatingCensusMemberDto person, out string? problem)
    {
        problem = null;
        var code = person.RelationshipCode?.Trim();
        if (person.IsSubscriber)
        {
            if (code is not (null or "" or "18"))
            {
                problem = $"Member {person.MemberId} is a subscriber with relationship code {code}";
                return null;
            }
            return MemberRelationship.Subscriber;
        }

        if (string.IsNullOrEmpty(person.SubscriberMemberId))
        {
            problem = $"Dependent {person.MemberId} has no subscriber";
            return null;
        }
        switch (code)
        {
            case null or "":
                problem = $"Dependent {person.MemberId} has no relationship code";
                return null;
            case "18":
                problem = $"Dependent {person.MemberId} has relationship code 18 (self)";
                return null;
            case "01" or "53":
                return MemberRelationship.Spouse;
            case "19" or "09" or "10" or "17" or "05" or "15":
                return MemberRelationship.Child;
            default:
                return MemberRelationship.OtherDependent;
        }
    }

    private static bool SameProduct(CoverageDto a, CoverageDto b) =>
        string.Equals(a.PlanId, b.PlanId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.InsuranceLineCode ?? string.Empty, b.InsuranceLineCode ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private static bool Overlaps(CoverageDto a, CoverageDto b) =>
        a.EffectiveDate.Date <= (b.TerminationDate?.Date ?? DateTime.MaxValue.Date)
        && b.EffectiveDate.Date <= (a.TerminationDate?.Date ?? DateTime.MaxValue.Date);

    private static DateTime Utc(DateTime date) => DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);

    private static string? Name(RatingCensusMemberDto person)
    {
        var name = $"{person.FirstName} {person.LastName}".Trim();
        return name.Length == 0 ? null : name;
    }
}
