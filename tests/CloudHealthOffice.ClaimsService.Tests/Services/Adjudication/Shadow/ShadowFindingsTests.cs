using ClaimsService.Controllers;
using ClaimsService.Services.Adjudication.Shadow;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Shadow;

public class ShadowFindingsTests
{
    private static readonly DateTime Now = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
    private static readonly ShadowAdjudicationRun Run = new() { Id = "run1", TenantId = "plan-a", Tolerance = 0.01m };

    [Fact]
    public void PaymentDifferences_BecomeFindings_AndMatchesOrUndecidedDoNot()
    {
        var results = new[]
        {
            Result("C1", ShadowDiscrepancyKind.AllowedVariance, corePaid: 100m, choPaid: 70m),
            Result("C2", ShadowDiscrepancyKind.PaidVariance, corePaid: 50m, choPaid: 65m),
            Result("C3", ShadowDiscrepancyKind.Match, corePaid: 40m, choPaid: 40m),
            Result("C4", ShadowDiscrepancyKind.ChoUndecided, corePaid: 40m, choPaid: 0m),
            // Within tolerance: no finding.
            Result("C5", ShadowDiscrepancyKind.PaidVariance, corePaid: 40m, choPaid: 40.005m),
        };

        var findings = ShadowFindingBuilder.Merge(Run, results, new Dictionary<string, ShadowFinding>(), Now);

        Assert.Equal(["plan-a:C1", "plan-a:C2"], findings.Select(f => f.Id));
        var over = findings[0];
        Assert.Equal(ShadowFindingDirection.CorePaidMore, over.Direction);
        Assert.Equal(30m, over.Amount);
        Assert.Equal(-30m, over.AdjustmentAmount);
        Assert.Equal("Pricing", over.Stage);
        Assert.Equal(new DateTime(2026, 3, 10), over.CorePaidDate);
        Assert.Equal(ShadowFindingStatus.New, over.Status);
        Assert.Equal(ShadowFindingDirection.CorePaidLess, findings[1].Direction);
    }

    [Fact]
    public void ALaterRun_RefreshesUnreviewedFindings_KeepsReviewedOnes_AndMarksResolvedClaims()
    {
        var unreviewed = Finding("C1", amount: 30m);
        var approved = Finding("C2", amount: 15m, status: ShadowFindingStatus.Approved);
        var gone = Finding("C3", amount: 10m);
        var existing = new[] { unreviewed, approved, gone }.ToDictionary(f => f.Id);
        var later = new ShadowAdjudicationRun { Id = "run2", TenantId = "plan-a", Tolerance = 0.01m };

        var changed = ShadowFindingBuilder.Merge(later, new[]
        {
            Result("C1", ShadowDiscrepancyKind.AllowedVariance, 100m, 60m, runId: "run2"),
            Result("C2", ShadowDiscrepancyKind.AllowedVariance, 100m, 50m, runId: "run2"),
            Result("C3", ShadowDiscrepancyKind.Match, 100m, 100m, runId: "run2"),
        }, existing, Now);

        Assert.Equal(3, changed.Count);
        Assert.Equal(40m, unreviewed.Amount);
        Assert.Equal("run2", unreviewed.RunId);
        Assert.Equal(15m, approved.Amount);
        Assert.Equal("run1", approved.RunId);
        Assert.True(approved.StillDiffers);
        Assert.False(gone.StillDiffers);
    }

    [Theory]
    [InlineData(ShadowFindingStatus.New, ShadowFindingStatus.Approved, true)]
    [InlineData(ShadowFindingStatus.New, ShadowFindingStatus.Recovered, false)]
    [InlineData(ShadowFindingStatus.Approved, ShadowFindingStatus.InRecovery, true)]
    [InlineData(ShadowFindingStatus.InRecovery, ShadowFindingStatus.Appealed, true)]
    [InlineData(ShadowFindingStatus.Appealed, ShadowFindingStatus.InRecovery, true)]
    [InlineData(ShadowFindingStatus.Rejected, ShadowFindingStatus.New, true)]
    [InlineData(ShadowFindingStatus.Recovered, ShadowFindingStatus.InRecovery, false)]
    public void StatusChanges_FollowTheReviewFlow(string from, string to, bool allowed) =>
        Assert.Equal(allowed, ShadowFindingStatus.CanMove(from, to));

    [Fact]
    public void RecordingARecovery_NeedsAnAmountNoMoreThanTheFinding_AndKeepsHistory()
    {
        var f = Finding("C1", amount: 30m, status: ShadowFindingStatus.InRecovery);

        Assert.NotNull(ShadowFindingBuilder.Move(f, new() { Status = "Recovered" }, "u1", Now));
        Assert.NotNull(ShadowFindingBuilder.Move(f, new() { Status = "Recovered", RecoveredAmount = 31m }, "u1", Now));
        Assert.Contains("closed", ShadowFindingBuilder.Move(
            Finding("C9", 5m, ShadowFindingStatus.Recovered), new() { Status = "Approved" }, "u1", Now));

        Assert.Null(ShadowFindingBuilder.Move(f, new() { Status = "recovered", RecoveredAmount = 25m, Note = "offset on 12/1 remit" }, "u1", Now));
        Assert.Equal(ShadowFindingStatus.Recovered, f.Status);
        Assert.Equal(25m, f.RecoveredAmount);
        Assert.Equal("u1", f.StatusChangedBy);
        var e = Assert.Single(f.History);
        Assert.Equal((ShadowFindingStatus.InRecovery, ShadowFindingStatus.Recovered, 25m), (e.From, e.To, e.RecoveredAmount));
    }

    [Fact]
    public void Lookback_UsesTheProvidersContract_ElseThePlanDefault_FromThePaidDate()
    {
        var lookback = new ShadowRecoveryLookbackSettings
        {
            Id = "plan-a", DefaultDays = 365,
            Providers = { new() { Npi = "1111111111", ContractNumber = "K-7", Days = 180 } },
        };
        var paid = new DateTime(2026, 6, 1);

        // 180 days from May 1 is October 28: past.
        var contract = ShadowFindingView.For(Finding("C1", 10m, npi: "1111111111", paid: new DateTime(2026, 5, 1)), lookback, Now);
        Assert.Equal(ShadowLookbackState.Past, contract.LookbackState);
        Assert.Equal(new DateTime(2026, 10, 28), contract.LookbackDeadline);
        Assert.Equal(-26, contract.DaysLeft);
        Assert.Equal("K-7", contract.ContractNumber);

        var plan = ShadowFindingView.For(Finding("C2", 10m, npi: "2222222222", paid: paid), lookback, Now);
        Assert.Equal(ShadowLookbackState.Within, plan.LookbackState);
        Assert.Equal(new DateTime(2027, 6, 1), plan.LookbackDeadline);

        var dueSoon = ShadowFindingView.For(Finding("C3", 10m, npi: "2222222222", paid: new DateTime(2025, 12, 10)), lookback, Now);
        Assert.Equal(ShadowLookbackState.DueSoon, dueSoon.LookbackState);

        var under = Finding("C4", 10m, paid: paid);
        under.Direction = ShadowFindingDirection.CorePaidLess;
        Assert.Equal(ShadowLookbackState.NotApplicable, ShadowFindingView.For(under, lookback, Now).LookbackState);
        Assert.Equal(ShadowLookbackState.Unknown, ShadowFindingView.For(Finding("C5", 10m, paid: paid), null, Now).LookbackState);
    }

    [Fact]
    public void TheSummary_TotalsByStatus_RecoveredAgainstConfirmed_AndRollsUpProviders()
    {
        var lookback = new ShadowRecoveryLookbackSettings { Id = "plan-a", DefaultDays = 90 };
        var recovered = Finding("C1", 100m, ShadowFindingStatus.Recovered, npi: "1111111111");
        recovered.RecoveredAmount = 80m;
        var findings = new[]
        {
            recovered,
            Finding("C2", 50m, ShadowFindingStatus.Approved, npi: "1111111111"),
            Finding("C3", 40m, ShadowFindingStatus.New, npi: "2222222222", paid: new DateTime(2026, 1, 5)),
            Finding("C4", 30m, ShadowFindingStatus.Rejected, npi: "2222222222"),
        };
        var under = Finding("C5", 20m, ShadowFindingStatus.New, npi: "2222222222");
        under.Direction = ShadowFindingDirection.CorePaidLess;

        var views = findings.Append(under).Select(f => ShadowFindingView.For(f, lookback, Now)).ToList();
        var summary = ShadowFindingSummaryBuilder.Build(views);

        Assert.Equal(5, summary.Findings);
        Assert.Equal(190m, summary.Overpayments.Amount);
        Assert.Equal(20m, summary.Underpayments.Amount);
        Assert.Equal(150m, summary.Confirmed.Amount);
        Assert.Equal(80m, summary.Recovered.Recovered);
        Assert.Equal(0.5333m, summary.RecoveryRate);
        Assert.Equal(1, summary.ByStatus.Single(r => r.Status == ShadowFindingStatus.Rejected).Findings);
        Assert.Equal(1, summary.PastLookback.Findings);
        Assert.Equal(40m, summary.PastLookback.Amount);

        var first = summary.ByProvider[0];
        Assert.Equal("1111111111", first.BillingProviderNpi);
        Assert.Equal(50m, first.OpenOverpayments);
        Assert.Equal(80m, first.Recovered);
        var second = summary.ByProvider[1];
        Assert.Equal(40m, second.OpenOverpayments);
        Assert.Equal(20m, second.CorePaidLess);
        Assert.Equal(1, second.PastLookback);
    }

    [Fact]
    public void TheExport_IsOneRowPerFinding_FormulaSafe()
    {
        var f = Finding("C1", 30m, ShadowFindingStatus.Approved);
        f.Note = "=HYPERLINK(\"x\")";
        var csv = ShadowFindingCsv.Write([ShadowFindingView.For(f, null, Now)]);

        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("FindingId,SourceSystem,SourceClaimId,ClaimNumber", lines[0]);
        Assert.Contains(",-30.00,", lines[1]);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", lines[1]);
    }

    [Fact]
    public async Task TheService_AddsARunsDifferences_ToTheStore()
    {
        var store = Substitute.For<IShadowFindingRepository>();
        store.GetManyAsync("plan-a", Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ShadowFinding>());
        IReadOnlyList<ShadowFinding>? saved = null;
        await store.SaveManyAsync(Arg.Do<IReadOnlyList<ShadowFinding>>(x => saved = x), Arg.Any<CancellationToken>());

        var n = await new ShadowFindingService(store).AddRunAsync(Run,
        [
            Result("C1", ShadowDiscrepancyKind.AllowedVariance, 100m, 70m),
            Result("C2", ShadowDiscrepancyKind.Match, 10m, 10m),
        ]);

        Assert.Equal(1, n);
        Assert.Equal("plan-a:C1", Assert.Single(saved!).Id);
    }

    [Fact]
    public async Task AStatusChangeThatRacesAnother_IsRefused()
    {
        var store = Substitute.For<IShadowFindingRepository>();
        store.GetAsync("plan-a", "plan-a:C1", Arg.Any<CancellationToken>()).Returns(Finding("C1", 30m));
        store.ReplaceIfStatusAsync(Arg.Any<ShadowFinding>(), ShadowFindingStatus.New, Arg.Any<CancellationToken>()).Returns(false);
        var actor = Substitute.For<ICurrentActor>();
        actor.TenantId.Returns("plan-a");
        actor.UserId.Returns("u1");
        var controller = new ShadowFindingsController(store, Substitute.For<IShadowFindingService>(),
            Substitute.For<IShadowAdjudicationRepository>(), actor);

        var answer = await controller.SetStatus("plan-a:C1", new() { Status = "Approved" });

        Assert.IsType<ConflictObjectResult>(answer.Result);
    }

    private static ShadowAdjudicationResult Result(
        string claim, string kind, decimal corePaid, decimal choPaid, string runId = "run1") => new()
    {
        Id = $"{runId}-{claim}",
        RunId = runId,
        TenantId = "plan-a",
        ClaimId = claim,
        ClaimNumber = claim,
        Kind = kind,
        CoreOutcome = ShadowOutcome.Paid,
        ChoOutcome = kind == ShadowDiscrepancyKind.ChoUndecided ? ShadowOutcome.Pended : ShadowOutcome.Paid,
        CorePaid = corePaid,
        ChoPaid = choPaid,
        PaidVariance = choPaid - corePaid,
        AbsolutePaidVariance = Math.Abs(choPaid - corePaid),
        CorePaidDate = new DateTime(2026, 3, 10),
        BillingProviderNpi = "1111111111",
    };

    private static ShadowFinding Finding(
        string claim, decimal amount, string status = ShadowFindingStatus.New, string npi = "1111111111", DateTime? paid = null) => new()
    {
        Id = ShadowFinding.IdFor("plan-a", claim),
        TenantId = "plan-a",
        ClaimId = claim,
        ClaimNumber = claim,
        Direction = ShadowFindingDirection.CorePaidMore,
        Amount = amount,
        AdjustmentAmount = -amount,
        Status = status,
        RunId = "run1",
        BillingProviderNpi = npi,
        CorePaidDate = paid ?? new DateTime(2026, 10, 1),
    };
}

public class InMemoryShadowFindingRepositoryTests
{
    [Fact]
    public async Task AReviewThroughTheController_IsSaved_AndASecondOneFromTheSameStateIsRefused()
    {
        var store = new InMemoryShadowFindingRepository();
        await store.SaveManyAsync([new ShadowFinding { Id = "plan-a:C1", TenantId = "plan-a", ClaimId = "C1", Amount = 30m }]);
        var actor = Substitute.For<ICurrentActor>();
        actor.TenantId.Returns("plan-a");
        actor.UserId.Returns("u1");
        var controller = new ShadowFindingsController(store, new ShadowFindingService(store),
            Substitute.For<IShadowAdjudicationRepository>(), actor);

        var first = await controller.SetStatus("plan-a:C1", new() { Status = "Approved" });
        Assert.IsType<OkObjectResult>(first.Result);
        Assert.Equal(ShadowFindingStatus.Approved, (await store.GetAsync("plan-a", "plan-a:C1"))!.Status);

        // A stale copy read before the approval cannot overwrite it.
        var stale = new ShadowFinding { Id = "plan-a:C1", TenantId = "plan-a", Status = ShadowFindingStatus.Rejected };
        Assert.False(await store.ReplaceIfStatusAsync(stale, ShadowFindingStatus.New));
    }
}
