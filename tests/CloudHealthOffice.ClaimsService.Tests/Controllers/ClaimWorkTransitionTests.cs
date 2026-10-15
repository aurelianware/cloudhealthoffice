using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services;
using ClaimWorkEvent = CloudHealthOffice.Events.ClaimWorkEvent;
using ClaimWorkEventTopics = CloudHealthOffice.Events.ClaimWorkEventTopics;
using CloudHealthOffice.Infrastructure.Security;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Controllers;

/// <summary>
/// Work management (claims.work.v1): claims-service tells
/// work-management-service when a pended claim leaves Pended through an
/// examiner resolution, and serves the reconcile snapshot of every pended
/// claim. Resolution never depends on work-management: the publisher is
/// fire-and-forget and runs after the claim's own fenced write.
/// </summary>
public class ClaimWorkTransitionTests : IClassFixture<ClaimsApiFactory>
{
    private readonly ClaimsApiFactory _factory;
    private readonly IClaimRepository _repo;
    private readonly IClaimWorkEventPublisher _work;

    public ClaimWorkTransitionTests(ClaimsApiFactory factory)
    {
        _factory = factory;
        _repo = factory.ClaimRepository;
        _work = factory.WorkEventPublisher;
        _work.ClearReceivedCalls();
        _repo.UpdateHoldingResolutionLockAsync(Arg.Any<Claim>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Claim>());
    }

    private HttpClient Client(string user = "examiner-1", string role = ChoRolePermissions.ClaimsExaminer)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(user, role));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");
        return client;
    }

    private static Claim ClaimIn(string id, ClaimStatus status, string pendCode = "NCCI") => new()
    {
        Id = id,
        TenantId = "test-tenant",
        ClaimNumber = "CN-" + id,
        Status = status,
        VersionState = ClaimVersionState.Submitted,
        BillingProviderNPI = "1234567890",
        LineOfBusiness = LineOfBusiness.Medicaid,
        ClaimType = ClaimType.Institutional,
        MemberId = "MEM-1",
        TotalChargeAmount = 1250m,
        ServiceDateFrom = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        ServiceDateTo = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        ReceivedDate = new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc),
        Institutional = new InstitutionalClaimDetails { AdmissionTypeCode = "1" },
        PendDetails = status == ClaimStatus.Pended
            ? new PendDetails { PendCode = pendCode, PendReason = "bundled pair", PendedAt = DateTime.UtcNow }
            : null,
        ClaimLines = [new() { LineNumber = 1, ProcedureCode = "99214", ChargeAmount = 1250m, Units = 1 }],
    };

    private Task<HttpResponseMessage> Resolve(Claim claim, string disposition)
    {
        _repo.GetByIdAsync(claim.Id).Returns(claim);
        return Client().PostAsJsonAsync($"/api/claims/work-queue/{claim.Id}/resolve", new JsonObject
        {
            ["disposition"] = disposition,
            ["reason"] = "reviewed",
            ["pendFingerprint"] = claim.PendDetails?.Fingerprint,
        });
    }

    [Theory]
    [InlineData("Approved", "Approved")]
    [InlineData("Deny", "Deny")]
    public async Task ResolvingAPendedClaim_PublishesWorkResolved_WithTheDispositionAndExaminer(string disposition, string expected)
    {
        var claim = ClaimIn("claim-w1-" + disposition, ClaimStatus.Pended);

        var response = await Resolve(claim, disposition);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _work.Received(1).PublishWorkResolvedAsync(
            Arg.Is<Claim>(c => c.Id == claim.Id), "test-tenant", expected, "examiner-1", Arg.Any<CancellationToken>());
        await _work.DidNotReceiveWithAnyArgs().PublishWorkNeededAsync(default!, default!, default);
    }

    [Fact]
    public async Task ReResolvingAPayClaim_IsNotAWorkTransition()
    {
        // A Pay (Approved) claim re-resolved to Deny never had an open work item.
        var claim = ClaimIn("claim-w2", ClaimStatus.Approved);

        var response = await Resolve(claim, "Deny");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _work.DidNotReceiveWithAnyArgs().PublishWorkResolvedAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task ARefusedResolution_PublishesNothing()
    {
        var claim = ClaimIn("claim-w3", ClaimStatus.Pended);
        _repo.GetByIdAsync(claim.Id).Returns(claim);

        // An approval without the reviewed pends' fingerprint is refused (400) before any write.
        var response = await Client().PostAsJsonAsync($"/api/claims/work-queue/{claim.Id}/resolve",
            new { disposition = "Approved", reason = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await _work.DidNotReceiveWithAnyArgs().PublishWorkResolvedAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task Snapshots_ReturnEveryPendedClaimAsAWorkNeededEvent_WithTheRoutingAttributes()
    {
        var pended = ClaimIn("claim-w4", ClaimStatus.Pended, "COB");
        pended.PendDetails!.AdditionalPendReasons.Add("NCCI: bundled pair NE001");
        _repo.SearchWithCountAsync(null, null, null, null, ClaimStatus.Pended, null, 2, 50)
            .Returns((new List<Claim> { pended }, 51));

        var response = await Client().GetAsync("/api/claims/work-queue/snapshots?page=2&pageSize=50");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<WorkSnapshotPageDto>();
        Assert.NotNull(page);
        Assert.Equal(51, page!.TotalCount);
        var item = Assert.Single(page.Items);
        Assert.Equal(ClaimWorkEventTopics.WorkNeeded, item.EventType);
        Assert.Equal("claim-w4", item.ClaimId);
        Assert.Equal("test-tenant", item.TenantId);
        Assert.Equal("COB", item.PrimaryPendCode);
        Assert.Equal(new[] { "COB", "NCCI" }, item.PendCodes);
        Assert.Equal("Medicaid", item.LineOfBusiness);
        Assert.Equal("Institutional", item.ClaimType);
        Assert.Equal(1250m, item.TotalChargeAmount);
        Assert.True(item.Expedited);
        Assert.Equal(page.ObservedAt.Ticks, item.SourceVersion);
    }

    [Fact]
    public async Task Snapshots_NeedClaimsRead()
    {
        // MemberServices has claims:read; a role without it is refused.
        var response = await Client("finance-approver", ChoRolePermissions.FinanceApprover)
            .GetAsync("/api/claims/work-queue/snapshots");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private sealed class WorkSnapshotPageDto
    {
        public int TotalCount { get; set; }
        public DateTime ObservedAt { get; set; }
        public List<ClaimWorkEvent> Items { get; set; } = new();
    }
}

/// <summary>The claims.work.v1 payload mapping (pure).</summary>
public class ClaimWorkEventsTests
{
    [Fact]
    public void Needed_CarriesThePendCodes_RoutingCodeFirst_AndTheRoutingAttributes()
    {
        var at = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        var claim = new Claim
        {
            Id = "c-1",
            ClaimNumber = "CN-1",
            Status = ClaimStatus.Pended,
            LineOfBusiness = LineOfBusiness.Commercial,
            ClaimType = ClaimType.Professional,
            TotalChargeAmount = 99.5m,
            BillingProviderNPI = "1999999984",
            SubmittedDate = at.AddDays(-3),
            AdjudicationResult = new AdjudicationResult { NetworkTier = "OutOfNetwork" },
            PendDetails = new PendDetails
            {
                PendCode = "oon",
                PendReason = "no contract",
                AdditionalPendReasons = ["MEDREVIEW: records", "OON: duplicate code"],
            },
        };

        var evt = ClaimWorkEvents.Needed(claim, "t-1", "system", at);

        Assert.Equal(ClaimWorkEventTopics.WorkNeeded, evt.EventType);
        Assert.Equal("OON", evt.PrimaryPendCode);
        Assert.Equal(new[] { "OON", "MEDREVIEW" }, evt.PendCodes);
        Assert.Equal("OutOfNetwork", evt.NetworkTier);
        Assert.Equal(at.AddDays(-3), evt.ReceivedDate);
        Assert.Equal(at.Ticks, evt.SourceVersion);
        Assert.False(evt.Expedited);
        Assert.Equal(PendDetails.ComputeFingerprint(claim.PendDetails), evt.PendFingerprint);
        Assert.Equal("Pended", evt.ClaimStatus);
    }

    [Fact]
    public void Resolved_CarriesTheResolutionAndTheNewStatus()
    {
        var claim = new Claim { Id = "c-2", Status = ClaimStatus.Deny };

        var evt = ClaimWorkEvents.Resolved(claim, "t-1", "Deny", "examiner-9");

        Assert.Equal(ClaimWorkEventTopics.WorkResolved, evt.EventType);
        Assert.Equal("Deny", evt.Resolution);
        Assert.Equal("Deny", evt.ClaimStatus);
        Assert.Equal("examiner-9", evt.ActorId);
    }

    [Fact]
    public void LegacyDenialCode_IsTheRoutingCode_WhenNoPendDetails()
    {
        var claim = new Claim
        {
            Id = "c-3",
            Status = ClaimStatus.Pended,
            AdjudicationResult = new AdjudicationResult { DenialReasonCode = "COB" },
        };

        var evt = ClaimWorkEvents.Needed(claim, "t-1", null);

        Assert.Equal("COB", evt.PrimaryPendCode);
        Assert.Equal(new[] { "COB" }, evt.PendCodes);
    }
}
