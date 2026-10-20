using FluentAssertions;
using FhirService.Services.ChoNative;

namespace CloudHealthOffice.FhirService.Tests.ChoNative;

public class FhirSearchTranslationTests
{
    private static DateTime U(int y, int m, int d, int h = 0, int min = 0, int s = 0) => new(y, m, d, h, min, s, DateTimeKind.Utc);

    [Theory]
    [InlineData("1980", 1980, 1, 1, 1981, 1, 1)]
    [InlineData("1980-05", 1980, 5, 1, 1980, 6, 1)]
    [InlineData("1980-05-17", 1980, 5, 17, 1980, 5, 18)]
    [InlineData("eq1980-05-17", 1980, 5, 17, 1980, 5, 18)]
    public void A_date_matches_every_instant_at_its_precision(string raw, int fy, int fm, int fd, int ty, int tm, int td)
    {
        FhirSearchTranslation.ParseDate("birthdate", raw)
            .Should().Be(new FhirSearchTranslation.DateRange(U(fy, fm, fd), U(ty, tm, td)));
    }

    [Theory]
    [InlineData("ge1980", 1980, null)]
    [InlineData("gt1980", 1981, null)]
    [InlineData("le1980", null, 1981)]
    [InlineData("lt1980", null, 1980)]
    public void Comparison_prefixes_open_one_side(string raw, int? fromYear, int? toYear)
    {
        var range = FhirSearchTranslation.ParseDate("birthdate", raw);

        range.From.Should().Be(fromYear is { } f ? U(f, 1, 1) : null);
        range.To.Should().Be(toYear is { } t ? U(t, 1, 1) : null);
    }

    [Fact]
    public void An_instant_is_read_in_its_offset_and_matches_its_second()
    {
        FhirSearchTranslation.ParseDate("_lastUpdated", "2026-03-01T10:15:30-07:00")
            .Should().Be(new FhirSearchTranslation.DateRange(U(2026, 3, 1, 17, 15, 30), U(2026, 3, 1, 17, 15, 31)));
    }

    [Theory]
    [InlineData("ne1980")]
    [InlineData("sa1980")]
    [InlineData("ap1980")]
    public void Unsupported_prefixes_are_refused(string raw)
    {
        var act = () => FhirSearchTranslation.ParseDate("birthdate", raw);
        act.Should().Throw<FhirSearchNotSupportedException>().WithMessage("*prefix*");
    }

    [Theory]
    [InlineData("80-05-17")]
    [InlineData("1980-13-01")]
    [InlineData("yesterday")]
    [InlineData("9999-12-31")]
    [InlineData("9999")]
    [InlineData("01/02/2020 10:00")]
    [InlineData("2020-01-02 10:00")]
    public void Malformed_dates_are_refused(string raw)
    {
        var act = () => FhirSearchTranslation.ParseDate("birthdate", raw);
        act.Should().Throw<FhirSearchNotSupportedException>();
    }

    [Fact]
    public void Repeated_dates_are_anded_into_one_range_and_or_values_are_refused()
    {
        FhirSearchTranslation.ParseDates("birthdate", ["ge1980", "lt1990"])
            .Should().Be(new FhirSearchTranslation.DateRange(U(1980, 1, 1), U(1990, 1, 1)));

        FhirSearchTranslation.ParseDates("birthdate", ["ge1990", "lt1980"])!.Value.IsEmpty.Should().BeTrue();
        FhirSearchTranslation.ParseDates("birthdate", [null, " "]).Should().BeNull();

        var or = () => FhirSearchTranslation.ParseDates("birthdate", ["1980,1990"]);
        or.Should().Throw<FhirSearchNotSupportedException>();
    }

    [Fact]
    public void A_token_keeps_its_code_when_the_system_is_absent_or_accepted_and_refuses_other_systems()
    {
        FhirSearchTranslation.ParseToken("identifier", "M-1", "urn:cho:member-id").Should().Be("M-1");
        FhirSearchTranslation.ParseToken("identifier", "urn:cho:member-id|M-1", "urn:cho:member-id").Should().Be("M-1");
        FhirSearchTranslation.ParseToken("identifier", "|M-1", "urn:cho:member-id").Should().Be("M-1");

        var mbi = () => FhirSearchTranslation.ParseToken("identifier", "http://hl7.org/fhir/sid/us-mbi|1EG4", "urn:cho:member-id");
        mbi.Should().Throw<FhirSearchNotSupportedException>().WithMessage("*us-mbi*");
    }

    [Theory]
    [InlineData("M-1", "M-1")]
    [InlineData("Patient/M-1", "M-1")]
    [InlineData("https://fhir.example.org/r4/Patient/M-1", "M-1")]
    public void A_patient_reference_resolves_to_the_member_id(string raw, string expected)
    {
        FhirSearchTranslation.ParsePatientReference("patient", raw).Should().Be(expected);
    }

    [Fact]
    public void A_reference_to_another_resource_type_is_refused()
    {
        var act = () => FhirSearchTranslation.ParsePatientReference("beneficiary", "RelatedPerson/R-1");
        act.Should().Throw<FhirSearchNotSupportedException>().WithMessage("*RelatedPerson*");
    }

    [Fact]
    public void Single_valued_parameters_refuse_repeats_and_or()
    {
        FhirSearchTranslation.Single("family", [null, "Smith"]).Should().Be("Smith");
        FhirSearchTranslation.Single("family", []).Should().BeNull();

        var twice = () => FhirSearchTranslation.Single("family", ["Smith", "Jones"]);
        twice.Should().Throw<FhirSearchNotSupportedException>();
        var or = () => FhirSearchTranslation.Single("family", ["Smith,Jones"]);
        or.Should().Throw<FhirSearchNotSupportedException>();
    }

    [Fact]
    public void Any_of_splits_or_values_and_refuses_repeats()
    {
        FhirSearchTranslation.AnyOf("status", ["active, cancelled"]).Should().Equal("active", "cancelled");

        var twice = () => FhirSearchTranslation.AnyOf("status", ["active", "draft"]);
        twice.Should().Throw<FhirSearchNotSupportedException>();
    }
}
