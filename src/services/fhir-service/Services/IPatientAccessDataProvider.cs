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
/// Read access to the CHO-owned member/coverage directory used by the
/// Payer-to-Payer <c>$member-match</c> operation (P2P-04). It is a query surface
/// over the SAME authoritative member store the Patient Access provider serves —
/// not a second copy — adding only the two lookups member-match needs:
/// enumerate candidates for demographic resolution, and read a member's
/// coverages so the relevant (prior / current / concurrent) coverage context can
/// be selected. Kept separate from <see cref="IPatientAccessDataProvider"/> so
/// the Patient Access contract is unchanged.
/// </summary>
public interface IChoMemberDirectory
{
    /// <summary>All members in the served store. The tenant boundary is applied by the caller.</summary>
    Task<IReadOnlyList<ChoMember>> GetAllMembersAsync(CancellationToken ct = default);

    /// <summary>The member's coverage records (may be several — prior, current, overlapping).</summary>
    Task<IReadOnlyList<ChoCoverage>> GetCoveragesByMemberIdAsync(string memberId, CancellationToken ct = default);
}

/// <summary>
/// In-memory mock data provider for the Patient Access API. Also serves the
/// <see cref="IChoMemberDirectory"/> over the same synthetic member store so the
/// Payer-to-Payer member-match reuses CHO-owned data (no duplicate store).
/// </summary>
public class MockPatientAccessDataProvider : IPatientAccessDataProvider, IChoMemberDirectory
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
        },
        // Two synthetic members deliberately share a family name AND date of birth
        // so a demographic-only match on last name + DOB alone is genuinely
        // ambiguous — the member-match must refuse rather than pick one. A given
        // name or a strong identifier tells them apart.
        new()
        {
            MemberId = "pat-010",
            FirstName = "Alice",
            LastName = "Brown",
            Dob = "1990-05-05",
            Gender = "F",
            Address = new ChoAddress { Street1 = "10 Elm St", City = "Chattanooga", State = "TN", Zip = "37402" },
            Phone = "423-555-0110"
        },
        new()
        {
            MemberId = "pat-011",
            FirstName = "Andrew",
            LastName = "Brown",
            Dob = "1990-05-05",
            Gender = "M",
            Address = new ChoAddress { Street1 = "22 Birch Ln", City = "Clarksville", State = "TN", Zip = "37040" }
        }
    ];

    // Synthetic coverage records. pat-001 carries TWO coverages — a prior
    // (ended) relationship and the current open-ended one, whose periods overlap
    // in 2022 — so concurrent/prior/current coverage selection is exercised
    // against real data. The others have a single active coverage.
    private static readonly List<ChoCoverage> Coverages =
    [
        new()
        {
            MemberId = "pat-001", CoverageId = "COV-001-PRIOR", Status = "cancelled",
            PayerId = "PRIOR-PLAN", SubscriberId = "SUB-1001",
            PeriodStart = "2018-01-01", PeriodEnd = "2022-12-31"
        },
        new()
        {
            MemberId = "pat-001", CoverageId = "COV-001-CURRENT", Status = "active",
            PayerId = "CHO-PLAN", SubscriberId = "SUB-2001",
            PeriodStart = "2022-06-01", PeriodEnd = null
        },
        new()
        {
            MemberId = "pat-002", CoverageId = "COV-002", Status = "active",
            PayerId = "CHO-PLAN", SubscriberId = "SUB-3002",
            PeriodStart = "2020-01-01", PeriodEnd = null
        },
        new()
        {
            MemberId = "pat-003", CoverageId = "COV-003", Status = "active",
            PayerId = "CHO-PLAN", SubscriberId = "SUB-3003",
            PeriodStart = "2019-03-15", PeriodEnd = null
        },
        new()
        {
            MemberId = "pat-010", CoverageId = "COV-010", Status = "active",
            PayerId = "CHO-PLAN", SubscriberId = "SUB-3010",
            PeriodStart = "2021-01-01", PeriodEnd = null
        },
        new()
        {
            MemberId = "pat-011", CoverageId = "COV-011", Status = "active",
            PayerId = "CHO-PLAN", SubscriberId = "SUB-3011",
            PeriodStart = "2021-01-01", PeriodEnd = null
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

    // ── IChoMemberDirectory ─────────────────────────────────────────────────

    public Task<IReadOnlyList<ChoMember>> GetAllMembersAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ChoMember>>(Members);

    public Task<IReadOnlyList<ChoCoverage>> GetCoveragesByMemberIdAsync(string memberId, CancellationToken ct = default)
    {
        var result = Coverages.Where(c => c.MemberId == memberId).ToList();
        return Task.FromResult<IReadOnlyList<ChoCoverage>>(result);
    }
}
