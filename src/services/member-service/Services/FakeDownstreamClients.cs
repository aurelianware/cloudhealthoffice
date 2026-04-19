using MemberService.Controllers;

namespace MemberService.Services;

/// <summary>
/// Dev-only stand-ins for downstream services. Registered ONLY when
/// <c>IHostEnvironment.IsDevelopment()</c> is true AND the corresponding
/// downstream base URL is not configured. Never use in production — production
/// must fail loudly with 503 when a downstream is misconfigured.
/// </summary>
public sealed class FakeCoverageServiceClient : ICoverageServiceClient
{
    public Task<MemberPcpResponse> GetPcpAsync(string tenantId, string memberId, CancellationToken ct = default)
        => Task.FromResult(new MemberPcpResponse
        {
            ProviderId = "dev-prov-001",
            ProviderName = "Dr. Dev Fixture, MD",
            NPI = "1234567890",
            Specialty = "Internal Medicine",
            NetworkStatus = "In-Network",
            AssignedDate = DateTime.UtcNow.AddMonths(-6),
            PracticeName = "Dev Fixture Clinic",
            Phone = "555-555-0100"
        });

    public Task<AssignPcpOutcome> AssignPcpAsync(
        string tenantId, string memberId, AssignPcpRequest request, CancellationToken ct = default)
        => Task.FromResult(new AssignPcpOutcome
        {
            Pcp = new MemberPcpResponse
            {
                ProviderId = request.ProviderId,
                ProviderName = "Dr. Dev Fixture, MD",
                NPI = "1234567890",
                Specialty = "Internal Medicine",
                NetworkStatus = "In-Network",
                AssignedDate = request.EffectiveDate
            }
        });

    public Task<IReadOnlyList<PcpAssignmentHistoryItem>> GetPcpHistoryAsync(
        string tenantId, string memberId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PcpAssignmentHistoryItem>>(new List<PcpAssignmentHistoryItem>
        {
            new()
            {
                Id = "dev-pcp-history-1",
                MemberId = memberId,
                CoverageId = "dev-coverage",
                ProviderNpi = "1234567890",
                ProviderName = "Dr. Dev Fixture, MD",
                EffectiveDate = DateTime.UtcNow.AddMonths(-6),
                AssignmentSource = "MemberChoice",
                NetworkStatusAtAssignment = "InNetwork",
                CreatedDate = DateTime.UtcNow.AddMonths(-6)
            }
        });

    public Task<IReadOnlyList<CoverageHistoryEvent>> GetCoverageHistoryAsync(
        string tenantId, string memberId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CoverageHistoryEvent>>(new List<CoverageHistoryEvent>
        {
            new()
            {
                EventDate = DateTime.UtcNow.AddMonths(-6),
                EventType = "Enrolled",
                Description = "Initial enrollment (dev fixture)",
                ChangedBy = "dev-fixture"
            }
        });

    public Task TerminateCoverageAsync(
        string tenantId, string memberId, TerminateMemberRequest request, CancellationToken ct = default)
        => Task.CompletedTask;
}

public sealed class FakeEnrollmentImportServiceClient : IEnrollmentImportServiceClient
{
    public Task<IReadOnlyList<Enrollment834Record>> Get834TransactionsAsync(
        string tenantId, string memberId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Enrollment834Record>>(new List<Enrollment834Record>
        {
            new()
            {
                TransactionId = "DEV-TXN-001",
                BatchId = "DEV-BATCH-001",
                MemberId = memberId,
                MemberName = "Dev Fixture",
                MaintenanceTypeCode = "021",
                TransactionDate = DateTime.UtcNow.AddMonths(-6),
                Status = "Accepted"
            }
        });

    public Task<EnrollmentEventListResponse> GetEnrollmentEventsAsync(
        string tenantId,
        string memberId,
        string? type = null,
        DateTime? from = null,
        DateTime? to = null,
        int limit = 50,
        string? continuationToken = null,
        CancellationToken ct = default)
        => Task.FromResult(new EnrollmentEventListResponse
        {
            Items = new List<EnrollmentEventRecord>
            {
                new()
                {
                    EventId = "834-DEV-BATCH-001:DEV-TXN-001:" + memberId,
                    EventType = "Enrolled",
                    Version = 1,
                    OccurredAt = DateTime.UtcNow.AddMonths(-6),
                    EventDate = DateTime.UtcNow.AddMonths(-6),
                    SourceBatchId = "DEV-BATCH-001",
                    TransactionId = "DEV-TXN-001",
                    MaintenanceType = "021",
                    Source = "edi834"
                }
            },
            ContinuationToken = null
        });
}

public sealed class FakeAccumulatorServiceClient : IAccumulatorServiceClient
{
    public Task<MemberAccumulatorsResponse> GetAccumulatorsAsync(
        string tenantId, string memberId, CancellationToken ct = default)
        => Task.FromResult(new MemberAccumulatorsResponse
        {
            MemberId = memberId,
            PlanYearStart = new DateTime(DateTime.UtcNow.Year, 1, 1),
            PlanYearEnd = new DateTime(DateTime.UtcNow.Year, 12, 31),
            IndividualDeductibleUsed = 0m,
            IndividualDeductibleLimit = 2000m,
            FamilyDeductibleUsed = 0m,
            FamilyDeductibleLimit = 6000m,
            IndividualOopUsed = 0m,
            IndividualOopLimit = 8150m,
            FamilyOopUsed = 0m,
            FamilyOopLimit = 16300m
        });
}
