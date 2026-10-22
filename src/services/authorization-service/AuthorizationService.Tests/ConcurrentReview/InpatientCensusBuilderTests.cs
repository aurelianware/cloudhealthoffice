using AuthorizationService.Models;
using AuthorizationService.Services.ConcurrentReview;

namespace AuthorizationService.Tests.ConcurrentReview;

public class InpatientCensusBuilderTests
{
    private static readonly DateTime AsOf = new(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc);

    private static InpatientStay Stay(
        string id,
        int admittedDaysAgo,
        int certifiedDays,
        int? reviewDueInDays,
        InpatientStayStatus status = InpatientStayStatus.Admitted)
    {
        var admission = AsOf.Date.AddDays(-admittedDaysAgo);
        return new InpatientStay
        {
            Id = id,
            TenantId = "t1",
            AuthorizationId = "auth-1",
            MemberId = $"M-{id}",
            MemberFirstName = "Dana",
            MemberLastName = "Reyes",
            FacilityNPI = "1234567893",
            AdmissionDate = admission,
            Status = status,
            CertifiedDays = certifiedDays,
            CertifiedThroughDate = certifiedDays > 0 ? admission.AddDays(certifiedDays - 1) : null,
            NextReviewDueDate = reviewDueInDays is { } d ? AsOf.Date.AddDays(d) : null,
        };
    }

    [Fact]
    public void Only_open_stays_are_on_the_census()
    {
        var census = InpatientCensusBuilder.Build(
            [
                Stay("open", 2, 3, 1),
                Stay("gone", 5, 5, null, InpatientStayStatus.Discharged),
                Stay("void", 5, 0, null, InpatientStayStatus.Cancelled),
            ],
            AsOf);

        census.TotalStays.Should().Be(1);
        census.Stays.Single().StayId.Should().Be("open");
    }

    [Fact]
    public void Lapsed_certifications_sort_above_everything_else()
    {
        var census = InpatientCensusBuilder.Build(
            [
                Stay("due-tomorrow", 1, 3, 1),
                // Certified past today, so overdue on its review date alone.
                Stay("overdue", 6, 10, -2),
                // Certified only through four days ago: the member is in a bed
                // on days nobody has paid for, which outranks an overdue review.
                Stay("lapsed", 6, 2, 3),
            ],
            AsOf);

        census.Stays.Select(s => s.StayId).Should().ContainInOrder("lapsed", "overdue", "due-tomorrow");
    }

    [Fact]
    public void The_counts_say_what_the_morning_looks_like()
    {
        var census = InpatientCensusBuilder.Build(
            [
                Stay("today", 3, 10, 0),
                Stay("overdue", 6, 10, -1),
                Stay("lapsed", 6, 2, 2),
                Stay("fine", 1, 10, 5),
            ],
            AsOf);

        census.AsOfDate.Should().Be(AsOf.Date);
        census.TotalStays.Should().Be(4);
        census.ReviewsDueToday.Should().Be(1);
        census.ReviewsOverdue.Should().Be(1);
        census.StaysWithLapsedCertification.Should().Be(1);
    }

    [Fact]
    public void A_row_shows_the_days_the_plan_has_not_certified()
    {
        var census = InpatientCensusBuilder.Build([Stay("s1", 6, 3, 1)], AsOf);

        var row = census.Stays.Single();
        row.ElapsedDays.Should().Be(7, "admitted six days ago, counting today");
        row.CertifiedDays.Should().Be(3);
        row.UncertifiedDays.Should().Be(4);
        row.DaysUntilReview.Should().Be(1);
        row.MemberName.Should().Be("Reyes, Dana");
    }

    [Fact]
    public void A_stay_with_no_review_date_sorts_last_rather_than_first()
    {
        var census = InpatientCensusBuilder.Build(
            [
                Stay("no-date", 1, 10, null),
                Stay("due-later", 1, 10, 4),
            ],
            AsOf);

        census.Stays.Select(s => s.StayId).Should().ContainInOrder("due-later", "no-date");
    }

    [Fact]
    public void An_empty_census_is_still_a_census()
    {
        var census = InpatientCensusBuilder.Build([], AsOf);

        census.TotalStays.Should().Be(0);
        census.Stays.Should().BeEmpty();
        census.ReviewsOverdue.Should().Be(0);
    }
}
