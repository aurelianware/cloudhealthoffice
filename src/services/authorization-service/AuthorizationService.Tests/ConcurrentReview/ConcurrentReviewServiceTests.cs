using AuthorizationService.Models;
using AuthorizationService.Repositories;
using AuthorizationService.Services.ConcurrentReview;
using CloudHealthOffice.ClinicalCriteria.Abstractions;
using CloudHealthOffice.ClinicalCriteria.Domain;
using CloudHealthOffice.ClinicalCriteria.Models;

namespace AuthorizationService.Tests.ConcurrentReview;

public class ConcurrentReviewServiceTests
{
    private static readonly DateTime Today = new(2026, 10, 10, 6, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IInpatientStayRepository> _stays = new();
    private readonly Mock<IAuthorizationRepository> _authorizations = new();
    private readonly Mock<IClinicalCriteriaService> _criteria = new();

    public ConcurrentReviewServiceTests()
    {
        _stays.Setup(s => s.CreateAsync(It.IsAny<InpatientStay>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((InpatientStay stay, CancellationToken _) => stay);
        _stays.Setup(s => s.UpdateAsync(It.IsAny<InpatientStay>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((InpatientStay stay, CancellationToken _) => stay);
    }

    private ConcurrentReviewService Service() => new(
        _stays.Object, _authorizations.Object, _criteria.Object,
        new FakeTimeProvider(Today));

    private static Authorization ApprovedAuthorization(DateTime? approvedThrough) => new()
    {
        Id = "auth-1",
        AuthorizationNumber = "PA-0001",
        MemberId = "M1",
        PatientFirstName = "Dana",
        PatientLastName = "Reyes",
        LineOfBusiness = LineOfBusiness.Medicare,
        Status = approvedThrough is null ? AuthorizationStatus.Pended : AuthorizationStatus.Approved,
        ApprovedServiceDateTo = approvedThrough,
        DiagnosisCodes = [new DiagnosisCode { Code = "J18.9" }],
    };

    private static AdmitStayRequest Admission(DateTime date) => new()
    {
        AuthorizationId = "auth-1",
        AdmissionDate = date,
        FacilityNPI = "1234567893",
        LevelOfCare = "inpatient-acute",
    };

    [Fact]
    public async Task An_approved_authorization_carries_its_certified_period_onto_the_stay()
    {
        var admission = new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc);
        _authorizations.Setup(a => a.GetByIdAsync("auth-1"))
            .ReturnsAsync(ApprovedAuthorization(new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc)));

        var stay = await Service().AdmitAsync(Admission(admission), "reviewer-1");

        stay.CertifiedThroughDate.Should().Be(new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc));
        stay.CertifiedDays.Should().Be(5, "8 October through 12 October inclusive");
        stay.NextReviewDueDate.Should().Be(stay.CertifiedThroughDate);
        stay.MemberId.Should().Be("M1");
        stay.AuthorizationNumber.Should().Be("PA-0001");
    }

    [Fact]
    public async Task A_stay_under_an_uncertified_authorization_is_on_todays_worklist()
    {
        _authorizations.Setup(a => a.GetByIdAsync("auth-1")).ReturnsAsync(ApprovedAuthorization(null));

        var stay = await Service().AdmitAsync(
            Admission(new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc)), "reviewer-1");

        stay.CertifiedDays.Should().Be(0);
        stay.CertifiedThroughDate.Should().BeNull();
        stay.NextReviewDueDate.Should().Be(Today.Date);
    }

    [Fact]
    public async Task An_admission_against_an_unknown_authorization_is_refused()
    {
        _authorizations.Setup(a => a.GetByIdAsync("auth-1")).ReturnsAsync((Authorization?)null);

        var act = () => Service().AdmitAsync(Admission(Today), "reviewer-1");

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task An_admission_in_the_future_is_refused()
    {
        _authorizations.Setup(a => a.GetByIdAsync("auth-1")).ReturnsAsync(ApprovedAuthorization(null));

        var act = () => Service().AdmitAsync(
            Admission(new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc)), "reviewer-1");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task A_review_that_asked_for_criteria_records_which_guideline_said_what()
    {
        _criteria.Setup(c => c.EvaluateAsync(It.IsAny<CriteriaRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CriteriaEvaluation
            {
                ProviderId = "interqual",
                Recommendation = CriteriaRecommendation.MeetsCriteria,
                Guideline = new CriteriaGuidelineReference
                {
                    GuidelineId = "IQ-ACUTE-PNA",
                    Title = "Pneumonia, acute",
                    Version = "2026.1",
                },
                Rationale = ["Hypoxia on room air."],
            });

        var stay = OpenStay();

        await Service().RecordReviewAsync(
            stay,
            new ConcurrentReviewDecision { Outcome = ConcurrentReviewOutcome.Approved, DaysCertified = 2 },
            evaluateCriteria: true,
            "reviewer-1");

        var review = stay.Reviews.Single();
        review.CriteriaProviderId.Should().Be("interqual");
        review.CriteriaGuidelineId.Should().Be("IQ-ACUTE-PNA");
        review.CriteriaGuidelineVersion.Should().Be("2026.1");
        review.CriteriaRecommendation.Should().Be("MeetsCriteria");
        review.CriteriaRationale.Should().ContainSingle().Which.Should().Be("Hypoxia on room air.");
        review.ReviewerId.Should().Be("reviewer-1");
    }

    [Fact]
    public async Task A_reviewers_own_citation_is_not_overwritten_by_the_criteria_call()
    {
        _criteria.Setup(c => c.EvaluateAsync(It.IsAny<CriteriaRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CriteriaEvaluation
            {
                ProviderId = "cho-local",
                Recommendation = CriteriaRecommendation.Indeterminate,
                Guideline = new CriteriaGuidelineReference { GuidelineId = "RULE-1", Title = "Rule" },
            });

        var stay = OpenStay();

        await Service().RecordReviewAsync(
            stay,
            new ConcurrentReviewDecision
            {
                Outcome = ConcurrentReviewOutcome.Approved,
                DaysCertified = 1,
                CriteriaProviderId = "mcg",
                CriteriaGuidelineId = "M-123",
            },
            evaluateCriteria: true,
            "reviewer-1");

        var review = stay.Reviews.Single();
        review.CriteriaProviderId.Should().Be("mcg", "the reviewer may cite a guideline consulted outside CHO");
        review.CriteriaGuidelineId.Should().Be("M-123");
    }

    [Fact]
    public async Task A_review_that_did_not_ask_for_criteria_does_not_call_the_seam()
    {
        var stay = OpenStay();

        await Service().RecordReviewAsync(
            stay,
            new ConcurrentReviewDecision { Outcome = ConcurrentReviewOutcome.Approved, DaysCertified = 1 },
            evaluateCriteria: false,
            "reviewer-1");

        _criteria.Verify(
            c => c.EvaluateAsync(It.IsAny<CriteriaRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task The_criteria_request_says_it_is_a_concurrent_review_and_which_day_of_the_stay()
    {
        CriteriaRequest? seen = null;
        _criteria.Setup(c => c.EvaluateAsync(It.IsAny<CriteriaRequest>(), It.IsAny<CancellationToken>()))
            .Callback<CriteriaRequest, CancellationToken>((r, _) => seen = r)
            .ReturnsAsync(new CriteriaEvaluation
            {
                ProviderId = "cho-local",
                Recommendation = CriteriaRecommendation.Indeterminate,
            });

        await Service().EvaluateCriteriaAsync(OpenStay());

        seen.Should().NotBeNull();
        seen!.ReviewType.Should().Be(CriteriaReviewType.Concurrent);
        seen.LevelOfCare.Should().Be("inpatient-acute");
        seen.PlaceOfServiceCode.Should().Be("21");
        seen.DiagnosisCodes.Should().ContainSingle().Which.Should().Be("J18.9");
    }

    [Fact]
    public async Task A_discharge_closes_the_stay_through_the_repository()
    {
        var stay = OpenStay();

        await Service().DischargeAsync(
            stay, new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc), "01", "reviewer-1");

        stay.Status.Should().Be(InpatientStayStatus.Discharged);
        _stays.Verify(s => s.UpdateAsync(stay, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static InpatientStay OpenStay() => new()
    {
        Id = "stay-1",
        TenantId = "t1",
        AuthorizationId = "auth-1",
        MemberId = "M1",
        FacilityNPI = "1234567893",
        LineOfBusiness = LineOfBusiness.Medicare,
        AdmissionDate = new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc),
        LevelOfCare = "inpatient-acute",
        PrincipalDiagnosisCode = "J18.9",
        Status = InpatientStayStatus.Admitted,
    };

    private sealed class FakeTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }
}
