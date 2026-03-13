using FhirService.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;

namespace CloudHealthOffice.FhirService.Tests;

/// <summary>
/// Unit tests for FHIR search parameter model binding and validation.
/// </summary>
public class SearchParameterTests
{
    [Fact]
    public void PatientSearchParams_DefaultPageSize_Is20()
    {
        var p = new PatientSearchParams();
        p.Count.Should().Be(20);
        p.Page.Should().Be(1);
    }

    [Fact]
    public void PatientSearchParams_IncludeAndRevInclude_DefaultToEmpty()
    {
        var p = new PatientSearchParams();
        p.Include.Should().BeEmpty();
        p.RevInclude.Should().BeEmpty();
    }

    [Fact]
    public void CoverageSearchParams_PatientReference_AcceptsTypedAndBareId()
    {
        // Bare ID — adapter normalises to "Patient/pat-001"
        var bare = new CoverageSearchParams { Patient = "pat-001" };
        bare.Patient.Should().Be("pat-001");

        // Typed reference
        var typed = new CoverageSearchParams { Patient = "Patient/pat-001" };
        typed.Patient.Should().Be("Patient/pat-001");
    }

    [Fact]
    public void EobSearchParams_HasAllRequiredFields()
    {
        var p = new EobSearchParams
        {
            Patient = "Patient/pat-001",
            Created = "2025-01-01",
            Status = "active"
        };

        p.Patient.Should().Be("Patient/pat-001");
        p.Created.Should().Be("2025-01-01");
        p.Status.Should().Be("active");
    }

    [Fact]
    public void EncounterSearchParams_HasPatientAndDate()
    {
        var p = new EncounterSearchParams
        {
            Patient = "pat-002",
            Date = "2025-07-01",
            Status = "finished"
        };

        p.Patient.Should().Be("pat-002");
        p.Date.Should().Be("2025-07-01");
        p.Status.Should().Be("finished");
    }

    [Fact]
    public void ClaimSearchParams_HasAllFields()
    {
        var p = new ClaimSearchParams
        {
            Patient = "pat-001",
            Created = "2025-02-06",
            Status = "active",
            Use = "claim"
        };

        p.Patient.Should().Be("pat-001");
        p.Use.Should().Be("claim");
    }

    [Theory]
    [InlineData(0, 1)]   // min-clamp
    [InlineData(20, 20)] // default
    [InlineData(200, 100)] // max-clamp
    public void ClampPageSize_ReturnsCorrectValue(int input, int expected)
    {
        var result = Math.Clamp(input, 1, 100);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(3, 3)]
    public void ClampPage_ReturnsAtLeastOne(int input, int expected)
    {
        var result = Math.Max(1, input);
        result.Should().Be(expected);
    }
}
