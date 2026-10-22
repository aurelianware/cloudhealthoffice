using ArService.Gl;
using CloudHealthOffice.Finance.Contracts;

namespace ArService.Tests.Gl;

public class GlBankSettlementTests
{
    private readonly InMemoryGl _gl = new();
    private readonly GlClock _clock = new();
    private readonly GlPostingOptions _options = GlFixtures.Options();
    public GlBankSettlementTests() => GlFixtures.SeedChart(_gl);
    private GlPostingService Service => GlFixtures.Service(_gl, _options, _clock);

    private static GlEventEnvelope Settled(GlEventEnvelope transmitted, decimal amount = 100m,
        string run = "run-1", string file = "FFS-PR-run-1", string key = "settlement:file-1") =>
        GlFixtures.Envelope(GlEventTypes.PaymentFileSettled, key, new PaymentFileSettledEvent
        {
            TenantId = GlFixtures.Tenant, PaymentRunId = run, FileReference = file,
            TransmissionEventId = transmitted.EventId, BankTransactionId = "bank-line-1", Amount = amount,
            BankAsOfDate = new DateTime(2026, 5, 22), PaymentRunExecutedAt = new DateTime(2026, 5, 20), SettledBy = "matched",
        });

    [Fact]
    public async Task Bank_settlement_balances_cash_and_clears_transit_once_despite_redelivery()
    {
        var transmitted = GlFixtures.FileTransmitted();
        await Service.IngestAsync(GlFixtures.RunExecuted(), GlFixtures.Tenant);
        await Service.IngestAsync(transmitted, GlFixtures.Tenant);
        var settled = Settled(transmitted);
        (await Service.IngestAsync(settled, GlFixtures.Tenant)).Status.Should().Be(GlSourceEventStatus.Posted);
        (await Service.IngestAsync(settled, GlFixtures.Tenant)).Duplicate.Should().BeTrue();
        var entry = _gl.Entries.Single(e => e.Kind == GlEntryKind.AchSettlement);
        entry.TotalDebit.Should().Be(100m);
        entry.TotalCredit.Should().Be(100m);
        entry.EntryDate.Should().Be(new DateTime(2026, 5, 22));
        entry.Lines.Single(l => l.Role == GlPostingRole.Cash).Credit.Should().Be(100m);
        _gl.Entries.SelectMany(e => e.Lines).Where(l => l.Role == GlPostingRole.AchInTransit)
            .Sum(l => l.Debit - l.Credit).Should().Be(0m);
    }

    [Fact]
    public async Task Out_of_order_settlement_waits_for_the_exact_transmission_and_is_released_automatically()
    {
        var transmitted = GlFixtures.FileTransmitted();
        var result = await Service.IngestAsync(Settled(transmitted), GlFixtures.Tenant);
        result.ParkReason.Should().Be(GlParkReason.AwaitingSourceEvent);
        _gl.Entries.Should().BeEmpty();
        await Service.IngestAsync(GlFixtures.RunExecuted(), GlFixtures.Tenant);
        _gl.Entries.Should().NotContain(e => e.Kind == GlEntryKind.AchSettlement);
        await Service.IngestAsync(transmitted, GlFixtures.Tenant);
        _gl.Entries.Count(e => e.Kind == GlEntryKind.AchSettlement).Should().Be(1);
    }

    [Theory]
    [InlineData(99, "run-1", "FFS-PR-run-1")]
    [InlineData(100, "other-run", "FFS-PR-run-1")]
    [InlineData(100, "run-1", "other-file")]
    public async Task A_posted_transmission_for_another_amount_run_or_file_cannot_justify_cash(decimal amount, string run, string file)
    {
        var transmitted = GlFixtures.FileTransmitted();
        await Service.IngestAsync(GlFixtures.RunExecuted(), GlFixtures.Tenant);
        await Service.IngestAsync(transmitted, GlFixtures.Tenant);
        var result = await Service.IngestAsync(Settled(transmitted, amount, run, file), GlFixtures.Tenant);
        result.ParkReason.Should().Be(GlParkReason.InvalidPayload);
        _gl.Entries.Should().NotContain(e => e.Kind == GlEntryKind.AchSettlement);
    }

    [Fact]
    public async Task Aliased_cash_and_transit_accounts_park_instead_of_manufacturing_a_cash_movement()
    {
        var transmitted = GlFixtures.FileTransmitted();
        await Service.IngestAsync(GlFixtures.RunExecuted(), GlFixtures.Tenant);
        await Service.IngestAsync(transmitted, GlFixtures.Tenant);
        _options.Tenants[GlFixtures.Tenant].Accounts[nameof(GlPostingRole.Cash)] =
            _options.Tenants[GlFixtures.Tenant].Accounts[nameof(GlPostingRole.AchInTransit)];
        (await Service.IngestAsync(Settled(transmitted), GlFixtures.Tenant)).ParkReason.Should().Be(GlParkReason.NeedsMapping);
    }

    [Fact]
    public async Task Reversed_transmission_cannot_justify_settlement()
    {
        var transmitted = GlFixtures.FileTransmitted();
        await Service.IngestAsync(GlFixtures.RunExecuted(), GlFixtures.Tenant);
        var posted = await Service.IngestAsync(transmitted, GlFixtures.Tenant);
        await Service.ReverseAsync(GlFixtures.Tenant, posted.EntryId!, "bank transmission corrected", null, "finance");
        (await Service.IngestAsync(Settled(transmitted), GlFixtures.Tenant)).ParkReason.Should().Be(GlParkReason.InvalidPayload);
        _gl.Entries.Should().NotContain(e => e.Kind == GlEntryKind.AchSettlement);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(0.001)]
    public void Nonpositive_or_fractional_cent_cash_is_rejected(decimal amount)
    {
        var act = () => GlEntryBuilder.Build(Settled(GlFixtures.FileTransmitted(), amount));
        act.Should().Throw<GlInvalidPayloadException>();
    }
}
