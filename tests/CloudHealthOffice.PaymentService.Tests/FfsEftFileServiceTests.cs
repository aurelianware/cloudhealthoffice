using PaymentService.Models;
using PaymentService.Services;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests;

/// <summary>
/// The NACHA CCD+ credit file of an executed FFS payment run: one credit per
/// payee (TIN + approved account) aggregating its payments, the addenda TRN
/// equal to the 835's TRN, check fallback for payees without an approved EFT
/// account, idempotent regeneration, and nothing recorded when provider-service
/// cannot answer.
/// </summary>
public class FfsEftFileServiceTests
{
    private const string NpiA = "1111111111";
    private const string NpiB = "2222222222";
    private const string NpiC = "3333333333";

    private static FfsRunHarness TwoPartners()
    {
        var h = new FfsRunHarness();
        h.Partner(NpiA, "TP-A");
        h.Partner(NpiC, "TP-A");
        h.Partner(NpiB, "TP-B");
        // A and C bill under one TIN into one account; B has no approved EFT account.
        h.Accounts.Eft(NpiA);
        h.Accounts.Eft(NpiC);
        h.Accounts.NoEft(NpiB, "no approved bank account (a pending change needs approval by a user with payments:approve)");
        return h;
    }

    private static string[] Records(FfsNachaBuiltFile file) => file.Content.TrimEnd('\n').Split('\n');

    [Fact]
    public async Task One_credit_per_payee_aggregating_its_payments_with_the_835_trn_in_the_addenda()
    {
        var h = TwoPartners();
        var run = await h.ExecuteRunAsync(
            FfsRunHarness.Claim("c1", NpiA, 100m),
            FfsRunHarness.Claim("c2", NpiA, 50m),
            FfsRunHarness.Claim("c3", NpiC, 25m),
            FfsRunHarness.Claim("c4", NpiB, 70m));

        var outcome = await h.EftFiles().GenerateAsync(run.Id, "approver-2");
        var file = outcome.Run.EftFile!;

        // A (two claims) and C share TIN, account and 835: one credit of 175.00.
        var entry = Assert.Single(file.Entries);
        Assert.Equal(175m, entry.Amount);
        Assert.Equal(new[] { NpiA, NpiC }, entry.PayeeNpis);
        Assert.Equal(3, entry.ClaimCount);
        Assert.Equal(2, entry.PaymentIds.Count);
        Assert.Equal(("6789", "0021", "3333", "22"), (entry.TaxIdLast4, entry.RoutingNumberLast4, entry.AccountNumberLast4, entry.TransactionCode));
        Assert.Equal((1, 1, 1, 175m, 0m), (file.EntryCount, file.AddendaCount, file.BatchCount, file.TotalCreditAmount, file.TotalDebitAmount));

        // The addenda TRN is the TP-A 835's TRN, segment for segment.
        var tpA = h.Envelopes.Single(e => e.TradingPartnerId == "TP-A");
        var trn = FfsRunHarness.Segments(tpA).Single(s => s[0] == "TRN");
        var addenda = Records(outcome.File!).Single(r => r[0] == '7');
        Assert.Equal(string.Join("*", trn) + "\\", addenda[3..83].TrimEnd());
        Assert.Equal(trn[2], entry.ReassociationTrace);
        Assert.Equal(FfsRunHarness.CompanyId, trn[3]);
        // ...and the credit is that 835's BPR02.
        Assert.Equal(decimal.Parse(FfsRunHarness.Segments(tpA).Single(s => s[0] == "BPR")[2]), entry.Amount);
        Assert.Equal("ACH", FfsRunHarness.Segments(tpA).Single(s => s[0] == "BPR")[4]);

        // The batch company id is TRN03, so the provider can reassociate.
        Assert.Equal(trn[3], Records(outcome.File!)[1][40..50]);
    }

    [Fact]
    public async Task A_payee_without_an_approved_eft_account_is_paid_by_check_and_reported()
    {
        var h = TwoPartners();
        var run = await h.ExecuteRunAsync(FfsRunHarness.Claim("c1", NpiA, 100m), FfsRunHarness.Claim("c4", NpiB, 70m));

        // Decided at execution: the payment and its 835 say check.
        var fallback = Assert.Single(run.CheckFallbacks);
        Assert.Equal((NpiB, 70m, "Execution", false), (fallback.PayeeNpi, fallback.Amount, fallback.DecidedAt, fallback.NeedsAttention));
        Assert.Contains("no approved bank account", fallback.Reason);
        Assert.Equal("CHK", h.Payments.All.Single(p => p.PayeeNPI == NpiB).PaymentMethod);
        Assert.Equal("CHK", FfsRunHarness.Segments(h.Envelopes.Single(e => e.TradingPartnerId == "TP-B")).Single(s => s[0] == "BPR")[4]);
        Assert.Contains(run.Warnings, w => w.Contains("paid by check") && w.Contains(NpiB));

        var outcome = await h.EftFiles().GenerateAsync(run.Id, "approver-2");

        Assert.Equal(new[] { NpiA }, Assert.Single(outcome.Run.EftFile!.Entries).PayeeNpis);
        var reported = Assert.Single(outcome.Run.EftFile.CheckFallbacks);
        Assert.Equal((fallback.PaymentId, fallback.CheckNumber), (reported.PaymentId, reported.CheckNumber));
        Assert.DoesNotContain(Records(outcome.File!), r => r.Contains(fallback.CheckNumber + "*"));
    }

    [Fact]
    public async Task An_account_gone_since_execution_falls_back_to_check_and_needs_attention()
    {
        var h = TwoPartners();
        var run = await h.ExecuteRunAsync(FfsRunHarness.Claim("c1", NpiA, 100m), FfsRunHarness.Claim("c3", NpiC, 30m));
        h.Accounts.NoEft(NpiC, "the approved bank account is not enabled for EFT");

        var outcome = await h.EftFiles().GenerateAsync(run.Id, "approver-2");

        Assert.Equal(100m, Assert.Single(outcome.Run.EftFile!.Entries).Amount);
        var fallback = Assert.Single(outcome.Run.EftFile.CheckFallbacks);
        Assert.Equal((NpiC, "EftFile", true), (fallback.PayeeNpi, fallback.DecidedAt, fallback.NeedsAttention));
        Assert.Contains(outcome.Run.CheckFallbacks, f => f.PaymentId == fallback.PaymentId);
        Assert.Contains(outcome.Run.Warnings, w => w.Contains("already went out as ACH"));
    }

    [Fact]
    public async Task Regenerating_the_file_for_a_run_yields_the_same_file()
    {
        var h = TwoPartners();
        var run = await h.ExecuteRunAsync(FfsRunHarness.Claim("c1", NpiA, 100m), FfsRunHarness.Claim("c3", NpiC, 30m), FfsRunHarness.Claim("c4", NpiB, 70m));

        var first = await h.EftFiles().GenerateAsync(run.Id, "approver-2");
        var second = await h.EftFiles().GenerateAsync(run.Id, "approver-3");

        Assert.False(first.Reproduced);
        Assert.True(second.Reproduced);
        Assert.Equal(first.File!.Content, second.File!.Content);
        Assert.Equal(first.Run.EftFile!.Sha256, second.Run.EftFile!.Sha256);
        Assert.Equal((2, "approver-2", "approver-3"), (second.Run.EftFile.GenerationCount, second.Run.EftFile.FirstGeneratedBy, second.Run.EftFile.LastVerifiedBy));
        var stored = (await h.Runs.GetByIdAsync(run.Id))!;
        Assert.Equal(first.Run.EftFile.Sha256, stored.EftFile!.Sha256);
        Assert.Equal($"FFS-{run.PaymentRunNumber}", stored.EftFile.FileReference);
    }

    [Fact]
    public async Task A_changed_approved_account_after_generation_is_refused_and_the_pinned_file_stands()
    {
        var h = TwoPartners();
        var run = await h.ExecuteRunAsync(FfsRunHarness.Claim("c1", NpiA, 100m));
        var first = await h.EftFiles().GenerateAsync(run.Id, "approver-2");

        h.Accounts.Eft(NpiA, account: "999900001111");
        var ex = await Assert.ThrowsAsync<RunConflictException>(() => h.EftFiles().GenerateAsync(run.Id, "approver-2"));

        Assert.DoesNotContain("999900001111", ex.Message);
        var stored = (await h.Runs.GetByIdAsync(run.Id))!;
        Assert.Equal(first.Run.EftFile!.Sha256, stored.EftFile!.Sha256);
        Assert.Equal(1, stored.EftFile.GenerationCount);
    }

    [Fact]
    public async Task When_provider_service_cannot_answer_no_file_is_recorded()
    {
        var h = TwoPartners();
        var run = await h.ExecuteRunAsync(FfsRunHarness.Claim("c1", NpiA, 100m));
        h.Accounts.Unavailable(NpiA);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.EftFiles().GenerateAsync(run.Id, "approver-2"));

        Assert.Null((await h.Runs.GetByIdAsync(run.Id))!.EftFile);
    }

    [Fact]
    public async Task When_provider_service_cannot_answer_at_execution_nothing_is_paid_and_reservations_are_released()
    {
        var h = TwoPartners();
        h.Accounts.Unavailable(NpiA);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.ExecuteRunAsync(FfsRunHarness.Claim("c1", NpiA, 100m)));

        Assert.Empty(h.Payments.All);
        Assert.Empty(h.Envelopes);
        Assert.Empty(h.Reservations.All);
        Assert.Equal(PaymentRunStatus.Failed, (await h.Runs.GetByIdAsync("run-1"))!.Status);
    }

    [Fact]
    public async Task Payees_with_different_tins_get_separate_credits_in_a_stable_order()
    {
        var h = TwoPartners();
        h.Accounts.Eft(NpiC, routing: "026009593", account: "444455556666", tin: "98-7654321", savings: true);
        var run = await h.ExecuteRunAsync(FfsRunHarness.Claim("c3", NpiC, 30m), FfsRunHarness.Claim("c1", NpiA, 100m));

        var outcome = await h.EftFiles().GenerateAsync(run.Id, "approver-2");

        var entries = outcome.Run.EftFile!.Entries;
        Assert.Equal(2, entries.Count);
        Assert.Equal(new[] { ("6789", 100m, "22"), ("4321", 30m, "32") }, entries.Select(e => (e.TaxIdLast4!, e.Amount, e.TransactionCode)));
        // Same 835 (one trading partner): both credits carry its trace, which is reported.
        Assert.Single(entries.Select(e => e.ReassociationTrace).Distinct());
        Assert.Contains(outcome.Run.Warnings, w => w.Contains("is paid as 2 credits"));
        Assert.Equal(new[] { "091000010000001", "091000010000002" }, entries.Select(e => e.AchTraceNumber));
    }

    [Fact]
    public async Task Only_a_completed_ach_run_has_an_eft_file()
    {
        var h = TwoPartners();
        var pending = await h.Runs.CreateAsync(new PaymentRun { Id = "run-p", TenantId = FfsRunHarness.Tenant, PaymentRunNumber = "PR-P", PaymentMethod = "ACH" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.EftFiles().GenerateAsync(pending.Id, "approver-2"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => h.EftFiles().GenerateAsync("missing", "approver-2"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.EftFiles(new UnconfiguredProviderPayeeAccountSource()).GenerateAsync(pending.Id, "approver-2"));
    }
}
