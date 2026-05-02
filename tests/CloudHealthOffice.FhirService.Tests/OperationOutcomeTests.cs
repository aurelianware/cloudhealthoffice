using System.Net;
using System.Text.Json;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using FhirService.Services;
using FhirService.Models;
using Microsoft.Extensions.Configuration;

namespace CloudHealthOffice.FhirService.Tests;

/// <summary>
/// Unit tests verifying that the MockFhirDataAdapter returns correct results
/// and OperationOutcome-shaped error paths are exercised correctly.
/// </summary>
public class OperationOutcomeTests
{
    private static MockFhirDataAdapter CreateAdapter() => new();

    // ── MockFhirDataAdapter correctness ──────────────────────────────────────

    [Fact]
    public async Task GetPatient_KnownId_ReturnsPatient()
    {
        var adapter = CreateAdapter();
        var patient = await adapter.GetPatientAsync("pat-001", "tenant-a");

        patient.Should().NotBeNull();
        patient!.Id.Should().Be("pat-001");
        patient.Name.Should().NotBeEmpty();
        patient.Name[0].Family.Should().Be("Smith");
    }

    [Fact]
    public async Task GetPatient_UnknownId_ReturnsNull()
    {
        var adapter = CreateAdapter();
        var patient = await adapter.GetPatientAsync("pat-999", "tenant-a");
        patient.Should().BeNull();
    }

    [Fact]
    public async Task SearchPatients_ByName_ReturnsFilteredResults()
    {
        var adapter = CreateAdapter();
        var (items, total) = await adapter.SearchPatientsAsync(
            new PatientSearchParams { Name = "Smith" }, "tenant-a");

        items.Should().HaveCount(1);
        total.Should().Be(1);
        items[0].Name[0].Family.Should().Be("Smith");
    }

    [Fact]
    public async Task SearchPatients_EmptyQuery_ReturnsAll()
    {
        var adapter = CreateAdapter();
        var (items, total) = await adapter.SearchPatientsAsync(
            new PatientSearchParams(), "tenant-a");

        total.Should().Be(3);
        items.Should().HaveCount(3);
    }

    [Fact]
    public async Task SearchPatients_Pagination_ReturnsCorrectPage()
    {
        var adapter = CreateAdapter();

        var (page1, total) = await adapter.SearchPatientsAsync(
            new PatientSearchParams { Count = 2, Page = 1 }, "tenant-a");

        total.Should().Be(3);
        page1.Should().HaveCount(2);

        var (page2, _) = await adapter.SearchPatientsAsync(
            new PatientSearchParams { Count = 2, Page = 2 }, "tenant-a");

        page2.Should().HaveCount(1);
        // Pages should not overlap
        page1.Select(p => p.Id).Should().NotIntersectWith(page2.Select(p => p.Id));
    }

    [Fact]
    public async Task SearchCoverage_ByPatient_BareId_Works()
    {
        var adapter = CreateAdapter();
        var (items, total) = await adapter.SearchCoverageAsync(
            new CoverageSearchParams { Patient = "pat-001" }, "tenant-a");

        total.Should().Be(1);
        items[0].Id.Should().Be("cov-001");
    }

    [Fact]
    public async Task SearchCoverage_ByPatient_TypedRef_Works()
    {
        var adapter = CreateAdapter();
        var (items, total) = await adapter.SearchCoverageAsync(
            new CoverageSearchParams { Patient = "Patient/pat-002" }, "tenant-a");

        total.Should().Be(1);
        items[0].Id.Should().Be("cov-002");
    }

    [Fact]
    public async Task GetCoverage_KnownId_ReturnsCoverage()
    {
        var adapter = CreateAdapter();
        var cov = await adapter.GetCoverageAsync("cov-003", "tenant-a");
        cov.Should().NotBeNull();
        cov!.Status.Should().Be(FinancialResourceStatusCodes.Active);
    }

    [Fact]
    public async Task GetEncounter_KnownId_ReturnsEncounter()
    {
        var adapter = CreateAdapter();
        var enc = await adapter.GetEncounterAsync("enc-004", "tenant-a");
        enc.Should().NotBeNull();
        enc!.Status.Should().Be(Encounter.EncounterStatus.InProgress);
    }

    [Fact]
    public async Task SearchEncounters_ByStatus_Finished_ReturnsThree()
    {
        var adapter = CreateAdapter();
        var (items, total) = await adapter.SearchEncountersAsync(
            new EncounterSearchParams { Status = "finished" }, "tenant-a");

        total.Should().Be(3);
    }

    [Fact]
    public async Task GetClaim_UnknownId_ReturnsNull()
    {
        var adapter = CreateAdapter();
        var claim = await adapter.GetClaimAsync("clm-999", "tenant-a");
        claim.Should().BeNull();
    }

    [Fact]
    public async Task SearchClaims_ByPatient_ReturnsTwoClaims()
    {
        var adapter = CreateAdapter();
        var (items, total) = await adapter.SearchClaimsAsync(
            new ClaimSearchParams { Patient = "pat-001" }, "tenant-a");

        total.Should().Be(2); // clm-001, clm-002
    }

    // ── OperationOutcome structure ────────────────────────────────────────────

    [Fact]
    public void OperationOutcome_HasRequiredFields()
    {
        var outcome = new OperationOutcome
        {
            Issue =
            [
                new OperationOutcome.IssueComponent
                {
                    Severity = OperationOutcome.IssueSeverity.Error,
                    Code = OperationOutcome.IssueType.NotFound,
                    Diagnostics = "Patient/pat-999 not found"
                }
            ]
        };

        outcome.Issue.Should().HaveCount(1);
        outcome.Issue[0].Severity.Should().Be(OperationOutcome.IssueSeverity.Error);
        outcome.Issue[0].Code.Should().Be(OperationOutcome.IssueType.NotFound);
        outcome.Issue[0].Diagnostics.Should().Contain("not found");
    }

    [Fact]
    public void OperationOutcome_Serialises_ToFhirJson()
    {
        var options = new JsonSerializerOptions().ForFhir(typeof(OperationOutcome).Assembly);

        var outcome = new OperationOutcome
        {
            Issue =
            [
                new OperationOutcome.IssueComponent
                {
                    Severity = OperationOutcome.IssueSeverity.Error,
                    Code = OperationOutcome.IssueType.Invalid,
                    Diagnostics = "Bad request"
                }
            ]
        };

        var json = JsonSerializer.Serialize(outcome, options);
        json.Should().Contain("\"resourceType\":\"OperationOutcome\"");
        json.Should().Contain("\"issue\"");
        json.Should().Contain("error");
    }
}
