using CloudHealthOffice.Infrastructure.Edi.Files;
using CloudHealthOffice.Infrastructure.Edi.Interchange;
using CloudHealthOffice.Infrastructure.Tests.Gateways.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace CloudHealthOffice.Infrastructure.Tests.Edi.X12;

/// <summary>Outbound control numbers from the sequence store (edi-operations.md §5).</summary>
public class X12ControlNumberAllocatorTests
{
    private readonly InMemoryX12InterchangeStore _store = new();

    internal static string Isa(string sender, string receiver, string control) =>
        $"ISA*00*          *00*          *ZZ*{sender,-15}*ZZ*{receiver,-15}*261010*0900*^*00501*{control}*0*P*:~";

    /// <summary>One interchange from CHO to <paramref name="receiver"/> with one group of <paramref name="sets"/> 835s, all numbers placeholders.</summary>
    internal static string Era(string receiver = "PROV01", int sets = 1, string gsSender = "CHO")
    {
        var body = string.Concat(Enumerable.Range(1, sets).Select(_ => "ST*835*0001~BPR*I~SE*3*0001~"));
        return Isa("CHO", receiver, "123456789") +
               $"GS*HP*{gsSender}*{receiver}*20261010*0900*1*X*005010X221A1~{body}GE*{sets}*1~IEA*1*123456789~";
    }

    private X12ControlNumberAllocator Allocator() => new(_store);

    [Fact]
    public async Task AssignsSequentialNumbers_PerSenderReceiverPair()
    {
        var a1 = await Allocator().AssignAsync("t1", Era("PROV01"));
        var a2 = await Allocator().AssignAsync("t1", Era("PROV01"));
        var b1 = await Allocator().AssignAsync("t1", Era("PROV02"));

        a1.Interchanges.Single().InterchangeControlNumber.Should().Be("000000001");
        a2.Interchanges.Single().InterchangeControlNumber.Should().Be("000000002");
        b1.Interchanges.Single().InterchangeControlNumber.Should().Be("000000001");
        a2.Content.Should().Contain("*00501*000000002*").And.EndWith("IEA*1*000000002~");
        a2.Content.Should().Contain("*0900*2*X*").And.Contain("GE*1*2~");
        b1.Content.Should().Contain("*0900*1*X*");
    }

    [Fact]
    public async Task KeepsTenantsApart()
    {
        await Allocator().AssignAsync("t1", Era());
        await Allocator().AssignAsync("t1", Era());
        var other = await Allocator().AssignAsync("t2", Era());

        other.Interchanges.Single().InterchangeControlNumber.Should().Be("000000001");
        other.Interchanges.Single().GroupControlNumbers.Should().Equal("1");
    }

    [Fact]
    public async Task WrapsFromTheLargestNineDigitNumberToOne()
    {
        _store.SeedSequence("t1", X12ControlNumberAllocator.InterchangeSequenceKey("ZZ:CHO", "ZZ:PROV01"), 999_999_998);

        var last = await Allocator().AssignAsync("t1", Era());
        var wrapped = await Allocator().AssignAsync("t1", Era());

        last.Interchanges.Single().InterchangeControlNumber.Should().Be("999999999");
        wrapped.Interchanges.Single().InterchangeControlNumber.Should().Be("000000001");
    }

    [Fact]
    public async Task NumbersTransactionSetsWithinTheInterchange_AndGroupsByApplicationPair()
    {
        var content = Isa("CHO", "PROV01", "123456789") +
                      "GS*HP*CHO*PROV01*20261010*0900*1*X*005010X221A1~ST*835*0001~SE*2*0001~ST*835*0001~SE*2*0001~GE*2*1~" +
                      "GS*HP*CHO*PROV01*20261010*0900*1*X*005010X221A1~ST*835*0001~SE*2*0001~GE*1*1~" +
                      "IEA*2*123456789~" +
                      Isa("CHO", "PROV01", "123456789") +
                      "GS*HP*CHO-B*PROV01*20261010*0900*1*X*005010X221A1~ST*835*0001~SE*2*0001~GE*1*1~IEA*1*123456789~";

        var assigned = await Allocator().AssignAsync("t1", content);

        assigned.Interchanges.Should().HaveCount(2);
        assigned.Interchanges[0].Should().BeEquivalentTo(new X12InterchangeNumbers("000000001", ["1", "2"], ["0001", "0002", "0003"]));
        assigned.Interchanges[1].Should().BeEquivalentTo(new X12InterchangeNumbers("000000002", ["1"], ["0001"]));
        X12ControlNumberStamper.Read(assigned.Content).Should().BeEquivalentTo(assigned.Interchanges);
        assigned.Content.Should().Contain("ST*835*0003~SE*2*0003~GE*1*2~IEA*2*000000001~");
    }

    [Fact]
    public async Task LeavesUnreadableContentAlone()
    {
        var assigned = await Allocator().AssignAsync("t1", "not x12");
        assigned.Content.Should().Be("not x12");
        assigned.Interchanges.Should().BeEmpty();
    }

    [Fact]
    public async Task LeavesAnEnvelopeTooShortToCarryItsNumbersAlone_WithoutTakingNumbers()
    {
        const string truncated = "ISA*00*          *00*          *ZZ*SENDER~GS*HP*~ST*835*0001~SE*10*0001~GE*1*1~IEA*1*000000001~";

        var assigned = await Allocator().AssignAsync("t1", truncated);
        var next = await Allocator().AssignAsync("t1", Era());

        assigned.Content.Should().Be(truncated);
        assigned.Interchanges.Should().BeEmpty();
        next.Interchanges.Single().InterchangeControlNumber.Should().Be("000000001");
    }

    [Fact]
    public void Apply_RefusesNumbersThatDoNotFitTheContent()
    {
        X12ControlNumberStamper.Apply(Era(sets: 2), [new X12InterchangeNumbers("000000001", ["1"], ["0001"])]).Should().BeNull();
        X12ControlNumberStamper.Apply(Era(), [new X12InterchangeNumbers("000000001", ["1", "2"], ["0001"])]).Should().BeNull();
        X12ControlNumberStamper.Apply(Era(), []).Should().BeNull();
    }

    [Fact]
    public async Task AssignAndRecord_RecordsTheOutboundInterchangeAndFile()
    {
        var files = new InMemoryEdiFileStore();
        var tracker = new OutboundInterchangeTracker(_store, NullLogger<OutboundInterchangeTracker>.Instance,
            files: new EdiFileRecorder(files, NullLogger<EdiFileRecorder>.Instance),
            allocator: Allocator());

        var sent = await tracker.AssignAndRecordAsync("t1", Era(sets: 2), "835", "payment:p1");

        X12ControlNumberStamper.Read(sent).Single().Should().BeEquivalentTo(
            new X12InterchangeNumbers("000000001", ["1"], ["0001", "0002"]));
        var record = (await _store.ListOutboundAsync("t1", null, 10)).Single();
        record.ControlNumber.Should().Be("000000001");
        record.GroupControlNumbers.Should().Equal("1");
        record.TransactionSetControlNumbers.Should().Equal("0001", "0002");

        var file = (await files.ListAsync("t1", new EdiFileQuery())).Single();
        file.Direction.Should().Be(EdiFileDirection.Outbound);
        file.Source.Should().Be(EdiFileSource.Generated);
        file.Status.Should().Be(EdiFileStatus.Sent);
        file.SourceReference.Should().Be("payment:p1");
        file.InterchangeControlNumbers.Should().Equal("000000001");
        file.TransactionSetControlNumbers.Should().Equal("0001", "0002");
        record.EdiFileId.Should().Be(file.Id);
        file.OutboundInterchangeIds.Should().Equal(record.Id);
        (await files.ListAsync("t2", new EdiFileQuery())).Should().BeEmpty();
    }
}

/// <summary>The allocator over real MongoDB: one atomic sequence per tenant and pair, safe under concurrency.</summary>
public sealed class MongoX12ControlNumberAllocatorTests(MongoClaimTransmissionTenantTests.Mongod mongo)
    : IClassFixture<MongoClaimTransmissionTenantTests.Mongod>, IAsyncLifetime
{
    private IMongoDatabase _db = null!;

    public Task InitializeAsync()
    {
        _db = mongo.Client.GetDatabase($"x12_ctl_{Guid.NewGuid():N}");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.Client.DropDatabaseAsync(_db.DatabaseNamespace.DatabaseName);

    [Fact]
    public async Task ConcurrentAssignments_NeverShareANumber()
    {
        var allocator = new X12ControlNumberAllocator(new MongoX12InterchangeStore(_db));

        var results = await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => allocator.AssignAsync("t1", X12ControlNumberAllocatorTests.Era()))));

        results.Select(r => r.Interchanges.Single().InterchangeControlNumber).Should().OnlyHaveUniqueItems()
            .And.BeEquivalentTo(Enumerable.Range(1, 50).Select(n => n.ToString("D9")));
        results.Select(r => r.Interchanges.Single().GroupControlNumbers.Single()).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task SequencesAreSeparatePerPairAndTenant_AndWrap()
    {
        var store = new MongoX12InterchangeStore(_db);
        var allocator = new X12ControlNumberAllocator(store);

        (await allocator.AssignAsync("t1", X12ControlNumberAllocatorTests.Era("PROV01"))).Interchanges[0].InterchangeControlNumber.Should().Be("000000001");
        (await allocator.AssignAsync("t1", X12ControlNumberAllocatorTests.Era("PROV01"))).Interchanges[0].InterchangeControlNumber.Should().Be("000000002");
        (await allocator.AssignAsync("t1", X12ControlNumberAllocatorTests.Era("PROV02"))).Interchanges[0].InterchangeControlNumber.Should().Be("000000001");
        (await allocator.AssignAsync("t2", X12ControlNumberAllocatorTests.Era("PROV01"))).Interchanges[0].InterchangeControlNumber.Should().Be("000000001");

        var key = X12ControlNumberAllocator.InterchangeSequenceKey("ZZ:CHO", "ZZ:PROV03");
        await store.NextControlNumberAsync("t1", key);
        await _db.GetCollection<MongoDB.Bson.BsonDocument>("x12-control-sequences").UpdateOneAsync(
            MongoDB.Bson.BsonDocument.Parse($"{{ _id: 't1|{key}' }}"),
            MongoDB.Bson.BsonDocument.Parse("{ $set: { Value: NumberLong(999999999) } }"));
        (await allocator.AssignAsync("t1", X12ControlNumberAllocatorTests.Era("PROV03"))).Interchanges[0].InterchangeControlNumber.Should().Be("000000001");
    }
}
