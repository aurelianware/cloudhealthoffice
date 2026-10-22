using CloudHealthOffice.Infrastructure.Edi.Interchange;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static CloudHealthOffice.Infrastructure.Tests.Edi.X12.X12Samples;

namespace CloudHealthOffice.Infrastructure.Tests.Edi.X12;

/// <summary>
/// Review follow-ups: duplicate check per path (off for real-time 270),
/// releasing a claimed ISA13 after a downstream failure, receiver in the
/// duplicate key, test interchanges, and the TA1's own ISA13 sequence.
/// </summary>
public class X12InterchangeControlTests
{
    private readonly InMemoryX12InterchangeStore _store = new();
    private readonly SettableClock _clock = new(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));

    private X12InterchangeIntake Intake(X12InterchangeOptions? options = null) =>
        new(_store, Options.Create(options ?? new X12InterchangeOptions()), NullLogger<X12InterchangeIntake>.Instance, _clock);

    private static InterchangeIntakeRequest Request(string content, string type = "837", bool realTime = false) =>
        new() { TenantId = "t1", Content = content, TransactionType = type, RealTime = realTime };

    [Fact]
    public async Task RealTime270_IsNotCheckedForDuplicates_AndWritesNoReceipt()
    {
        var intake = Intake();
        var first = await intake.ReceiveAsync(Request(Interchange(), "270", realTime: true));
        var retry = await intake.ReceiveAsync(Request(Interchange(), "270", realTime: true));

        first.IsRejected.Should().BeFalse();
        retry.IsRejected.Should().BeFalse();
        first.Interchanges.Single().ReceiptClaimedAt.Should().BeNull();
        // Nothing was stored: the same ISA13 can still be claimed.
        (await _store.TryRegisterReceiptAsync("t1", "ZZ:SUBMITTER01|ZZ:CHO", "000000101", _clock.Now.UtcDateTime, TimeSpan.FromDays(1))).Should().BeTrue();
    }

    [Fact]
    public async Task Batch270_KeepsTheDuplicateCheck()
    {
        var intake = Intake();
        await intake.ReceiveAsync(Request(Interchange(), "270"));
        (await intake.ReceiveAsync(Request(Interchange(), "270"))).Ta1.Should().Contain("*R*025~");
    }

    [Fact]
    public async Task DuplicateCheck_IsConfigurablePerPath()
    {
        var options = new X12InterchangeOptions();
        options.DuplicateCheck["837"] = false;
        options.DuplicateCheck["270-realtime"] = true;
        var intake = Intake(options);

        await intake.ReceiveAsync(Request(Interchange()));
        (await intake.ReceiveAsync(Request(Interchange()))).IsRejected.Should().BeFalse();

        await intake.ReceiveAsync(Request(Interchange(f => f[12] = "000000555", iea: "IEA*1*000000555"), "270", realTime: true));
        (await intake.ReceiveAsync(Request(Interchange(f => f[12] = "000000555", iea: "IEA*1*000000555"), "270", realTime: true)))
            .Ta1.Should().Contain("*R*025~");
    }

    [Fact]
    public async Task Release_AfterADownstreamFailure_LetsTheResendThrough()
    {
        var intake = Intake();
        var request = Request(Interchange());
        var failed = await intake.ReceiveAsync(request);
        failed.Interchanges.Single().ReceiptClaimedAt.Should().NotBeNull();

        await intake.ReleaseAsync(request, failed);

        (await intake.ReceiveAsync(Request(Interchange()))).IsRejected.Should().BeFalse();
        // ...and that resend now holds the number.
        (await intake.ReceiveAsync(Request(Interchange()))).Ta1.Should().Contain("*R*025~");
    }

    [Fact]
    public async Task Release_LeavesAReceiptClaimedByAnotherRequestAlone()
    {
        var intake = Intake();
        var request = Request(Interchange());
        var stale = await intake.ReceiveAsync(request);
        await intake.ReleaseAsync(request, stale);

        _clock.Now = _clock.Now.AddMinutes(1);
        (await intake.ReceiveAsync(Request(Interchange()))).IsRejected.Should().BeFalse();

        // Releasing the old result again must not free the newer claim.
        await intake.ReleaseAsync(request, stale);
        (await intake.ReceiveAsync(Request(Interchange()))).Ta1.Should().Contain("*R*025~");
    }

    [Fact]
    public async Task SameControlNumber_ToAnotherReceiver_IsNotADuplicate()
    {
        var intake = Intake();
        await intake.ReceiveAsync(Request(Interchange()));

        var otherReceiver = await intake.ReceiveAsync(Request(Interchange(f => f[7] = Field("CHO2", 15))));
        var sameReceiver = await intake.ReceiveAsync(Request(Interchange()));

        otherReceiver.IsRejected.Should().BeFalse();
        sameReceiver.Ta1.Should().Contain("*R*025~");
    }

    [Theory]
    [InlineData(TestInterchangeHandling.Reject, "R", "020")]
    [InlineData(TestInterchangeHandling.Accept, "A", "000")]
    public async Task TestInterchanges_FollowTheConfiguredHandling(TestInterchangeHandling handling, string ackCode, string note)
    {
        var result = await Intake(new X12InterchangeOptions { TestInterchangeHandling = handling }).ReceiveAsync(Request(Interchange()));

        result.Interchanges.Single().Decision.Should().Be(new Ta1Decision(ackCode, note));
        result.IsRejected.Should().Be(handling == TestInterchangeHandling.Reject);
    }

    [Fact]
    public async Task ProductionInterchange_IsAcceptedWhenTestsAreRejected()
    {
        var result = await Intake(new X12InterchangeOptions { TestInterchangeHandling = TestInterchangeHandling.Reject })
            .ReceiveAsync(Request(Interchange(f => f[14] = "P")));
        result.IsRejected.Should().BeFalse();
    }

    [Fact]
    public async Task Ta1ControlNumbers_AreASequencePerPair()
    {
        var intake = Intake();
        string Control(InterchangeIntakeResult r) => r.Interchanges.Single().Ta1ControlNumber!;

        var a1 = await intake.ReceiveAsync(Request(Interchange(f => { f[12] = "000000001"; f[13] = "1"; }, iea: "IEA*1*000000001")));
        var a2 = await intake.ReceiveAsync(Request(Interchange(f => { f[12] = "000000002"; f[13] = "1"; }, iea: "IEA*1*000000002")));
        var b1 = await intake.ReceiveAsync(Request(Interchange(f => { f[5] = Field("SUBMITTER02", 15); f[13] = "1"; })));

        Control(a1).Should().Be("000000001");
        Control(a2).Should().Be("000000002");
        Control(b1).Should().Be("000000001");
        a2.Ta1.Should().Contain("*000000002*0*T*:~").And.EndWith("IEA*0*000000002~");
    }

    [Fact]
    public async Task Ta1ControlNumber_WrapsAfter999999999()
    {
        _store.SeedSequence("t1", "ZZ:CHO>ZZ:SUBMITTER01", 999_999_999);
        var result = await Intake().ReceiveAsync(Request(Interchange(f => f[13] = "1")));
        result.Interchanges.Single().Ta1ControlNumber.Should().Be("000000001");
        X12ControlNumberRange.Wrap(999_999_999).Should().Be(999_999_999);
        X12ControlNumberRange.Wrap(2_000_000_000).Should().Be(2);
    }

    [Fact]
    public void Preview_TakesNoNumberFromTheSequence()
    {
        Intake().Preview(Interchange(f => f[13] = "1")).Interchanges.Single().Ta1.Should().Contain("*000000000*0*T*:~");
    }

    [Fact]
    public async Task RepeatedDownload_ReusesTheRecordedControlNumber()
    {
        var tracker = new OutboundInterchangeTracker(_store, NullLogger<OutboundInterchangeTracker>.Instance);
        string Ca(string control) =>
            $"ISA*00*          *00*          *ZZ*CHO            *ZZ*SUBMITTER01    *261010*0900*^*00501*{control}*0*P*:~" +
            $"GS*HN*CHO*SUBMITTER01*20261010*0900*1*X*005010X214~ST*277*0001~SE*2*0001~GE*1*1~IEA*1*{control}~";

        var first = await tracker.GetOrRecordSentAsync("t1", Ca("111111111"), "277CA", "claim-1:Approved");
        var second = await tracker.GetOrRecordSentAsync("t1", Ca("222222222"), "277CA", "claim-1:Approved");
        var otherStatus = await tracker.GetOrRecordSentAsync("t1", Ca("333333333"), "277CA", "claim-1:Paid");

        // The builder's numbers are placeholders: the first download gets the
        // first number of the CHO → SUBMITTER01 sequence, a repeat reuses it,
        // another claim status is another interchange with the next number.
        first.Should().Be(Ca("000000001"));
        second.Should().Be(Ca("000000001"));
        otherStatus.Should().Contain("*000000002*").And.Contain("IEA*1*000000002~");
        (await _store.ListOutboundAsync("t1", null, 10)).Should().HaveCount(2);
    }

    [Fact]
    public async Task RepeatedDownload_OfARecordFromBeforeGroupNumbersWereKept_ReusesItsIsa13()
    {
        await _store.SaveOutboundAsync(new OutboundInterchangeRecord
        {
            TenantId = "t1", TransactionType = "277CA", ControlNumber = "000000777",
            SenderId = "CHO", ReceiverId = "SUBMITTER01", SourceReference = "claim-9:Approved",
        });
        var tracker = new OutboundInterchangeTracker(_store, NullLogger<OutboundInterchangeTracker>.Instance);
        const string ca =
            "ISA*00*          *00*          *ZZ*CHO            *ZZ*SUBMITTER01    *261010*0900*^*00501*123456789*0*P*:~" +
            "GS*HN*CHO*SUBMITTER01*20261010*0900*1*X*005010X214~ST*277*0001~SE*2*0001~GE*1*1~IEA*1*123456789~";

        var again = await tracker.GetOrRecordSentAsync("t1", ca, "277CA", "claim-9:Approved");

        again.Should().Contain("*000000777*0*P*").And.Contain("IEA*1*000000777~");
    }
}
