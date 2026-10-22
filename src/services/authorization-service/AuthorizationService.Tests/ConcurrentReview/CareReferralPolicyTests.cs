using AuthorizationService.Models;
using AuthorizationService.Services.CareManagement;

namespace AuthorizationService.Tests.ConcurrentReview;

public class CareReferralPolicyTests
{
    private static InpatientStay Discharged(string? disposition, int lengthOfStayDays)
    {
        var admission = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        return new InpatientStay
        {
            Id = "stay-1",
            TenantId = "t1",
            AuthorizationId = "auth-1",
            MemberId = "M1",
            FacilityNPI = "1234567893",
            AdmissionDate = admission,
            DischargeDate = admission.AddDays(lengthOfStayDays - 1),
            DischargeDisposition = disposition,
            Status = InpatientStayStatus.Discharged,
        };
    }

    [Fact]
    public void A_short_stay_discharged_home_needs_no_care_manager()
    {
        CareReferralPolicy.ShouldRefer(Discharged("01", 2), 2).Should().BeFalse();
    }

    [Fact]
    public void A_long_stay_discharged_home_is_referred()
    {
        CareReferralPolicy.ShouldRefer(Discharged("01", 6), 6).Should().BeTrue();
    }

    [Theory]
    [InlineData("03")] // skilled nursing facility
    [InlineData("06")] // home with home health
    [InlineData("62")] // inpatient rehabilitation
    public void A_discharge_to_further_care_is_referred_however_short_the_stay(string disposition)
    {
        CareReferralPolicy.ShouldRefer(Discharged(disposition, 1), 1).Should().BeTrue();
    }

    [Theory]
    [InlineData("20")]
    [InlineData("41")]
    public void A_member_who_died_is_not_referred(string disposition)
    {
        CareReferralPolicy.ShouldRefer(Discharged(disposition, 8), 8).Should().BeFalse();
    }

    [Fact]
    public void Leaving_against_medical_advice_is_not_a_transitions_of_care_referral()
    {
        CareReferralPolicy.ShouldRefer(Discharged("07", 4), 4).Should().BeFalse(
            "that belongs to outreach, not transitions of care");
    }

    [Fact]
    public void A_discharge_with_no_disposition_recorded_is_referred()
    {
        CareReferralPolicy.ShouldRefer(Discharged(null, 1), 1).Should().BeTrue(
            "an unrecorded disposition is not evidence that the member went home well");
    }

    [Theory]
    [InlineData("01", 12, "High")]
    [InlineData("03", 2, "High")]
    [InlineData("01", 6, "Moderate")]
    [InlineData("01", 4, "Low")]
    public void Acuity_follows_the_length_of_stay_and_where_the_member_went(
        string disposition, int lengthOfStay, string expected)
    {
        CareReferralPolicy.AcuityFor(Discharged(disposition, lengthOfStay), lengthOfStay)
            .Should().Be(expected);
    }
}

public class DischargeCareReferralTests
{
    private readonly Mock<ICareReferralGateway> _gateway = new();

    private DischargeCareReferral Subject() => new(
        _gateway.Object, Microsoft.Extensions.Logging.Abstractions.NullLogger<DischargeCareReferral>.Instance);

    private static InpatientStay Stay(InpatientStayStatus status, string? disposition = "03", int days = 5)
    {
        var admission = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        return new InpatientStay
        {
            Id = "stay-1",
            TenantId = "t1",
            AuthorizationId = "auth-1",
            MemberId = "M1",
            MemberLastName = "Reyes",
            FacilityNPI = "1234567893",
            FacilityName = "Mercy General",
            PrincipalDiagnosisCode = "I50.9",
            AdmissionDate = admission,
            DischargeDate = status == InpatientStayStatus.Discharged ? admission.AddDays(days - 1) : null,
            DischargeDisposition = disposition,
            Status = status,
        };
    }

    [Fact]
    public async Task A_warranted_discharge_is_referred_with_the_stay_as_its_reference()
    {
        CareReferralCommand? sent = null;
        _gateway.Setup(g => g.ReferOnDischargeAsync(It.IsAny<CareReferralCommand>(), It.IsAny<CancellationToken>()))
            .Callback<CareReferralCommand, CancellationToken>((c, _) => sent = c)
            .ReturnsAsync("case-9");

        var caseId = await Subject().ReferIfWarrantedAsync(Stay(InpatientStayStatus.Discharged));

        caseId.Should().Be("case-9");
        sent!.StayId.Should().Be("stay-1");
        sent.TenantId.Should().Be("t1");
        sent.LengthOfStayDays.Should().Be(5);
        sent.Acuity.Should().Be("High", "the member went on to a skilled nursing facility");
        sent.PrincipalDiagnosisCode.Should().Be("I50.9");
    }

    [Fact]
    public async Task A_stay_that_is_still_open_is_not_referred()
    {
        await Subject().ReferIfWarrantedAsync(Stay(InpatientStayStatus.Admitted));

        _gateway.Verify(
            g => g.ReferOnDischargeAsync(It.IsAny<CareReferralCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task A_discharge_the_policy_rules_out_never_reaches_the_gateway()
    {
        await Subject().ReferIfWarrantedAsync(Stay(InpatientStayStatus.Discharged, "01", days: 2));

        _gateway.Verify(
            g => g.ReferOnDischargeAsync(It.IsAny<CareReferralCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task A_gateway_that_throws_does_not_make_a_recorded_discharge_look_failed()
    {
        _gateway.Setup(g => g.ReferOnDischargeAsync(It.IsAny<CareReferralCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("care-management-service is down"));

        var caseId = await Subject().ReferIfWarrantedAsync(Stay(InpatientStayStatus.Discharged));

        caseId.Should().BeNull();
    }
}
