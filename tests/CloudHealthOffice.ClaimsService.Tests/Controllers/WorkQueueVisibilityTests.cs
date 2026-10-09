using CloudHealthOffice.Infrastructure.Security;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services;
using ClaimsService.Services.Adjudication;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Controllers;

/// <summary>
/// Pend-persistence defect fix — task requirement 3: a claim pended via
/// the fixed path (<c>ClaimStatus.Pended</c> + populated <c>PendDetails</c>)
/// must appear in the examiner work queue, which filters on
/// <c>IClaimRepository.SearchAsync(..., status: ClaimStatus.Pended, ...)</c>
/// (<c>ClaimsController.GetWorkQueueSummary</c> / <c>GetWorkQueueItems</c>).
/// Before the fix, no code path ever produced a claim in this shape, so
/// this query always returned empty for orchestrator-pended claims.
/// </summary>
public class WorkQueueVisibilityTests : IClassFixture<ClaimsApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _client;
    private readonly IClaimRepository _repo;
    private readonly IClaimVersionEventPublisher _versionPublisher;
    private readonly IClaimVersionEventReader _versionReader;
    private readonly IClaimApprovalReadjudicator _readjudicator;

    public WorkQueueVisibilityTests(ClaimsApiFactory factory)
    {
        _repo = factory.ClaimRepository;
        _versionPublisher = factory.VersionEventPublisher;
        _versionReader = factory.VersionEventReader;
        _readjudicator = factory.ApprovalReadjudicator;
        _readjudicator.ClearReceivedCalls();
        _readjudicator.ReadjudicateForApprovalAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ExaminerApproval>(), Arg.Any<CancellationToken>())
            .Returns(new ApprovalReadjudicationResult(ClaimAdjudicationOutcome.Pass, null));
        _client = factory.CreateDefaultClient(
            new ChoDevelopmentTokenHandler("examiner-1", ChoRolePermissions.TenantAdmin));
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");
        _repo.ClearReceivedCalls();
        _versionPublisher.ClearReceivedCalls();
        _versionReader.ClearReceivedCalls();
        _versionReader.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ClaimVersionEvent>());
    }

    private static Claim NcciPendedClaim() => new()
    {
        Id = "claim-ncci-pended",
        TenantId = "test-tenant",
        ClaimNumber = "CLM-NCCI-PENDED",
        Status = ClaimStatus.Pended,
        VersionState = ClaimVersionState.Submitted,
        BillingProviderNPI = "1234567890",
        LineOfBusiness = LineOfBusiness.Commercial,
        MemberId = "MEM-1",
        TotalChargeAmount = 200m,
        ServiceDateFrom = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        ServiceDateTo = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        LastUpdatedDate = DateTime.UtcNow.AddDays(-2),
        PendDetails = new PendDetails
        {
            PendCode = "NCCI",
            PendReason = "bundled pair NE001",
            PendedAt = DateTime.UtcNow.AddDays(-2),
        },
        ClaimLines = new List<ClaimLine>
        {
            new() { LineNumber = 1, ProcedureCode = "99213", ChargeAmount = 100m, Units = 1 },
            new() { LineNumber = 2, ProcedureCode = "99214", ChargeAmount = 100m, Units = 1 },
        },
    };

    private static Claim CobPendedClaim() => new()
    {
        Id = "claim-cob-pended",
        TenantId = "test-tenant",
        ClaimNumber = "CLM-COB-PENDED",
        Status = ClaimStatus.Pended,
        VersionState = ClaimVersionState.Submitted,
        BillingProviderNPI = "1234567890",
        LineOfBusiness = LineOfBusiness.Commercial,
        MemberId = "MEM-2",
        TotalChargeAmount = 100m,
        ServiceDateFrom = new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc),
        ServiceDateTo = new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc),
        LastUpdatedDate = DateTime.UtcNow.AddDays(-1),
        PendDetails = new PendDetails
        {
            PendCode = "COB",
            PendReason = "Cloud Health Office is the secondary payer; primary payer Aetna",
            PendedAt = DateTime.UtcNow.AddDays(-1),
        },
        ClaimLines = new List<ClaimLine>
        {
            new() { LineNumber = 1, ProcedureCode = "99213", ChargeAmount = 100m, Units = 1 },
        },
    };

    [Fact]
    public async Task WorkQueueSummary_reflects_Ncci_and_Cob_pended_claims_by_pend_code()
    {
        _repo.SearchAsync(
                memberId: null, providerNPI: null,
                serviceDateFrom: null, serviceDateTo: null,
                status: ClaimStatus.Pended, lineOfBusiness: null,
                page: 1, pageSize: Arg.Any<int>())
            .Returns(new[] { NcciPendedClaim(), CobPendedClaim() });

        var response = await _client.GetAsync("/api/claims/work-queue/summary");
        response.EnsureSuccessStatusCode();

        var summary = await response.Content.ReadFromJsonAsync<WorkQueueSummaryDto>(Json);
        Assert.NotNull(summary);
        Assert.Equal(1, summary!.NcciEditFailures);
        Assert.Equal(1, summary.CobRequired);
        Assert.Equal(0, summary.MedicalReview);
    }

    [Fact]
    public async Task WorkQueueItems_surfaces_pended_claim_with_examiner_relevant_fields()
    {
        _repo.SearchAsync(
                memberId: null, providerNPI: null,
                serviceDateFrom: null, serviceDateTo: null,
                status: ClaimStatus.Pended, lineOfBusiness: null,
                page: 1, pageSize: Arg.Any<int>())
            .Returns(new[] { NcciPendedClaim() });

        var response = await _client.GetAsync("/api/claims/work-queue/items");
        response.EnsureSuccessStatusCode();

        var items = await response.Content.ReadFromJsonAsync<List<WorkQueueItemDto>>(Json);
        Assert.NotNull(items);
        var item = Assert.Single(items!);
        Assert.Equal("claim-ncci-pended", item.ClaimId);
        Assert.Equal("NCCI", item.QueueReasonCode);
        Assert.Equal("NCCI Edit Failure", item.QueueReason);
        Assert.Equal(2, item.ProcedureCodes.Count);
    }

    [Fact]
    public async Task WorkQueueItems_filters_by_queueType_using_pend_code()
    {
        _repo.SearchAsync(
                memberId: null, providerNPI: null,
                serviceDateFrom: null, serviceDateTo: null,
                status: ClaimStatus.Pended, lineOfBusiness: null,
                page: 1, pageSize: Arg.Any<int>())
            .Returns(new[] { NcciPendedClaim(), CobPendedClaim() });

        var response = await _client.GetAsync("/api/claims/work-queue/items?queueType=COB");
        response.EnsureSuccessStatusCode();

        var items = await response.Content.ReadFromJsonAsync<List<WorkQueueItemDto>>(Json);
        Assert.NotNull(items);
        var item = Assert.Single(items!);
        Assert.Equal("claim-cob-pended", item.ClaimId);
    }

    [Fact]
    public async Task ResolvePendedClaim_Approves_And_Persists_Ai_Feedback()
    {
        var claim = NcciPendedClaim();
        claim.AiExamination = new AiExamination
        {
            RecommendedDisposition = "RequestInfo",
            ConfidenceScore = 0.87,
        };
        _repo.GetByIdAsync(claim.Id).Returns(claim);
        _repo.UpdateAsync(Arg.Any<Claim>()).Returns(call => call.Arg<Claim>());

        var response = await _client.PostAsJsonAsync(
            $"/api/claims/work-queue/{claim.Id}/resolve",
            new
            {
                disposition = "Approved",
                reason = "Documentation supports modifier 59",
                aiExaminerAgreement = "Overridden",
                // Ignored: the examiner is the token subject ("examiner-1").
                examinerUserId = "spoofed-examiner",
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _repo.Received(1).UpdateAsync(Arg.Is<Claim>(saved =>
            saved.Status == ClaimStatus.Approved
            && saved.VersionState == ClaimVersionState.Adjudicated
            && saved.AiExamination!.ExaminerAgreement == "Overridden"
            && saved.AiExamination.ExaminerUserId == "examiner-1"));
        await _versionPublisher.Received(1).PublishVersionResolvedAsync(
            Arg.Is<Claim>(saved => saved.Id == claim.Id),
            "Approved",
            "Documentation supports modifier 59",
            "examiner-1",
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AuditTimeline_CombinesVersionEventsWithStructuredPend_InChronologicalOrder()
    {
        var claim = NcciPendedClaim();
        claim.ClaimVersionId = "chain-1";
        _repo.GetByIdAsync(claim.Id).Returns(claim);
        _versionReader.GetAsync("test-tenant", "chain-1", Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new ClaimVersionEvent
                {
                    EventType = ClaimVersionEventType.ClaimVersionSubmitted,
                    OccurredAt = claim.PendDetails!.PendedAt.AddMinutes(-1),
                    ActorId = "837-ingress",
                    Version = 1
                },
                new ClaimVersionEvent
                {
                    EventType = ClaimVersionEventType.ClaimVersionAdjudicated,
                    VersionId = claim.Id,
                    OccurredAt = claim.PendDetails!.PendedAt.AddSeconds(1),
                    ActorId = "system",
                    Version = 2
                }
            });

        var response = await _client.GetAsync($"/api/claims/{claim.Id}/audit-timeline");
        response.EnsureSuccessStatusCode();

        var timeline = await response.Content.ReadFromJsonAsync<List<AuditEntryDto>>(Json);
        Assert.Collection(timeline!,
            submitted => Assert.Equal("Claim submitted", submitted.Action),
            pended =>
            {
                Assert.Equal("Claim pended for review", pended.Action);
                Assert.Equal("Pended", pended.NewValue);
                Assert.Contains("NCCI", pended.Notes);
            });
        await _versionReader.Received(1).GetAsync(
            "test-tenant", "chain-1", Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// PR #1278 re-review N3: a claim pended by COB and then by NCCI shows
    /// the examiner every reason, the COB one first (it is the pend code
    /// that routes the queue).
    /// </summary>
    [Fact]
    public async Task WorkQueueItems_lists_every_pend_reason_cob_first()
    {
        var claim = CobPendedClaim();
        claim.PendDetails!.AdditionalPendReasons.Add("NCCI: bundled pair NE001");
        _repo.SearchAsync(
                memberId: null, providerNPI: null,
                serviceDateFrom: null, serviceDateTo: null,
                status: ClaimStatus.Pended, lineOfBusiness: null,
                page: 1, pageSize: Arg.Any<int>())
            .Returns(new[] { claim });

        var response = await _client.GetAsync("/api/claims/work-queue/items");
        response.EnsureSuccessStatusCode();

        var item = Assert.Single((await response.Content.ReadFromJsonAsync<List<WorkQueueItemDto>>(Json))!);
        Assert.Equal("COB", item.QueueReasonCode);
        Assert.Equal(
            new[] { "COB: Cloud Health Office is the secondary payer; primary payer Aetna", "NCCI: bundled pair NE001" },
            item.PendReasons);
    }

    /// <summary>
    /// Re-review N1: approval re-adjudicates the claim (so accumulators are
    /// written by a Production pass). A COB pend approved without the payer
    /// order the examiner confirmed is refused — never paid as primary — and
    /// nothing is saved.
    /// </summary>
    [Fact]
    public async Task ResolvePendedClaim_CobPend_WithoutPayerSequence_IsRefused()
    {
        var claim = CobPendedClaim();
        _repo.GetByIdAsync(claim.Id).Returns(claim);
        _readjudicator.ReadjudicateForApprovalAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ExaminerApproval>(), Arg.Any<CancellationToken>())
            .Returns(new ApprovalReadjudicationResult(
                ClaimAdjudicationOutcome.Pend, "payer order not confirmed", ["CoordinationOfBenefits: payer order not confirmed"]));

        var response = await _client.PostAsJsonAsync(
            $"/api/claims/work-queue/{claim.Id}/resolve",
            new { disposition = "Approved", reason = "ok" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("payerSequence", body);
        Assert.Contains("CoordinationOfBenefits: payer order not confirmed", body);
        await _readjudicator.Received(1).ReadjudicateForApprovalAsync(
            "test-tenant", claim.Id,
            Arg.Is<ExaminerApproval>(a => a.PayerSequence == null && a.ExaminerId == "examiner-1"),
            Arg.Any<CancellationToken>());
        await _repo.DidNotReceiveWithAnyArgs().UpdateAsync(default!);
    }

    /// <summary>The examiner's confirmed payer order reaches the re-adjudication; a passing re-run approves.</summary>
    [Fact]
    public async Task ResolvePendedClaim_CobPend_WithPayerSequence_ReadjudicatesThenApproves()
    {
        var claim = CobPendedClaim();
        _repo.GetByIdAsync(claim.Id).Returns(claim);
        _repo.UpdateAsync(Arg.Any<Claim>()).Returns(call => call.Arg<Claim>());

        var response = await _client.PostAsJsonAsync(
            $"/api/claims/work-queue/{claim.Id}/resolve",
            new { disposition = "Approved", reason = "secondary confirmed", payerSequence = 2 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _readjudicator.Received(1).ReadjudicateForApprovalAsync(
            "test-tenant", claim.Id,
            Arg.Is<ExaminerApproval>(a => a.PayerSequence == 2),
            Arg.Any<CancellationToken>());
        await _repo.Received(1).UpdateAsync(Arg.Is<Claim>(saved => saved.Status == ClaimStatus.Approved));
    }

    /// <summary>A denial does not re-adjudicate.</summary>
    [Fact]
    public async Task ResolvePendedClaim_Deny_DoesNotReadjudicate()
    {
        var claim = NcciPendedClaim();
        _repo.GetByIdAsync(claim.Id).Returns(claim);
        _repo.UpdateAsync(Arg.Any<Claim>()).Returns(call => call.Arg<Claim>());

        var response = await _client.PostAsJsonAsync(
            $"/api/claims/work-queue/{claim.Id}/resolve",
            new { disposition = "Denied", reason = "bundled" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _readjudicator.DidNotReceiveWithAnyArgs().ReadjudicateForApprovalAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task ResolvePendedClaim_Denies_Only_Pended_Claims()
    {
        var claim = NcciPendedClaim();
        claim.Status = ClaimStatus.Approved;
        _repo.GetByIdAsync(claim.Id).Returns(claim);

        var response = await _client.PostAsJsonAsync(
            $"/api/claims/work-queue/{claim.Id}/resolve",
            new { disposition = "Denied", reason = "Documentation not received" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await _repo.DidNotReceiveWithAnyArgs().UpdateAsync(default!);
    }

    private sealed class WorkQueueSummaryDto
    {
        public int NcciEditFailures { get; set; }
        public int MissingAuth { get; set; }
        public int ProviderNotContracted { get; set; }
        public int CobRequired { get; set; }
        public int MedicalReview { get; set; }
    }

    private sealed class WorkQueueItemDto
    {
        public string ClaimId { get; set; } = string.Empty;
        public string QueueReason { get; set; } = string.Empty;
        public string QueueReasonCode { get; set; } = string.Empty;
        public List<string> ProcedureCodes { get; set; } = new();
        public List<string> PendReasons { get; set; } = new();
    }

    private sealed class AuditEntryDto
    {
        public string Action { get; set; } = string.Empty;
        public string? NewValue { get; set; }
        public string? Notes { get; set; }
    }
}
