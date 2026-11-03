using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services;
using ClaimsService.Services.RetroRate;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.RetroRate;

public class RetroRateReprocessingServiceTests
{
    private readonly IRetroRateImpactRepository _impact = Substitute.For<IRetroRateImpactRepository>();
    private readonly InMemoryRuns _runs = new();
    private readonly IClaimRepository _claims = Substitute.For<IClaimRepository>();
    private readonly IClaimAdjustmentService _adjustments = Substitute.For<IClaimAdjustmentService>();

    private static readonly RetroRateImpactRun Impact = new()
    {
        Id = "impact-1", TenantId = "t1", FeeScheduleId = "t1:TMHP adult:20260901", FeeScheduleName = "TMHP adult", NewRevision = 3,
    };

    private static RetroRateImpactResult Result(string id, string direction, decimal net, string plan = "STAR-TX", string status = "Paid") => new()
    {
        ClaimId = id, ClaimDocumentId = "doc-" + id, ClaimNumber = "N-" + id, Direction = direction, NetPaidDelta = net,
        AbsoluteNetPaidDelta = Math.Abs(net), BenefitPlanId = plan, ClaimStatus = status,
    };

    private RetroRateReprocessingService Service(RetroRateOptions? options = null) =>
        new(_impact, _runs, _claims, _adjustments, Options.Create(options ?? new RetroRateOptions()),
            NullLogger<RetroRateReprocessingService>.Instance);

    private void ImpactReturns(params RetroRateImpactResult[] results)
    {
        _impact.GetRunAsync("t1", "impact-1", Arg.Any<CancellationToken>()).Returns(Impact);
        _impact.ListResultsAsync("t1", "impact-1", null, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(results);
    }

    [Fact]
    public void Plan_PaysUnderpayments_AndRecoupsOnlyOptedInPlans()
    {
        var run = RetroRateReprocessingService.Plan(Impact,
        [
            Result("a", RetroRateDirection.Underpaid, 10m),
            Result("b", RetroRateDirection.Overpaid, -4m),
            Result("c", RetroRateDirection.Overpaid, -6m, plan: "CHIP"),
            Result("d", RetroRateDirection.Undecided, 0m),
            Result("e", RetroRateDirection.Unchanged, 0m),
            Result("f", RetroRateDirection.Underpaid, 2m, status: "Approved"),
        ], new RetroRateTenantOptions { RecoupPlans = ["CHIP"] });

        Assert.Equal(2, run.ClaimsToAdjust);
        Assert.Equal(10m, run.PlannedAdditionalPayment);
        Assert.Equal(6m, run.PlannedRecoupment);
        Assert.Equal(RetroRateItemAction.PayDifference, run.Items.Single(i => i.ClaimId == "a").Action);
        Assert.Equal(RetroRateItemAction.Recoup, run.Items.Single(i => i.ClaimId == "c").Action);
        Assert.Equal(RetroRateItemAction.SkipRecoupmentNotOptedIn, run.Items.Single(i => i.ClaimId == "b").Action);
        Assert.Equal(RetroRateItemAction.SkipUndecided, run.Items.Single(i => i.ClaimId == "d").Action);
        Assert.Equal(RetroRateItemAction.SkipNotYetPaid, run.Items.Single(i => i.ClaimId == "f").Action);
        Assert.DoesNotContain(run.Items, i => i.ClaimId == "e");
        Assert.Equal(1, run.SkippedByAction[RetroRateItemAction.SkipRecoupmentNotOptedIn]);
    }

    [Fact]
    public void Plan_WithoutOptIn_NeverRecoups()
    {
        var run = RetroRateReprocessingService.Plan(Impact, [Result("b", RetroRateDirection.Overpaid, -4m)], new RetroRateTenantOptions());

        Assert.Equal(0, run.ClaimsToAdjust);
        Assert.Equal(0m, run.PlannedRecoupment);
    }

    [Fact]
    public async Task ProposeApproveExecute_CreatesIdempotentAdjustments()
    {
        ImpactReturns(Result("a", RetroRateDirection.Underpaid, 10m));
        var claim = RetroRateMatcherTests.PaidClaim();
        claim.Id = "doc-a";
        _claims.GetByIdAsync("doc-a").Returns(claim);
        string? key = null;
        ClaimAdjustmentRequest? sent = null;
        _adjustments.CreateAdjustmentAsync("doc-a", Arg.Do<ClaimAdjustmentRequest>(r => sent = r), Arg.Do<string>(k => key = k),
                "t1", "bob", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ClaimAdjustmentResult { Outcome = ClaimAdjustmentOutcome.Created, Adjustment = new ClaimAdjustment { Id = "adj-1" } });
        var service = Service();

        var proposed = await service.ProposeAsync("t1", new RetroRateReprocessingRunRequest { ImpactRunId = "impact-1" }, "alice", CancellationToken.None);
        Assert.Equal(RetroRateReprocessingStatus.Proposed, proposed.Status);

        var approved = await service.ApproveAsync("t1", proposed.Id, "bob", CancellationToken.None);
        Assert.Equal("bob", approved.ApprovedBy);

        var done = await service.ExecuteAsync("t1", proposed.Id, "bob", null, CancellationToken.None);

        Assert.Equal(RetroRateReprocessingStatus.Completed, done.Status);
        var item = Assert.Single(done.Items);
        Assert.Equal(RetroRateItemOutcome.AdjustmentCreated, item.Outcome);
        Assert.Equal("adj-1", item.AdjustmentId);
        Assert.Equal($"retro-rate:{proposed.Id}:doc-a", key);
        Assert.StartsWith("Retro rate change: fee schedule TMHP adult revision 3", sent!.AdjustmentReason);
        Assert.Null(sent.CorrectedClaim.AdjudicationResult);
        Assert.Equal(1, done.CountsByOutcome[RetroRateItemOutcome.AdjustmentCreated]);
    }

    [Fact]
    public async Task Approve_BySameUser_IsRefused_UnlessTurnedOff()
    {
        ImpactReturns(Result("a", RetroRateDirection.Underpaid, 10m));
        var run = await Service().ProposeAsync("t1", new RetroRateReprocessingRunRequest { ImpactRunId = "impact-1" }, "alice", CancellationToken.None);

        var ex = await Assert.ThrowsAsync<RetroRateReprocessingException>(() => Service().ApproveAsync("t1", run.Id, "alice", CancellationToken.None));
        Assert.Equal(RetroRateReprocessingException.Kind.Conflict, ex.Reason);

        var approved = await Service(new RetroRateOptions { RequireSecondApprover = false }).ApproveAsync("t1", run.Id, "alice", CancellationToken.None);
        Assert.Equal(RetroRateReprocessingStatus.Approved, approved.Status);
    }

    [Fact]
    public async Task Execute_BeforeApproval_IsRefused()
    {
        ImpactReturns(Result("a", RetroRateDirection.Underpaid, 10m));
        var run = await Service().ProposeAsync("t1", new RetroRateReprocessingRunRequest { ImpactRunId = "impact-1" }, "alice", CancellationToken.None);

        var ex = await Assert.ThrowsAsync<RetroRateReprocessingException>(() => Service().ExecuteAsync("t1", run.Id, "bob", null, CancellationToken.None));
        Assert.Contains("approved", ex.Message);
        await _adjustments.DidNotReceiveWithAnyArgs().CreateAdjustmentAsync(default!, default!, default!, default!, default!, default, default);
    }

    [Fact]
    public async Task Execute_SkipsClaimsThatChangedSinceTheImpactRun()
    {
        ImpactReturns(Result("a", RetroRateDirection.Underpaid, 10m));
        var claim = RetroRateMatcherTests.PaidClaim();
        claim.Id = "doc-a";
        claim.SupersededAt = DateTime.UtcNow;
        _claims.GetByIdAsync("doc-a").Returns(claim);
        var service = Service();
        var run = await service.ProposeAsync("t1", new RetroRateReprocessingRunRequest { ImpactRunId = "impact-1" }, "alice", CancellationToken.None);
        await service.ApproveAsync("t1", run.Id, "bob", CancellationToken.None);

        var done = await service.ExecuteAsync("t1", run.Id, "bob", null, CancellationToken.None);

        Assert.Equal(RetroRateItemOutcome.ClaimChanged, done.Items.Single().Outcome);
        await _adjustments.DidNotReceiveWithAnyArgs().CreateAdjustmentAsync(default!, default!, default!, default!, default!, default, default);
    }

    [Fact]
    public async Task Execute_RecordsARefusedAdjustment()
    {
        ImpactReturns(Result("a", RetroRateDirection.Underpaid, 10m));
        var claim = RetroRateMatcherTests.PaidClaim();
        claim.Id = "doc-a";
        _claims.GetByIdAsync("doc-a").Returns(claim);
        _adjustments.CreateAdjustmentAsync(default!, default!, default!, default!, default!, default, default).ReturnsForAnyArgs(
            new ClaimAdjustmentResult { Outcome = ClaimAdjustmentOutcome.DepthLimitExceeded, Message = "already adjusted once" });
        var service = Service();
        var run = await service.ProposeAsync("t1", new RetroRateReprocessingRunRequest { ImpactRunId = "impact-1" }, "alice", CancellationToken.None);
        await service.ApproveAsync("t1", run.Id, "bob", CancellationToken.None);

        var item = (await service.ExecuteAsync("t1", run.Id, "bob", null, CancellationToken.None)).Items.Single();

        Assert.Equal(RetroRateItemOutcome.Refused, item.Outcome);
        Assert.Contains("DepthLimitExceeded", item.Message);
    }

    [Fact]
    public async Task Propose_WithNothingToAdjust_IsCompletedAtOnce()
    {
        ImpactReturns(Result("e", RetroRateDirection.Unchanged, 0m));

        var run = await Service().ProposeAsync("t1", new RetroRateReprocessingRunRequest { ImpactRunId = "impact-1" }, "alice", CancellationToken.None);

        Assert.Equal(RetroRateReprocessingStatus.Completed, run.Status);
        Assert.Equal(0, run.ClaimsToAdjust);
    }

    [Fact]
    public async Task Cancel_StopsAnApprovedRun()
    {
        ImpactReturns(Result("a", RetroRateDirection.Underpaid, 10m));
        var service = Service();
        var run = await service.ProposeAsync("t1", new RetroRateReprocessingRunRequest { ImpactRunId = "impact-1" }, "alice", CancellationToken.None);
        await service.ApproveAsync("t1", run.Id, "bob", CancellationToken.None);

        var cancelled = await service.CancelAsync("t1", run.Id, "alice", CancellationToken.None);

        Assert.Equal(RetroRateReprocessingStatus.Cancelled, cancelled.Status);
        await Assert.ThrowsAsync<RetroRateReprocessingException>(() => service.ExecuteAsync("t1", run.Id, "bob", null, CancellationToken.None));
    }

    /// <summary>Copies on write and read, like a database, and checks the revision.</summary>
    private sealed class InMemoryRuns : IRetroRateReprocessingRepository
    {
        private readonly Dictionary<string, string> _rows = new();

        private static string Ser(RetroRateReprocessingRun r) => System.Text.Json.JsonSerializer.Serialize(r);
        private static RetroRateReprocessingRun De(string s) => System.Text.Json.JsonSerializer.Deserialize<RetroRateReprocessingRun>(s)!;

        public Task CreateAsync(RetroRateReprocessingRun run, CancellationToken ct = default)
        {
            _rows[run.Id] = Ser(run);
            return Task.CompletedTask;
        }

        public Task<RetroRateReprocessingRun?> GetAsync(string tenantId, string runId, CancellationToken ct = default) =>
            Task.FromResult(_rows.TryGetValue(runId, out var s) && De(s).TenantId == tenantId ? De(s) : null);

        public Task<IReadOnlyList<RetroRateReprocessingRun>> ListAsync(string tenantId, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RetroRateReprocessingRun>>(_rows.Values.Select(De).ToList());

        public Task<bool> TryReplaceAsync(RetroRateReprocessingRun run, long expectedRevision, CancellationToken ct = default)
        {
            if (!_rows.TryGetValue(run.Id, out var s) || De(s).ConcurrencyRevision != expectedRevision)
                return Task.FromResult(false);
            run.ConcurrencyRevision = expectedRevision + 1;
            _rows[run.Id] = Ser(run);
            return Task.FromResult(true);
        }
    }
}
