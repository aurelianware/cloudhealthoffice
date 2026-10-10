using PaymentService.Models;
using PaymentService.Services;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests;

/// <summary>Which reversed payments may not have been paid yet (their receivable is then held).</summary>
public class UnsentAchPaymentsTests
{
    private readonly InMemoryPaymentRunRepository _runs = new(FfsRunHarness.Tenant);
    private readonly InMemoryPaymentFileTransmissionRepository _store = new();

    private async Task<Payment> PaymentInRunAsync(PaymentRunEftFile? file, string method = "ACH")
    {
        var run = new PaymentRun { Id = "run-u", TenantId = FfsRunHarness.Tenant, PaymentRunNumber = "PR-U", PaymentMethod = "ACH",
            Status = PaymentRunStatus.Completed, EftFile = file };
        await _runs.CreateAsync(run);
        return new Payment { Id = "p-u", RunId = run.Id, PaymentMethod = method, TotalPaymentAmount = 10m };
    }

    private static PaymentRunEftFile File(params string[] paymentIds) => new()
    {
        FileReference = "FFS-PR-U", Sha256 = new string('a', 64), Entries = { new PaymentRunEftEntry { PaymentIds = paymentIds.ToList(), Amount = 10m } },
    };

    [Fact]
    public async Task A_payment_in_a_file_not_yet_transmitted_or_with_no_file_is_unsent()
    {
        var unsent = new UnsentAchPayments(_runs, _store);
        Assert.True(await unsent.IsUnsentAsync(await PaymentInRunAsync(null)));
    }

    [Fact]
    public async Task Transmitted_check_and_left_out_payments_are_not_held()
    {
        var unsent = new UnsentAchPayments(_runs, _store);
        var payment = await PaymentInRunAsync(File("p-u"));
        Assert.True(await unsent.IsUnsentAsync(payment));

        await _store.TryInsertAsync(new PaymentFileTransmission { TenantId = FfsRunHarness.Tenant, FileReference = "FFS-PR-U", Status = PaymentFileTransmissionStatus.Transmitted });
        Assert.False(await unsent.IsUnsentAsync(payment));
        Assert.False(await unsent.IsUnsentAsync(new Payment { Id = "p-c", RunId = "run-u", PaymentMethod = "CHK" }));
        Assert.False(await unsent.IsUnsentAsync(new Payment { Id = "p-other", RunId = "run-u", PaymentMethod = "ACH" })); // check fallback: not in the file
    }
}
