using ArService.Controllers;
using ArService.Models;
using ArService.Repositories;
using ArService.Tests.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace ArService.Tests.Controllers;

/// <summary>
/// Applying cash must move the AR balances it names. Before this, apply only
/// set the posting's status and AppliedAmount: no balance was ever credited.
/// Balances are re-read from the repository after each action.
/// </summary>
public class CashPostingBalanceTests
{
    private readonly Mock<ICashPostingRepository> _postings = new();
    private readonly FakeArBalanceRepository _balances = new();
    private readonly CashPostingController _controller;
    private CashPosting? _saved;

    public CashPostingBalanceTests()
    {
        _controller = new CashPostingController(_postings.Object, _balances, new TestActor("cash-poster"), Mock.Of<ILogger<CashPostingController>>());
        _postings.Setup(r => r.UpdateAsync(It.IsAny<CashPosting>())).ReturnsAsync((CashPosting p) => { _saved = p; return p; });
    }

    private CashPosting Posting(decimal amount, PayerType payer, params (string BalanceId, string Gl, decimal Amount)[] applications)
    {
        var posting = new CashPosting
        {
            Id = "cp-1",
            TenantId = "tenant-1",
            PostingNumber = "CP-20260306-0001",
            ReceiptDate = new DateTime(2026, 3, 6),
            Amount = amount,
            PayerType = payer,
            PayerReferenceId = payer == PayerType.Member ? "MBR-1" : "GRP001",
            Applications = applications.Select(a => new CashApplication
            {
                ArBalanceId = a.BalanceId, GlAccountId = a.Gl, AmountApplied = a.Amount, Period = new DateTime(2026, 3, 1), Memo = "premium"
            }).ToList()
        };
        _postings.Setup(r => r.GetByIdAsync("cp-1")).ReturnsAsync(posting);
        return posting;
    }

    [Fact]
    public async Task Apply_CreditsEachBalance()
    {
        _balances.Add("bal-mar", "gl-1200", openingBalance: 5000m);
        _balances.Add("bal-feb", "gl-1200", openingBalance: 800m);
        Posting(3000m, PayerType.Sponsor, ("bal-mar", "gl-1200", 2000m), ("bal-feb", "gl-1200", 800m));

        var result = await _controller.ApplyCashPosting("cp-1");

        result.Result.Should().BeOfType<OkObjectResult>();
        var march = _balances["bal-mar"];
        march.TotalCredits.Should().Be(2000m);
        march.ClosingBalance.Should().Be(3000m);
        march.SponsorCredits.Should().Be(2000m);
        march.SponsorBalance.Should().Be(3000m);
        var entry = march.PostingEntries.Should().ContainSingle().Subject;
        entry.Source.Should().Be(ArPostingSource.CashReceipt);
        entry.CreditAmount.Should().Be(2000m);
        entry.SourceReferenceId.Should().Be("cp-1");
        entry.SourceReferenceNumber.Should().Be("CP-20260306-0001");
        entry.PostedBy.Should().Be("cash-poster");
        _balances["bal-feb"].ClosingBalance.Should().Be(0m);

        _saved!.Status.Should().Be(CashPostingStatus.PartiallyApplied);
        _saved.UnappliedAmount.Should().Be(200m);
        _saved.Applications.Should().OnlyContain(a => a.PostedEntryId != null);
    }

    [Fact]
    public async Task Apply_MemberPayment_CreditsTheMemberSide()
    {
        _balances.Add("bal-1", "gl-1200");
        Posting(150m, PayerType.Member, ("bal-1", "gl-1200", 150m));

        await _controller.ApplyCashPosting("cp-1");

        var balance = _balances["bal-1"];
        balance.MemberCredits.Should().Be(150m);
        balance.MemberBalance.Should().Be(-150m);
        balance.SponsorCredits.Should().Be(0m);
        balance.PostingEntries.Single().MemberId.Should().Be("MBR-1");
    }

    [Fact]
    public async Task Apply_TwoApplicationsToOneBalance_CreditsBoth()
    {
        _balances.Add("bal-1", "gl-1200", openingBalance: 1000m);
        Posting(1000m, PayerType.Sponsor, ("bal-1", "gl-1200", 600m), ("bal-1", "gl-1200", 400m));

        await _controller.ApplyCashPosting("cp-1");

        _balances["bal-1"].PostingEntries.Should().HaveCount(2);
        _balances["bal-1"].ClosingBalance.Should().Be(0m);
        _balances.Updates.Should().Be(1);
    }

    [Fact]
    public async Task Apply_UnknownBalance_Is400_AndCreditsNothing()
    {
        _balances.Add("bal-1", "gl-1200", openingBalance: 1000m);
        Posting(1000m, PayerType.Sponsor, ("bal-1", "gl-1200", 500m), ("bal-missing", "gl-1200", 500m));

        var result = await _controller.ApplyCashPosting("cp-1");

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        _balances["bal-1"].PostingEntries.Should().BeEmpty();
        _balances["bal-1"].ClosingBalance.Should().Be(1000m);
        _saved.Should().BeNull();
    }

    [Fact]
    public async Task Apply_BalanceOfAnotherGlAccount_Is400()
    {
        _balances.Add("bal-1", "gl-4000");
        Posting(100m, PayerType.Sponsor, ("bal-1", "gl-1200", 100m));

        var result = await _controller.ApplyCashPosting("cp-1");

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        _balances["bal-1"].PostingEntries.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyingAgain_AfterAddingAnApplication_CreditsOnlyTheNewOne()
    {
        _balances.Add("bal-1", "gl-1200", openingBalance: 1000m);
        var posting = Posting(1000m, PayerType.Sponsor, ("bal-1", "gl-1200", 600m));
        await _controller.ApplyCashPosting("cp-1");
        posting.Applications.Add(new CashApplication { ArBalanceId = "bal-1", GlAccountId = "gl-1200", AmountApplied = 400m, Period = new DateTime(2026, 3, 1) });

        await _controller.ApplyCashPosting("cp-1");

        _balances["bal-1"].TotalCredits.Should().Be(1000m);
        _balances["bal-1"].ClosingBalance.Should().Be(0m);
        _saved!.Status.Should().Be(CashPostingStatus.Applied);
    }

    [Fact]
    public async Task RetryAfterAFailedSave_DoesNotCreditTwice()
    {
        _balances.Add("bal-1", "gl-1200", openingBalance: 1000m);
        Posting(1000m, PayerType.Sponsor, ("bal-1", "gl-1200", 700m));
        _postings.Setup(r => r.UpdateAsync(It.IsAny<CashPosting>())).ThrowsAsync(new TimeoutException("db"));
        await FluentActions.Invoking(() => _controller.ApplyCashPosting("cp-1")).Should().ThrowAsync<TimeoutException>();

        // The posting was not saved, so it is re-read without PostedEntryId.
        Posting(1000m, PayerType.Sponsor, ("bal-1", "gl-1200", 700m));
        _postings.Setup(r => r.UpdateAsync(It.IsAny<CashPosting>())).ReturnsAsync((CashPosting p) => { _saved = p; return p; });
        await _controller.ApplyCashPosting("cp-1");

        _balances["bal-1"].PostingEntries.Should().ContainSingle();
        _balances["bal-1"].TotalCredits.Should().Be(700m);
        _saved!.Applications.Single().PostedEntryId.Should().Be("cash-cp-1-0");
    }

    [Fact]
    public async Task ConcurrentBalanceChange_IsKept_AndTheCreditIsRetriedOnTop()
    {
        _balances.Add("bal-1", "gl-1200", openingBalance: 1000m);
        Posting(300m, PayerType.Sponsor, ("bal-1", "gl-1200", 300m));
        var interfered = false;
        _balances.BeforeUpdate = (repo, _) =>
        {
            if (interfered) return;
            interfered = true;
            // Someone posts a 50.00 debit between our read and our save.
            repo.ChangeStored("bal-1", b =>
            {
                b.PostingEntries.Add(new ArPostingEntry { EntryId = "other", DebitAmount = 50m, Source = ArPostingSource.ManualAdjustment });
                b.TotalDebits += 50m;
                b.ClosingBalance = b.OpeningBalance + b.TotalDebits - b.TotalCredits;
            });
        };

        await _controller.ApplyCashPosting("cp-1");

        var balance = _balances["bal-1"];
        balance.PostingEntries.Select(e => e.EntryId).Should().BeEquivalentTo("other", "cash-cp-1-0");
        balance.ClosingBalance.Should().Be(750m); // 1000 + 50 − 300
    }

    [Fact]
    public async Task VoidingAPartiallyAppliedPosting_ReversesItsCredits()
    {
        _balances.Add("bal-1", "gl-1200", openingBalance: 1000m);
        var posting = Posting(1000m, PayerType.Sponsor, ("bal-1", "gl-1200", 600m));
        await _controller.ApplyCashPosting("cp-1");
        posting.Status = CashPostingStatus.PartiallyApplied;

        var result = await _controller.VoidCashPosting("cp-1");

        result.Result.Should().BeOfType<OkObjectResult>();
        var balance = _balances["bal-1"];
        balance.TotalCredits.Should().Be(600m);
        balance.TotalDebits.Should().Be(600m);
        balance.ClosingBalance.Should().Be(1000m);
        balance.SponsorBalance.Should().Be(1000m);
        balance.PostingEntries.Last().EntryId.Should().Be("rev-cash-cp-1-0");
        balance.PostingEntries.Last().SourceReferenceNumber.Should().Be("REV-CP-20260306-0001");
        _saved!.Status.Should().Be(CashPostingStatus.Voided);
    }

    [Fact]
    public async Task VoidRetried_AfterAFailedSave_DoesNotReverseTwice_AndReversesTheOriginalCredit()
    {
        _balances.Add("bal-1", "gl-1200", openingBalance: 1000m);
        var posting = Posting(1000m, PayerType.Sponsor, ("bal-1", "gl-1200", 600m));
        await _controller.ApplyCashPosting("cp-1");
        posting.Status = CashPostingStatus.PartiallyApplied;
        // The application's amount was edited after it was credited: the reversal follows the entry.
        posting.Applications[0].AmountApplied = 999m;
        _postings.Setup(r => r.UpdateAsync(It.IsAny<CashPosting>())).ThrowsAsync(new TimeoutException("db"));
        await FluentActions.Invoking(() => _controller.VoidCashPosting("cp-1")).Should().ThrowAsync<TimeoutException>();
        _postings.Setup(r => r.UpdateAsync(It.IsAny<CashPosting>())).ReturnsAsync((CashPosting p) => { _saved = p; return p; });

        await _controller.VoidCashPosting("cp-1");

        var balance = _balances["bal-1"];
        balance.PostingEntries.Count(e => e.EntryId.StartsWith("rev-")).Should().Be(1);
        balance.TotalDebits.Should().Be(600m);
        balance.ClosingBalance.Should().Be(1000m);
    }

    [Fact]
    public async Task Create_ClearsClientSuppliedPostedState()
    {
        _postings.Setup(r => r.CreateAsync(It.IsAny<CashPosting>())).ReturnsAsync((CashPosting p) => p);
        var posting = new CashPosting
        {
            Amount = 500m,
            AppliedAmount = 500m,
            PayerType = PayerType.Sponsor,
            PayerReferenceId = "GRP001",
            Applications =
            {
                new CashApplication { ArBalanceId = "bal-1", GlAccountId = "gl-1200", AmountApplied = 500m, PostedEntryId = "forged", PostedAt = DateTime.UtcNow }
            }
        };

        await _controller.CreateCashPosting(posting);

        posting.Applications.Single().PostedEntryId.Should().BeNull();
        posting.Applications.Single().PostedAt.Should().BeNull();
        posting.AppliedAmount.Should().Be(0m);
        posting.UnappliedAmount.Should().Be(500m);
    }
}
