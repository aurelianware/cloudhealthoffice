using CloudHealthOffice.Infrastructure.Edi.Interchange;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using NSubstitute;
using PaymentService.Models;
using PaymentService.Repositories;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests.Integration;

/// <summary>
/// The shared X12 interchange-control store against a real mongod: the
/// atomic duplicate-ISA13 check (with its window and tenant/sender scoping),
/// TA1 storage, and an 835 envelope tracked as an outbound interchange then
/// rejected by the payee's TA1.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class X12InterchangeMongoStoreTests : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
    private readonly MongoRunnerFixture _mongo;
    private readonly IMongoDatabase _db;
    private readonly MongoX12InterchangeStore _store;

    public X12InterchangeMongoStoreTests(MongoRunnerFixture mongo)
    {
        _mongo = mongo;
        _db = mongo.CreateDatabase("x12_interchange");
        _store = new MongoX12InterchangeStore(_db);
    }

    public Task InitializeAsync() => _store.EnsureIndexesAsync();
    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_db);

    [Fact]
    public async Task Receipt_IsRefusedInsideTheWindow_AndAllowedAfterIt()
    {
        var window = TimeSpan.FromDays(30);
        Assert.True(await _store.TryRegisterReceiptAsync("t1", "ZZ:SUB", "000000101", T0, window));
        Assert.False(await _store.TryRegisterReceiptAsync("t1", "ZZ:SUB", "000000101", T0.AddDays(29), window));
        Assert.True(await _store.TryRegisterReceiptAsync("t1", "ZZ:SUB", "000000101", T0.AddDays(31), window));
        // Refreshed: the window now runs from day 31.
        Assert.False(await _store.TryRegisterReceiptAsync("t1", "ZZ:SUB", "000000101", T0.AddDays(40), window));
    }

    [Fact]
    public async Task Receipt_IsScopedToTenantAndSender()
    {
        var window = TimeSpan.FromDays(30);
        Assert.True(await _store.TryRegisterReceiptAsync("t1", "ZZ:SUB", "000000101", T0, window));
        Assert.True(await _store.TryRegisterReceiptAsync("t2", "ZZ:SUB", "000000101", T0, window));
        Assert.True(await _store.TryRegisterReceiptAsync("t1", "ZZ:OTHER", "000000101", T0, window));
    }

    [Fact]
    public async Task ConcurrentReceipts_OnlyOneWins()
    {
        var window = TimeSpan.FromDays(30);
        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => _store.TryRegisterReceiptAsync("t1", "ZZ:SUB", "000000777", T0, window)));
        Assert.Single(results, r => r);
    }

    [Fact]
    public async Task Acknowledgments_AreStoredListedAndTenantScoped()
    {
        var record = new InterchangeAcknowledgmentRecord
        {
            TenantId = "t1", AcknowledgedControlNumber = "000000101", AckCode = "R", NoteCode = "001",
            SenderId = "SUB", Ta1Content = "ISA*...~TA1*000000101*260115*1200*R*001~IEA*0*000000042~", CreatedAt = T0,
        };
        await _store.SaveAcknowledgmentAsync(record);

        Assert.Equal("001", (await _store.GetAcknowledgmentAsync("t1", record.Id))!.NoteCode);
        Assert.Null(await _store.GetAcknowledgmentAsync("t2", record.Id));
        Assert.Single(await _store.ListAcknowledgmentsAsync("t1", new InterchangeAcknowledgmentQuery { AckCode = "R" }));
        Assert.Empty(await _store.ListAcknowledgmentsAsync("t1", new InterchangeAcknowledgmentQuery { AckCode = "A" }));
    }

    [Fact]
    public async Task EraEnvelope_IsTrackedOutbound_AndThePayeesRejectingTa1IsRecordedAgainstIt()
    {
        const string era =
            "ISA*00*          *00*          *ZZ*CHO            *ZZ*PAYEE01        *261010*0900*^*00501*000000555*1*P*:~" +
            "GS*HP*CHO*PAYEE01*20261010*0900*555*X*005010X221A1~ST*835*0001~SE*2*0001~GE*1*555~IEA*1*000000555~";
        var inner = Substitute.For<IEraEnvelopeRepository>();
        inner.CreateAsync(Arg.Any<EraEnvelopeRecord>()).Returns(ci => ci.Arg<EraEnvelopeRecord>());
        var tracker = new OutboundInterchangeTracker(_store, NullLogger<OutboundInterchangeTracker>.Instance);
        var repo = new TrackingEraEnvelopeRepository(inner, tracker);

        var envelope = await repo.CreateAsync(new EraEnvelopeRecord { TenantId = "t1", TradingPartnerId = "tp-1", EdiContent = era });

        var pending = Assert.Single(await _store.ListOutboundAsync("t1", OutboundAckStatus.Pending, 10));
        Assert.Equal("835", pending.TransactionType);
        Assert.Equal(envelope.Id, pending.SourceReference);

        const string ta1 =
            "ISA*00*          *00*          *ZZ*PAYEE01        *ZZ*CHO            *261010*1000*^*00501*000000901*0*P*:~" +
            "TA1*000000555*261010*0900*R*022~IEA*0*000000901~";
        var result = await tracker.ProcessInboundTa1Async("t1", ta1, "ta1.edi");

        Assert.Equal(Ta1MatchStatus.Matched, Assert.Single(result.Results).Status);
        var rejected = Assert.Single(await _store.ListOutboundAsync("t1", OutboundAckStatus.Rejected, 10));
        Assert.Equal("022", rejected.AckNoteCode);
        Assert.Empty(await _store.ListOutboundAsync("t1", OutboundAckStatus.Pending, 10));
        Assert.Single(await _store.ListAcknowledgmentsAsync("t1", new InterchangeAcknowledgmentQuery { Direction = Ta1Direction.Received }));
    }
}
