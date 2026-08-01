using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class MemberServiceTests
{
    private readonly Mock<ILogger<MemberService>> _logger = new();
    private readonly IConfiguration _configuration;

    public MemberServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:MemberService"] = "http://localhost:5001"
            })
            .Build();
    }

    private MemberService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new MemberService(httpClient, _configuration, _logger.Object);
    }

    // ── SearchMembersAsync ──

    [Fact]
    public async Task SearchMembersAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.SearchMembersAsync("Smith"));
        ex.ServiceName.Should().Be("Member Service");
    }

    // ── GetMemberByIdAsync ──

    [Fact]
    public async Task GetMemberByIdAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetMemberByIdAsync("MBR-8201"));
        ex.ServiceName.Should().Be("Member Service");
    }

    // ── GetMemberPcpAsync ──

    [Fact]
    public async Task GetMemberPcpAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetMemberPcpAsync("MBR-8201"));
        ex.ServiceName.Should().Be("Member Service");
    }

    // ── AssignPcpAsync ──

    [Fact]
    public async Task AssignPcpAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.AssignPcpAsync(new AssignPcpRequest()));
        ex.ServiceName.Should().Be("Member Service");
    }

    // ── GetCoverageHistoryAsync ──

    [Fact]
    public async Task GetCoverageHistoryAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetCoverageHistoryAsync("MBR-8201"));
        ex.ServiceName.Should().Be("Member Service");
    }

    // ── GetMember834TransactionsAsync ──

    [Fact]
    public async Task GetMember834TransactionsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetMember834TransactionsAsync("MBR-8201"));
        ex.ServiceName.Should().Be("Member Service");
    }

    // ── TerminateEnrollmentAsync ──

    [Fact]
    public async Task TerminateEnrollmentAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.TerminateEnrollmentAsync(new TerminateEnrollmentRequest()));
        ex.ServiceName.Should().Be("Member Service");
    }

    [Fact]
    public async Task SearchMembersAsync_ExceptionWrapsInnerException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.SearchMembersAsync("Smith"));
        ex.InnerException.Should().BeOfType<HttpRequestException>();
    }

    // ════════════════════════════════════════════════════════════════
    // Happy-path and edge-case tests
    // ════════════════════════════════════════════════════════════════

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // ── SearchMembersAsync ──

    [Fact]
    public async Task SearchMembersAsync_WhenApiReturns200_DeserializesMembersList()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { memberId = "MBR-1", firstName = "Jane", lastName = "Doe",
                  dateOfBirth = "1990-05-15", coverageStatus = "Active" },
            new { memberId = "MBR-2", firstName = "John", lastName = "Smith",
                  dateOfBirth = "1985-11-20", coverageStatus = "Active" }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.SearchMembersAsync("Smith");

        result.Should().HaveCount(2);
        result[0].MemberId.Should().Be("MBR-1");
        result[0].FirstName.Should().Be("Jane");
        result[1].LastName.Should().Be("Smith");
    }

    [Fact]
    public async Task SearchMembersAsync_UsesMemberStatusWhenCoverageStatusIsAbsent()
    {
        var json = """
            [{"memberId":"BPV-001","firstName":"Plan","lastName":"Validator","dateOfBirth":"1985-01-15","status":"Active"}]
            """;

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.SearchMembersAsync("BPV-001");

        result.Should().ContainSingle();
        result[0].DisplayStatus.Should().Be("Active");
    }

    [Fact]
    public async Task SearchMembersAsync_WhenApiReturnsEmptyArray_ReturnsEmptyList()
    {
        var sut = CreateService(new HttpClient(
            new FakeHandler(HttpStatusCode.OK, "[]")));

        var result = await sut.SearchMembersAsync("Nobody");

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchMembersAsync_WhenApiReturnsNull_ReturnsEmptyList()
    {
        var sut = CreateService(new HttpClient(
            new FakeHandler(HttpStatusCode.OK, "null")));

        var result = await sut.SearchMembersAsync("Ghost");

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchMembersAsync_VerifyUrlEncodesSearchTerm()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        var sut = CreateService(new HttpClient(handler));

        await sut.SearchMembersAsync("O'Brien & Sons");

        handler.CapturedUrls.Should().ContainSingle()
            .Which.Should().Contain("q=O%27Brien%20%26%20Sons");
    }

    // ── GetMemberByIdAsync ──

    [Fact]
    public async Task GetMemberByIdAsync_WhenApiReturns200_DeserializesMemberDetails()
    {
        var json = JsonSerializer.Serialize(new
        {
            memberId = "MBR-42", firstName = "Alice", lastName = "Wonder",
            dateOfBirth = "1988-03-22", coverageStatus = "Active",
            gender = "Female", email = "alice@example.com", phone = "555-0101",
            address = "123 Main St, Springfield, IL 62701"
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetMemberByIdAsync("MBR-42");

        result.Should().NotBeNull();
        result!.MemberId.Should().Be("MBR-42");
        result.Gender.Should().Be("Female");
        result.Email.Should().Be("alice@example.com");
        result.Address.Should().Contain("Springfield");
    }

    [Fact]
    public async Task GetMemberByIdAsync_WhenApiReturnsNull_ReturnsNull()
    {
        var sut = CreateService(new HttpClient(
            new FakeHandler(HttpStatusCode.OK, "null")));

        var result = await sut.GetMemberByIdAsync("MBR-NONE");

        result.Should().BeNull();
    }

    // ── GetMemberPcpAsync ──

    [Fact]
    public async Task GetMemberPcpAsync_WhenApiReturns200_DeserializesPcp()
    {
        var json = JsonSerializer.Serialize(new
        {
            providerId = "PRV-50", providerName = "Dr. House",
            npi = "9876543210", specialty = "Internal Medicine",
            networkStatus = "In-Network", assignedDate = "2025-06-01",
            practiceName = "Princeton-Plainsboro"
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetMemberPcpAsync("MBR-42");

        result.Should().NotBeNull();
        result!.ProviderName.Should().Be("Dr. House");
        result.NPI.Should().Be("9876543210");
        result.PracticeName.Should().Be("Princeton-Plainsboro");
    }

    [Fact]
    public async Task GetMemberPcpAsync_WhenApiReturnsNull_ReturnsNull()
    {
        var sut = CreateService(new HttpClient(
            new FakeHandler(HttpStatusCode.OK, "null")));

        var result = await sut.GetMemberPcpAsync("MBR-NONE");

        result.Should().BeNull();
    }

    // ── AssignPcpAsync ──

    [Fact]
    public async Task AssignPcpAsync_WhenApiReturns200_CompletesWithoutException()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        var act = () => sut.AssignPcpAsync(new AssignPcpRequest
        {
            MemberId = "MBR-42", ProviderId = "PRV-50",
            EffectiveDate = new DateTime(2026, 4, 1), Reason = "Member request"
        });

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task AssignPcpAsync_VerifyPutBodyContainsRequest()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.AssignPcpAsync(new AssignPcpRequest
        {
            MemberId = "MBR-42", ProviderId = "PRV-50",
            EffectiveDate = new DateTime(2026, 4, 1)
        });

        handler.CapturedRequests.Should().ContainSingle();
        var req = handler.CapturedRequests[0];
        req.Method.Should().Be(HttpMethod.Put);
        var body = await req.Content!.ReadAsStringAsync();
        body.Should().Contain("MBR-42");
        body.Should().Contain("PRV-50");
        handler.CapturedUrls[0].Should().Contain("/members/MBR-42/pcp");
    }

    // ── GetCoverageHistoryAsync ──

    [Fact]
    public async Task GetCoverageHistoryAsync_WhenApiReturns200_DeserializesHistory()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { eventId = "EVT-1", eventDate = "2025-01-01", eventType = "Enrolled",
                  description = "Initial enrollment" },
            new { eventId = "EVT-2", eventDate = "2026-01-01", eventType = "PlanChange",
                  description = "Upgraded to PPO Gold" }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetCoverageHistoryAsync("MBR-42");

        result.Should().HaveCount(2);
        result[0].EventType.Should().Be("Enrolled");
        result[1].Description.Should().Be("Upgraded to PPO Gold");
    }

    [Fact]
    public async Task GetCoverageHistoryAsync_WhenApiReturnsEmpty_ReturnsEmptyList()
    {
        var sut = CreateService(new HttpClient(
            new FakeHandler(HttpStatusCode.OK, "[]")));

        var result = await sut.GetCoverageHistoryAsync("MBR-NEW");

        result.Should().BeEmpty();
    }

    // ── GetMember834TransactionsAsync ──

    [Fact]
    public async Task GetMember834TransactionsAsync_WhenApiReturns200_DeserializesRecords()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { transactionId = "TXN-1", batchId = "B-100", memberId = "MBR-42",
                  memberName = "Alice Wonder", maintenanceTypeCode = "021",
                  maintenanceReasonCode = "AI", transactionSetPurpose = "00",
                  transactionDate = "2025-01-15", status = "Accepted",
                  errors = Array.Empty<string>() }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetMember834TransactionsAsync("MBR-42");

        result.Should().ContainSingle();
        result[0].TransactionId.Should().Be("TXN-1");
        result[0].MaintenanceTypeCode.Should().Be("021");
        result[0].Status.Should().Be("Accepted");
    }

    [Fact]
    public async Task GetMember834TransactionsAsync_WhenApiReturnsEmpty_ReturnsEmptyList()
    {
        var sut = CreateService(new HttpClient(
            new FakeHandler(HttpStatusCode.OK, "[]")));

        var result = await sut.GetMember834TransactionsAsync("MBR-NEW");

        result.Should().BeEmpty();
    }

    // ── TerminateEnrollmentAsync ──

    [Fact]
    public async Task TerminateEnrollmentAsync_WhenApiReturns200_CompletesWithoutException()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        var act = () => sut.TerminateEnrollmentAsync(new TerminateEnrollmentRequest
        {
            MemberId = "MBR-42", CoverageId = "COV-1",
            TerminationDate = new DateTime(2026, 12, 31), ReasonCode = "1"
        });

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task TerminateEnrollmentAsync_VerifyPostBodyContainsRequest()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.TerminateEnrollmentAsync(new TerminateEnrollmentRequest
        {
            MemberId = "MBR-42", CoverageId = "COV-1",
            TerminationDate = new DateTime(2026, 12, 31),
            ReasonCode = "2", Notes = "Involuntary term"
        });

        handler.CapturedRequests.Should().ContainSingle();
        var req = handler.CapturedRequests[0];
        req.Method.Should().Be(HttpMethod.Post);
        handler.CapturedUrls[0].Should().Contain("/members/MBR-42/terminate");
        var body = await req.Content!.ReadAsStringAsync();
        body.Should().Contain("COV-1");
        body.Should().Contain("Involuntary term");
    }

    // ── GetAccumulatorsAsync ──

    [Fact]
    public async Task GetAccumulatorsAsync_WhenApiReturns200_DeserializesAccumulators()
    {
        var json = JsonSerializer.Serialize(new
        {
            memberId = "MBR-42",
            planYearStart = "2026-01-01",
            planYearEnd = "2026-12-31",
            individualDeductibleUsed = 500m, individualDeductibleLimit = 2000m,
            familyDeductibleUsed = 1200m, familyDeductibleLimit = 4000m,
            individualOopUsed = 1500m, individualOopLimit = 6000m,
            familyOopUsed = 3000m, familyOopLimit = 12000m,
            serviceAccumulators = new[]
            {
                new { benefitCategory = "Physical Therapy", used = 8m, limit = 20m, unit = "Visits" }
            },
            recentActivity = new[]
            {
                new {
                    eventId = "evt-1",
                    eventType = "ClaimApplied",
                    sourceReference = "CLM-500",
                    occurredAt = "2026-02-15",
                    deductibleDelta = 100m,
                    oopDelta = 155m,
                    familyDeductibleDelta = 0m,
                    familyOopDelta = 0m,
                    reason = (string?)null,
                    actorId = "system"
                }
            }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetAccumulatorsAsync("MBR-42");

        result.IndividualDeductibleUsed.Should().Be(500m);
        result.IndividualDeductibleLimit.Should().Be(2000m);
        result.FamilyOopLimit.Should().Be(12000m);
        result.ServiceAccumulators.Should().ContainSingle();
        result.ServiceAccumulators[0].BenefitCategory.Should().Be("Physical Therapy");
        result.ServiceAccumulators[0].Used.Should().Be(8m);
        result.ServiceAccumulators[0].Limit.Should().Be(20m);
        result.RecentActivity.Should().ContainSingle();
        result.RecentActivity[0].SourceReference.Should().Be("CLM-500");
        result.RecentActivity[0].DeductibleDelta.Should().Be(100m);
        result.RecentActivity[0].OopDelta.Should().Be(155m);
    }

    // ── CoverageHistoryEvent – remaining properties ────────────────────────────

    [Fact]
    public async Task GetCoverageHistoryAsync_WhenEventsHaveChangedByOldValueNewValue_DeserializesAllFields()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new
            {
                eventId = "EVT-FULL", eventDate = "2026-02-15T00:00:00Z",
                eventType = "PcpChange",
                description = "PCP reassigned due to provider termination",
                changedBy = "ops@healthplan.com",
                oldValue = "PRV-Old-999",
                newValue = "PRV-New-001"
            }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.GetCoverageHistoryAsync("MBR-100");

        result.Should().ContainSingle();
        var evt = result[0];
        evt.ChangedBy.Should().Be("ops@healthplan.com");
        evt.OldValue.Should().Be("PRV-Old-999");
        evt.NewValue.Should().Be("PRV-New-001");
    }
}
