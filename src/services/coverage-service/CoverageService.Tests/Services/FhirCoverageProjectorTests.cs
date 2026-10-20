using CoverageService.Models;
using CoverageService.Services;

namespace CoverageService.Tests.Services;

public class FhirCoverageProjectorTests
{
    private static Coverage Sample(CoverageStatus status = CoverageStatus.Active, DateTime? termination = null) => new()
    {
        Id = "cov-1",
        TenantId = "tenant-a",
        MemberId = "M-100",
        GroupNumber = "GRP-9",
        PlanId = "PLAN-7",
        Status = status,
        LineOfBusiness = LineOfBusiness.Medicaid,
        EffectiveDate = new DateTime(2024, 1, 1),
        TerminationDate = termination,
        LastUpdatedDate = new DateTime(2026, 3, 1, 10, 30, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void Projects_core_fields_with_the_member_id_as_the_beneficiary()
    {
        var json = new FhirCoverageProjector().Project(Sample());

        json["resourceType"]!.ToString().Should().Be("Coverage");
        json["id"]!.ToString().Should().Be("cov-1");
        json["status"]!.ToString().Should().Be("active");
        json["beneficiary"]!["reference"]!.ToString().Should().Be("Patient/M-100");
        json["period"]!["start"]!.ToString().Should().Be("2024-01-01");
        json["period"]!.AsObject().ContainsKey("end").Should().BeFalse();
        json["meta"]!["lastUpdated"]!.ToString().Should().StartWith("2026-03-01T10:30:00");
        json["type"]!["coding"]![0]!["code"]!.ToString().Should().Be("Medicaid");
        json["type"]!["coding"]![0]!["system"]!.ToString().Should().Be(FhirCoverageProjector.LineOfBusinessSystem);
    }

    [Fact]
    public void Group_and_plan_are_coverage_classes()
    {
        var classes = new FhirCoverageProjector().Project(Sample())["class"]!.AsArray();

        classes.Select(c => (c!["type"]!["coding"]![0]!["code"]!.ToString(), c["value"]!.ToString()))
            .Should().Equal(("group", "GRP-9"), ("plan", "PLAN-7"));
        classes.Should().OnlyContain(c => c!["type"]!["coding"]![0]!["system"]!.ToString() == FhirCoverageProjector.CoverageClassSystem);
    }

    [Theory]
    [InlineData(CoverageStatus.Active, "active")]
    [InlineData(CoverageStatus.COBRA, "active")]
    [InlineData(CoverageStatus.Suspended, "active")]
    [InlineData(CoverageStatus.Pending, "draft")]
    [InlineData(CoverageStatus.Terminated, "cancelled")]
    public void Status_maps_to_fhir_and_the_cho_status_is_kept_in_an_extension(CoverageStatus status, string fhir)
    {
        var json = new FhirCoverageProjector().Project(Sample(status));

        json["status"]!.ToString().Should().Be(fhir);
        var ext = json["extension"]!.AsArray().Should().ContainSingle().Subject!;
        ext["url"]!.ToString().Should().Be(FhirCoverageProjector.CoverageStatusExtension);
        ext["valueCode"]!.ToString().Should().Be(status.ToString());
        FhirCoverageProjector.ChoStatuses(fhir).Should().Contain(status, "the search inverse must agree with the projection");
    }

    [Fact]
    public void Termination_date_ends_the_period()
    {
        var json = new FhirCoverageProjector().Project(Sample(CoverageStatus.Terminated, new DateTime(2025, 6, 30)));

        json["period"]!["end"]!.ToString().Should().Be("2025-06-30");
    }

    [Fact]
    public void Payor_is_the_configured_payer_else_the_tenant()
    {
        new FhirCoverageProjector().Project(Sample())["payor"]![0]!["display"]!.ToString().Should().Be("tenant-a");

        var configured = new FhirCoverageProjector(new FhirCoverageProjectionOptions
        {
            PayorDisplay = "Example Health Plan",
            PayorReference = "Organization/plan-1",
        }).Project(Sample())["payor"]![0]!;
        configured["display"]!.ToString().Should().Be("Example Health Plan");
        configured["reference"]!.ToString().Should().Be("Organization/plan-1");
    }

    [Fact]
    public void Entered_in_error_matches_no_cho_status()
    {
        FhirCoverageProjector.ChoStatuses("entered-in-error").Should().BeEmpty();
    }
}
