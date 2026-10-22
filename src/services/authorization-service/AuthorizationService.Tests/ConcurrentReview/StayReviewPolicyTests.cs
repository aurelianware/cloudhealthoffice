using AuthorizationService.Models;
using AuthorizationService.Services.ConcurrentReview;

namespace AuthorizationService.Tests.ConcurrentReview;

public class StayReviewPolicyTests
{
    private static readonly DateTime Admission = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 10, 5, 9, 30, 0, DateTimeKind.Utc);

    private static InpatientStay Stay() => new()
    {
        TenantId = "t1",
        AuthorizationId = "auth-1",
        MemberId = "M1",
        FacilityNPI = "1234567893",
        AdmissionDate = Admission,
        LevelOfCare = "inpatient-acute",
        Status = InpatientStayStatus.Admitted,
    };

    private static ConcurrentReviewDecision Approve(int days, DateTime? nextReview = null) => new()
    {
        Outcome = ConcurrentReviewOutcome.Approved,
        DaysCertified = days,
        NextReviewDueDate = nextReview,
    };

    // ── Certification arithmetic ──────────────────────────────────

    [Fact]
    public void The_first_certification_starts_on_the_admission_day()
    {
        var stay = Stay();

        StayReviewPolicy.ApplyReview(stay, Approve(3), Now);

        stay.CertifiedThroughDate.Should().Be(new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc));
        stay.CertifiedDays.Should().Be(3);
    }

    [Fact]
    public void Each_further_review_begins_where_the_last_certification_ended()
    {
        var stay = Stay();
        StayReviewPolicy.ApplyReview(stay, Approve(3), Now);

        StayReviewPolicy.ApplyReview(stay, Approve(2), Now);

        stay.CertifiedThroughDate.Should().Be(new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc));
        stay.CertifiedDays.Should().Be(5, "certified days are contiguous and never overlap");
        stay.Reviews.Should().HaveCount(2);
        stay.Reviews[1].ReviewNumber.Should().Be(2);
    }

    [Fact]
    public void A_review_defaults_the_next_review_to_the_last_certified_day()
    {
        var stay = Stay();

        StayReviewPolicy.ApplyReview(stay, Approve(3), Now);

        stay.NextReviewDueDate.Should().Be(stay.CertifiedThroughDate);
    }

    [Fact]
    public void A_next_review_after_the_certification_runs_out_is_refused()
    {
        var stay = Stay();

        var act = () => StayReviewPolicy.ApplyReview(
            stay, Approve(2, nextReview: new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc)), Now);

        act.Should().Throw<ArgumentException>().WithMessage("*lapses unreviewed*");
    }

    [Fact]
    public void A_pend_certifies_nothing_and_leaves_the_certification_where_it_was()
    {
        var stay = Stay();
        StayReviewPolicy.ApplyReview(stay, Approve(3), Now);
        var certifiedThrough = stay.CertifiedThroughDate;

        StayReviewPolicy.ApplyReview(stay, new ConcurrentReviewDecision
        {
            Outcome = ConcurrentReviewOutcome.Pended,
        }, Now);

        stay.CertifiedThroughDate.Should().Be(certifiedThrough);
        stay.CertifiedDays.Should().Be(3);
        stay.NextReviewDueDate.Should().Be(certifiedThrough, "a pended stay stays on the worklist");
    }

    [Fact]
    public void A_denial_ends_the_review_cycle()
    {
        var stay = Stay();
        StayReviewPolicy.ApplyReview(stay, Approve(3), Now);

        StayReviewPolicy.ApplyReview(stay, new ConcurrentReviewDecision
        {
            Outcome = ConcurrentReviewOutcome.Denied,
            PhysicianReviewed = true,
            Reason = "No longer meets acute criteria.",
        }, Now);

        stay.NextReviewDueDate.Should().BeNull();
        stay.CertifiedDays.Should().Be(3, "a denial certifies nothing further");
    }

    [Fact]
    public void A_step_down_in_level_of_care_moves_the_stay()
    {
        var stay = Stay();

        StayReviewPolicy.ApplyReview(stay, Approve(2) with { LevelOfCare = "snf" }, Now);

        stay.LevelOfCare.Should().Be("snf");
        stay.Reviews.Single().LevelOfCare.Should().Be("snf");
    }

    // ── What the policy refuses ───────────────────────────────────

    [Fact]
    public void An_adverse_review_without_a_physician_is_refused()
    {
        var act = () => StayReviewPolicy.ApplyReview(Stay(), new ConcurrentReviewDecision
        {
            Outcome = ConcurrentReviewOutcome.Denied,
            Reason = "Does not meet criteria.",
        }, Now);

        act.Should().Throw<ArgumentException>().WithMessage("*physician reviewer*");
    }

    [Fact]
    public void An_adverse_review_without_a_reason_is_refused()
    {
        var act = () => StayReviewPolicy.ApplyReview(Stay(), new ConcurrentReviewDecision
        {
            Outcome = ConcurrentReviewOutcome.Modified,
            DaysCertified = 1,
            PhysicianReviewed = true,
        }, Now);

        act.Should().Throw<ArgumentException>().WithMessage("*reason*");
    }

    [Fact]
    public void An_approving_review_that_certifies_nothing_is_refused()
    {
        var act = () => StayReviewPolicy.ApplyReview(Stay(), Approve(0), Now);

        act.Should().Throw<ArgumentException>().WithMessage("*at least one day*");
    }

    [Fact]
    public void A_denied_review_that_certifies_days_is_refused()
    {
        var act = () => StayReviewPolicy.ApplyReview(Stay(), new ConcurrentReviewDecision
        {
            Outcome = ConcurrentReviewOutcome.Denied,
            DaysCertified = 2,
            PhysicianReviewed = true,
            Reason = "No.",
        }, Now);

        act.Should().Throw<ArgumentException>().WithMessage("*cannot certify days*");
    }

    [Fact]
    public void A_discharged_stay_cannot_be_reviewed()
    {
        var stay = Stay();
        StayReviewPolicy.Discharge(stay, new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc), "01", Now);

        var act = () => StayReviewPolicy.ApplyReview(stay, Approve(1), Now);

        act.Should().Throw<InpatientStayTransitionException>();
    }

    // ── Discharge ─────────────────────────────────────────────────

    [Fact]
    public void A_discharge_closes_the_stay_and_clears_the_review_date()
    {
        var stay = Stay();
        StayReviewPolicy.ApplyReview(stay, Approve(5), Now);

        StayReviewPolicy.Discharge(stay, new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc), "03", Now);

        stay.Status.Should().Be(InpatientStayStatus.Discharged);
        stay.DischargeDisposition.Should().Be("03");
        stay.NextReviewDueDate.Should().BeNull();
    }

    [Fact]
    public void A_discharge_before_the_admission_is_refused()
    {
        var act = () => StayReviewPolicy.Discharge(
            Stay(), new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc), "01", Now);

        act.Should().Throw<ArgumentException>().WithMessage("*precede the admission*");
    }

    [Fact]
    public void A_discharge_in_the_future_is_refused()
    {
        var act = () => StayReviewPolicy.Discharge(
            Stay(), new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc), "01", Now);

        act.Should().Throw<ArgumentException>().WithMessage("*in the future*");
    }

    [Fact]
    public void A_stay_cannot_be_discharged_twice()
    {
        var stay = Stay();
        StayReviewPolicy.Discharge(stay, new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc), "01", Now);

        var act = () => StayReviewPolicy.Discharge(stay, new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc), "01", Now);

        act.Should().Throw<InpatientStayTransitionException>();
    }

    // ── Derived day counts ────────────────────────────────────────

    [Fact]
    public void Elapsed_days_count_the_admission_day()
    {
        StayReviewPolicy.ElapsedDays(Stay(), Now).Should().Be(5, "1 October through 5 October inclusive");
    }

    [Fact]
    public void Elapsed_days_stop_at_the_discharge()
    {
        var stay = Stay();
        StayReviewPolicy.Discharge(stay, new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc), "01", Now);

        StayReviewPolicy.ElapsedDays(stay, Now).Should().Be(3);
    }

    [Fact]
    public void Uncertified_days_are_the_elapsed_days_nobody_certified()
    {
        var stay = Stay();
        StayReviewPolicy.ApplyReview(stay, Approve(2), Now);

        StayReviewPolicy.UncertifiedDays(stay, Now).Should().Be(3, "five days elapsed, two certified");
    }

    [Fact]
    public void Uncertified_days_never_go_negative()
    {
        var stay = Stay();
        StayReviewPolicy.ApplyReview(stay, Approve(10), Now);

        StayReviewPolicy.UncertifiedDays(stay, Now).Should().Be(0);
    }

    [Fact]
    public void A_certification_that_has_run_out_behind_the_member_is_lapsed()
    {
        var stay = Stay();
        StayReviewPolicy.ApplyReview(stay, Approve(2), Now);

        StayReviewPolicy.IsCertificationLapsed(stay, Now).Should().BeTrue();
        StayReviewPolicy.IsReviewOverdue(stay, Now).Should().BeTrue();
    }

    [Fact]
    public void A_stay_certified_past_today_is_neither_lapsed_nor_overdue()
    {
        var stay = Stay();
        StayReviewPolicy.ApplyReview(stay, Approve(8), Now);

        StayReviewPolicy.IsCertificationLapsed(stay, Now).Should().BeFalse();
        StayReviewPolicy.IsReviewOverdue(stay, Now).Should().BeFalse();
    }

    [Fact]
    public void A_discharged_stay_is_never_overdue_or_lapsed()
    {
        var stay = Stay();
        StayReviewPolicy.Discharge(stay, new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc), "01", Now);

        StayReviewPolicy.IsReviewOverdue(stay, Now).Should().BeFalse();
        StayReviewPolicy.IsCertificationLapsed(stay, Now).Should().BeFalse();
    }
}
