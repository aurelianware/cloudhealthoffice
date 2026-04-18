using EnrollmentImportService.Models;
using EnrollmentImportService.Services;

namespace EnrollmentImportService.Tests.Services;

public class EnrollmentValidatorTests
{
    private static MemberEnrollment Valid() => new()
    {
        MaintenanceType = "021",
        BenefitStatus = "A",
        Relationship = "18",
        SubscriberId = "M-001",
        EnrollmentDate = "2026-01-01",
        Demographics = new Demographics { FirstName = "Jane", LastName = "Doe" }
    };

    [Fact]
    public void Validate_HappyPath_IsValid()
    {
        var result = new EnrollmentValidator().Validate(Valid());
        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_MissingFields_ReportsStructuredErrors()
    {
        var bad = new MemberEnrollment();
        var result = new EnrollmentValidator().Validate(bad);

        result.IsValid.Should().BeFalse();
        result.Errors.Select(e => e.Code).Should().Contain(new[]
        {
            "maintenanceType.required",
            "benefitStatus.required",
            "relationship.required",
            "subscriberId.required",
            "demographics.required"
        });
        result.Errors.Should().OnlyContain(e => !string.IsNullOrEmpty(e.Field));
    }

    [Fact]
    public void Validate_TerminationWithoutDate_Fails()
    {
        var e = Valid();
        e.MaintenanceType = "024";
        e.EnrollmentDate = null;
        e.TerminationDate = null;
        var result = new EnrollmentValidator().Validate(e);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(x => x.Code == "terminationDate.requiredForTermination");
    }

    [Fact]
    public void Validate_AdditionWithoutDateOrCoverage_Fails()
    {
        var e = Valid();
        e.MaintenanceType = "021";
        e.EnrollmentDate = null;
        e.Coverage = new();
        var result = new EnrollmentValidator().Validate(e);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(x => x.Code == "enrollmentDate.requiredForAddition");
    }

    [Fact]
    public void Validate_UnsupportedMaintenanceType_Fails()
    {
        var e = Valid();
        e.MaintenanceType = "999";
        var result = new EnrollmentValidator().Validate(e);
        result.Errors.Should().Contain(x => x.Code == "maintenanceType.unsupported");
    }
}
