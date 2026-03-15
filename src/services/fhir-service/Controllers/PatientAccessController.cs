using FhirService.Mappers;
using FhirService.Models;
using FhirService.Services;
using Microsoft.AspNetCore.Mvc;

namespace FhirService.Controllers;

/// <summary>
/// Patient Access API controller — lightweight FHIR R4 endpoints that map CHO
/// internal models to FHIR resources using System.Text.Json serialization.
///
/// Routes: /fhir/Patient/{id}, /fhir/Coverage?patient={id}, /fhir/ExplanationOfBenefit?patient={id}
///
/// Port of the TypeScript patient-access-mapper.ts endpoints.
/// </summary>
[ApiController]
[Route("fhir")]
public class PatientAccessController : ControllerBase
{
    private readonly IPatientAccessDataProvider _dataProvider;

    public PatientAccessController(IPatientAccessDataProvider dataProvider)
    {
        _dataProvider = dataProvider;
    }

    /// <summary>GET /fhir/Patient/{id} — read a single Patient (lightweight FHIR)</summary>
    [HttpGet("Patient/{id}")]
    [Produces("application/fhir+json")]
    public async Task<IActionResult> GetPatient(string id, CancellationToken ct)
    {
        var member = await _dataProvider.GetMemberAsync(id, ct);
        if (member is null)
            return NotFound(new { resourceType = "OperationOutcome", issue = new[] { new { severity = "error", code = "not-found", diagnostics = $"Patient/{id} not found" } } });

        var patient = PatientAccessMapper.MapMemberToPatient(member);
        return Ok(patient);
    }

    /// <summary>GET /fhir/Coverage?patient={id} — search Coverage by patient</summary>
    [HttpGet("Coverage")]
    [Produces("application/fhir+json")]
    public async Task<IActionResult> SearchCoverage([FromQuery] string? patient, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(patient))
            return BadRequest(new { resourceType = "OperationOutcome", issue = new[] { new { severity = "error", code = "invalid", diagnostics = "Coverage search requires 'patient' parameter." } } });

        var members = await _dataProvider.GetMembersByPatientIdAsync(patient, ct);
        var selfLink = $"{Request.Scheme}://{Request.Host}/fhir/Coverage?patient={patient}";
        var bundle = PatientAccessMapper.CoverageToBundle(members, selfLink);
        return Ok(bundle);
    }

    /// <summary>GET /fhir/ExplanationOfBenefit?patient={id} — search EOBs by patient</summary>
    [HttpGet("ExplanationOfBenefit")]
    [Produces("application/fhir+json")]
    public async Task<IActionResult> SearchExplanationOfBenefit([FromQuery] string? patient, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(patient))
            return BadRequest(new { resourceType = "OperationOutcome", issue = new[] { new { severity = "error", code = "invalid", diagnostics = "ExplanationOfBenefit search requires 'patient' parameter." } } });

        var payments = await _dataProvider.GetPaymentsByPatientIdAsync(patient, ct);
        var selfLink = $"{Request.Scheme}://{Request.Host}/fhir/ExplanationOfBenefit?patient={patient}";
        var bundle = PatientAccessMapper.PaymentsToEobBundle(payments, selfLink);
        return Ok(bundle);
    }
}

/// <summary>
/// Data provider abstraction for the Patient Access API.
/// Decouples the controller from the data source (mock or real CHO services).
/// </summary>
public interface IPatientAccessDataProvider
{
    Task<ChoMember?> GetMemberAsync(string memberId, CancellationToken ct = default);
    Task<IReadOnlyList<ChoMember>> GetMembersByPatientIdAsync(string patientId, CancellationToken ct = default);
    Task<IReadOnlyList<ChoPaymentDocument>> GetPaymentsByPatientIdAsync(string patientId, CancellationToken ct = default);
}

/// <summary>
/// In-memory mock data provider for the Patient Access API.
/// </summary>
public class MockPatientAccessDataProvider : IPatientAccessDataProvider
{
    private static readonly List<ChoMember> Members =
    [
        new()
        {
            MemberId = "pat-001",
            FirstName = "John",
            LastName = "Smith",
            Dob = "1955-07-14",
            Gender = "M",
            Address = new ChoAddress { Street1 = "123 Main St", City = "Nashville", State = "TN", Zip = "37201" },
            Phone = "615-555-0101"
        },
        new()
        {
            MemberId = "pat-002",
            FirstName = "Mary",
            LastName = "Jones",
            Dob = "1962-03-22",
            Gender = "F",
            Address = new ChoAddress { Street1 = "456 Oak Ave", City = "Memphis", State = "TN", Zip = "38101" }
        },
        new()
        {
            MemberId = "pat-003",
            FirstName = "Robert",
            LastName = "Williams",
            Dob = "1948-11-30",
            Gender = "M",
            Address = new ChoAddress { Street1 = "789 Pine Rd", City = "Knoxville", State = "TN", Zip = "37901" }
        }
    ];

    private static readonly List<ChoPaymentDocument> Payments =
    [
        new()
        {
            PaymentId = "PMT-001",
            ClaimId = "CLM-001",
            MemberId = "pat-001",
            PaymentDate = "2025-02-10",
            TotalPaid = 104.00m,
            Status = "active"
        },
        new()
        {
            PaymentId = "PMT-002",
            ClaimId = "CLM-002",
            MemberId = "pat-001",
            PaymentDate = "2025-05-20",
            TotalPaid = 168.00m,
            Status = "active"
        },
        new()
        {
            PaymentId = "PMT-003",
            ClaimId = "CLM-003",
            MemberId = "pat-002",
            PaymentDate = "2025-07-08",
            TotalPaid = 156.00m,
            Status = "active"
        }
    ];

    public Task<ChoMember?> GetMemberAsync(string memberId, CancellationToken ct = default)
        => Task.FromResult(Members.FirstOrDefault(m => m.MemberId == memberId));

    public Task<IReadOnlyList<ChoMember>> GetMembersByPatientIdAsync(string patientId, CancellationToken ct = default)
    {
        var result = Members.Where(m => m.MemberId == patientId).ToList();
        return Task.FromResult<IReadOnlyList<ChoMember>>(result);
    }

    public Task<IReadOnlyList<ChoPaymentDocument>> GetPaymentsByPatientIdAsync(string patientId, CancellationToken ct = default)
    {
        var result = Payments.Where(p => p.MemberId == patientId).ToList();
        return Task.FromResult<IReadOnlyList<ChoPaymentDocument>>(result);
    }
}
