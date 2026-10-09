using ArService.Controllers;
using ArService.Ledger;
using ArService.Models;
using ArService.Repositories;
using ArService.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace ArService.Tests.Controllers;

/// <summary>
/// A posting applied before applying credited balances (legacy) has applications with an
/// amount and no PostedEntryId. Finance may already have corrected those balances by hand, so
/// apply and void refuse it (409) until it is reconciled; after reconciliation neither credits
/// nor reverses an application finance corrected by hand.
/// </summary>
public class LegacyPostingGuardTests
{
    private readonly Mock<ICashPostingRepository> _postings = new();
    private readonly FakeArBalanceRepository _balances = new();
    private readonly CashPostingController _controller;
    private CashPosting? _saved;

    public LegacyPostingGuardTests()
    {
        _controller = new CashPostingController(_postings.Object, _balances, new TestActor("cash-poster"), Mock.Of<ILogger<CashPostingController>>());
        _postings.Setup(r => r.UpdateAsync(It.IsAny<CashPosting>())).ReturnsAsync((CashPosting p) => { _saved = p; return p; });
        _balances.Add("bal-1", "gl-1200", openingBalance: 1000m);
        _balances.Add("bal-2", "gl-1200", openingBalance: 500m);
    }

    /// <summary>A posting as the old apply left it: status and AppliedAmount set, nothing posted.</summary>
    private CashPosting Legacy(CashPostingStatus status, decimal amount, params (string BalanceId, decimal Amount)[] applications)
    {
        var posting = new CashPosting
        {
            Id = "cp-1",
            TenantId = "tenant-1",
            PostingNumber = "CP-20260301-LEGACY01",
            ReceiptDate = new DateTime(2026, 3, 1),
            Amount = amount,
            PayerType = PayerType.Sponsor,
            PayerReferenceId = "GRP001",
            Status = status,
            AppliedAmount = applications.Sum(a => a.Amount),
            UnappliedAmount = amount - applications.Sum(a => a.Amount),
            Applications = applications.Select(a => new CashApplication
            {
                ArBalanceId = a.BalanceId, GlAccountId = "gl-1200", AmountApplied = a.Amount, Period = new DateTime(2026, 3, 1)
            }).ToList()
        };
        _postings.Setup(r => r.GetByIdAsync("cp-1")).ReturnsAsync(posting);
        return posting;
    }

    private static void ShouldBeLegacyConflict(ActionResult<CashPosting> result)
    {
        var conflict = result.Result.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        conflict.Value!.GetType().GetProperty("code")!.GetValue(conflict.Value)
            .Should().Be(CashPostingLedger.LegacyPostingRequiresReconciliation);
    }

    [Fact]
    public async Task Apply_LegacyPartiallyApplied_Is409_AndCreditsNothing()
    {
        Legacy(CashPostingStatus.PartiallyApplied, 1000m, ("bal-1", 600m));

        var result = await _controller.ApplyCashPosting("cp-1");

        ShouldBeLegacyConflict(result);
        _balances["bal-1"].PostingEntries.Should().BeEmpty();
        _balances["bal-1"].ClosingBalance.Should().Be(1000m);
        _balances.Updates.Should().Be(0);
        _saved.Should().BeNull();
    }

    [Fact]
    public async Task Apply_LegacyApplied_Is409()
    {
        Legacy(CashPostingStatus.Applied, 600m, ("bal-1", 600m));

        ShouldBeLegacyConflict(await _controller.ApplyCashPosting("cp-1"));
        _balances.Updates.Should().Be(0);
    }

    [Fact]
    public async Task Void_LegacyPartiallyApplied_Is409_AndDebitsNothing()
    {
        Legacy(CashPostingStatus.PartiallyApplied, 1000m, ("bal-1", 600m));

        var result = await _controller.VoidCashPosting("cp-1");

        ShouldBeLegacyConflict(result);
        _balances["bal-1"].PostingEntries.Should().BeEmpty();
        _saved.Should().BeNull();
    }

    [Fact]
    public async Task Apply_PendingReviewMarker_Is409()
    {
        var posting = Legacy(CashPostingStatus.PartiallyApplied, 1000m, ("bal-1", 600m));
        posting.Applications[0].PostedEntryId = "cash-cp-1-0";
        posting.LegacyReconciliation = new LegacyPostingReconciliation { Status = LegacyReconciliationStatus.PendingReview };

        ShouldBeLegacyConflict(await _controller.ApplyCashPosting("cp-1"));
    }

    [Fact]
    public async Task Pending_IsNotLegacy_AndIsCredited()
    {
        var posting = Legacy(CashPostingStatus.Pending, 1000m, ("bal-1", 600m));
        posting.AppliedAmount = 0m;

        var result = await _controller.ApplyCashPosting("cp-1");

        result.Result.Should().BeOfType<OkObjectResult>();
        _balances["bal-1"].TotalCredits.Should().Be(600m);
    }

    [Fact]
    public async Task PostedByCurrentCode_WithANewApplication_IsNotLegacy()
    {
        // Applied by the current code (posted id set), then given another application.
        var posting = Legacy(CashPostingStatus.PartiallyApplied, 1000m, ("bal-1", 600m), ("bal-2", 100m));
        posting.Applications[0].PostedEntryId = "cash-cp-1-0";

        var result = await _controller.ApplyCashPosting("cp-1");

        result.Result.Should().BeOfType<OkObjectResult>();
        _balances["bal-2"].TotalCredits.Should().Be(100m);
        _balances["bal-1"].PostingEntries.Should().BeEmpty();
    }

    [Fact]
    public void IsLegacy_FollowsTheData()
    {
        var legacy = Legacy(CashPostingStatus.PartiallyApplied, 1000m, ("bal-1", 600m), ("bal-2", 0m));
        CashPostingLedger.IsLegacy(legacy).Should().BeTrue();
        CashPostingLedger.LegacyApplications(legacy).Select(x => x.Index).Should().Equal(0);

        legacy.Status = CashPostingStatus.Voided;
        CashPostingLedger.IsLegacy(legacy).Should().BeFalse("a voided posting can be neither applied nor voided");
        legacy.Status = CashPostingStatus.Pending;
        CashPostingLedger.IsLegacy(legacy).Should().BeFalse("a pending posting was never applied");

        var zero = Legacy(CashPostingStatus.PartiallyApplied, 1000m, ("bal-1", 0m));
        CashPostingLedger.IsLegacy(zero).Should().BeFalse("nothing to credit");
    }

    /// <summary>The state the reconciliation tool leaves for CORRECTED_MANUALLY.</summary>
    private CashPosting ReconciledManually(CashPostingStatus status)
    {
        var posting = Legacy(status, 1000m, ("bal-1", 600m));
        posting.Applications[0].PostedEntryId = CashPostingLedger.ManualEntryId("cp-1", 0);
        posting.LegacyReconciliation = new LegacyPostingReconciliation
        {
            Status = LegacyReconciliationStatus.Reconciled,
            Applications = { new LegacyApplicationDecision { ApplicationIndex = 0, ArBalanceId = "bal-1", Amount = 600m, Decision = LegacyReconciliationDecision.CorrectedManually, PostedEntryId = "manual-cp-1-0" } }
        };
        return posting;
    }

    [Fact]
    public async Task Apply_AfterCorrectedManually_DoesNotCredit()
    {
        ReconciledManually(CashPostingStatus.PartiallyApplied);

        var result = await _controller.ApplyCashPosting("cp-1");

        result.Result.Should().BeOfType<OkObjectResult>();
        _balances["bal-1"].PostingEntries.Should().BeEmpty();
        _balances["bal-1"].ClosingBalance.Should().Be(1000m);
        _saved!.Applications[0].PostedEntryId.Should().Be("manual-cp-1-0");
    }

    [Fact]
    public async Task Void_AfterCorrectedManually_DoesNotDebit()
    {
        ReconciledManually(CashPostingStatus.PartiallyApplied);

        var result = await _controller.VoidCashPosting("cp-1");

        result.Result.Should().BeOfType<OkObjectResult>();
        _balances["bal-1"].PostingEntries.Should().BeEmpty();
        _balances["bal-1"].TotalDebits.Should().Be(0m);
        _balances["bal-1"].ClosingBalance.Should().Be(1000m);
        _saved!.Status.Should().Be(CashPostingStatus.Voided);
    }

    [Fact]
    public async Task Void_AfterApplyCredit_ReversesTheReconciliationCredit()
    {
        var posting = Legacy(CashPostingStatus.PartiallyApplied, 1000m, ("bal-1", 600m));
        // What the tool does for APPLY_CREDIT: the controller's own entry on the balance.
        _balances.ChangeStored("bal-1", b => CashPostingLedger.Post(b, CashPostingLedger.CreditEntry(posting, 0, "ops", DateTime.UtcNow), posting.PayerType));
        posting.Applications[0].PostedEntryId = "cash-cp-1-0";
        posting.LegacyReconciliation = new LegacyPostingReconciliation { Status = LegacyReconciliationStatus.Reconciled };

        var result = await _controller.VoidCashPosting("cp-1");

        result.Result.Should().BeOfType<OkObjectResult>();
        _balances["bal-1"].TotalCredits.Should().Be(600m);
        _balances["bal-1"].TotalDebits.Should().Be(600m);
        _balances["bal-1"].ClosingBalance.Should().Be(1000m);
    }

    [Fact]
    public async Task Create_ClearsClientSuppliedReconciliation()
    {
        _postings.Setup(r => r.CreateAsync(It.IsAny<CashPosting>())).ReturnsAsync((CashPosting p) => p);
        var posting = new CashPosting
        {
            Amount = 100m, PayerType = PayerType.Sponsor, PayerReferenceId = "GRP001",
            LegacyReconciliation = new LegacyPostingReconciliation { Status = LegacyReconciliationStatus.Reconciled }
        };

        await _controller.CreateCashPosting(posting);

        posting.LegacyReconciliation.Should().BeNull();
    }
}
