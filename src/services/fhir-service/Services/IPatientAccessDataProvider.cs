using FhirService.Models;

namespace FhirService.Services;

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
