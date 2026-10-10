using CloudHealthOffice.Infrastructure.Edi.Interchange;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static CloudHealthOffice.Infrastructure.Tests.Edi.X12.X12Samples;

namespace CloudHealthOffice.Infrastructure.Tests.Edi.X12;

internal sealed class SettableClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

public class X12InterchangeIntakeTests
{
    private readonly InMemoryX12InterchangeStore _store = new();
    private readonly SettableClock _clock = new(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));

    private X12InterchangeIntake Intake(X12InterchangeOptions? options = null) =>
        new(_store, Options.Create(options ?? new X12InterchangeOptions()), NullLogger<X12InterchangeIntake>.Instance, _clock);

    private static InterchangeIntakeRequest Request(string content, string tenant = "t1") =>
        new() { TenantId = tenant, Content = content, TransactionType = "837", FileName = "f.837" };

    [Fact]
    public async Task ValidEnvelope_NoAckRequested_ProducesNoTa1()
    {
        var content = Interchange();

        var result = await Intake().ReceiveAsync(Request(content));

        result.IsRejected.Should().BeFalse();
        result.Ta1.Should().BeNull();
        result.AcknowledgmentIds.Should().BeEmpty();
        result.AcceptedContent.Should().Be(content);
        (await _store.ListAcknowledgmentsAsync("t1", new InterchangeAcknowledgmentQuery())).Should().BeEmpty();
    }

    [Fact]
    public async Task ValidEnvelope_AckRequested_ProducesAndStoresAcceptedTa1()
    {
        var result = await Intake().ReceiveAsync(Request(Interchange(f => f[13] = "1")));

        result.IsRejected.Should().BeFalse();
        result.Ta1.Should().Contain("TA1*000000101*260115*1200*A*000~");
        var stored = await _store.GetAcknowledgmentAsync("t1", result.AcknowledgmentIds.Single());
        stored!.AckCode.Should().Be("A");
        stored.Ta1Content.Should().Be(result.Ta1);
        stored.SenderId.Should().Be("SUBMITTER01");
        stored.TransactionType.Should().Be("837");
        (await _store.GetAcknowledgmentAsync("other-tenant", stored.Id)).Should().BeNull();
    }

    [Fact]
    public async Task RejectedEnvelope_ProducesTa1EvenWithoutAckRequested_AndNoContentForTheParser()
    {
        var result = await Intake().ReceiveAsync(Request(Interchange(iea: "IEA*1*000000999")));

        result.IsRejected.Should().BeTrue();
        result.AcceptedContent.Should().BeNull();
        result.Ta1.Should().Contain("*R*001~");
        result.RejectionReasons.Single().Should().Contain("TA105 001");
        result.AcknowledgmentIds.Should().ContainSingle();
    }

    [Fact]
    public async Task DuplicateControlNumber_FromTheSameSender_IsRejected025()
    {
        var intake = Intake();
        (await intake.ReceiveAsync(Request(Interchange()))).IsRejected.Should().BeFalse();

        var again = await intake.ReceiveAsync(Request(Interchange()));

        again.IsRejected.Should().BeTrue();
        again.Interchanges.Single().IsDuplicate.Should().BeTrue();
        again.Ta1.Should().Contain("TA1*000000101*260115*1200*R*025~");
    }

    [Fact]
    public async Task SameControlNumber_FromAnotherSenderOrTenant_IsNotADuplicate()
    {
        var intake = Intake();
        await intake.ReceiveAsync(Request(Interchange()));

        (await intake.ReceiveAsync(Request(Interchange(f => f[5] = Field("SUBMITTER02", 15))))).IsRejected.Should().BeFalse();
        (await intake.ReceiveAsync(Request(Interchange(), tenant: "t2"))).IsRejected.Should().BeFalse();
    }

    [Fact]
    public async Task DuplicateWindow_Expires()
    {
        var intake = Intake(new X12InterchangeOptions { DuplicateWindowDays = 30 });
        await intake.ReceiveAsync(Request(Interchange()));

        _clock.Now = _clock.Now.AddDays(31);

        (await intake.ReceiveAsync(Request(Interchange()))).IsRejected.Should().BeFalse();
    }

    [Fact]
    public async Task RejectedEnvelope_DoesNotClaimItsControlNumber()
    {
        var intake = Intake();
        await intake.ReceiveAsync(Request(Interchange(iea: "IEA*1*000000999")));

        // The corrected file may reuse ISA13.
        (await intake.ReceiveAsync(Request(Interchange()))).IsRejected.Should().BeFalse();
    }

    [Fact]
    public async Task DuplicateCheckOff_WhenWindowIsZero()
    {
        var intake = Intake(new X12InterchangeOptions { DuplicateWindowDays = 0 });
        await intake.ReceiveAsync(Request(Interchange()));
        (await intake.ReceiveAsync(Request(Interchange()))).IsRejected.Should().BeFalse();
    }

    [Fact]
    public async Task NotedCode_IsAcceptedWithErrors_ContentStillFlows()
    {
        var content = Interchange(iea: "IEA*3*000000101");
        var result = await Intake(new X12InterchangeOptions { NotedNoteCodes = ["021"] }).ReceiveAsync(Request(content));

        result.IsRejected.Should().BeFalse();
        result.AcceptedContent.Should().Be(content);
        result.Ta1.Should().Contain("*E*021~");
    }

    [Fact]
    public async Task MixedFile_OnlyAcceptedInterchangesReachTheParser()
    {
        var good = Interchange(f => f[12] = "000000201", iea: "IEA*1*000000201");
        var bad = Interchange(f => f[12] = "000000202", iea: "IEA*1*000000999");

        var result = await Intake().ReceiveAsync(Request(good + "\n" + bad));

        result.IsRejected.Should().BeFalse();
        result.AcceptedContent.Should().Be(good);
        result.Interchanges.Select(i => i.Decision.AckCode).Should().Equal("A", "R");
        result.Ta1.Should().Contain("TA1*000000202").And.NotContain("TA1*000000201");
    }

    [Fact]
    public async Task UnreadableFile_IsRejectedWithoutTa1()
    {
        var result = await Intake().ReceiveAsync(Request("hello"));
        result.IsRejected.Should().BeTrue();
        result.Ta1.Should().BeNull();
        result.RejectionReasons.Should().ContainSingle();
    }

    [Fact]
    public async Task Preview_StoresNothing_AndRegistersNothing()
    {
        var intake = Intake();
        intake.Preview(Interchange(f => f[13] = "1")).Ta1.Should().NotBeNull();

        (await _store.ListAcknowledgmentsAsync("t1", new InterchangeAcknowledgmentQuery())).Should().BeEmpty();
        (await intake.ReceiveAsync(Request(Interchange()))).IsRejected.Should().BeFalse();
    }
}

public class OutboundInterchangeTrackerTests
{
    private readonly InMemoryX12InterchangeStore _store = new();
    private OutboundInterchangeTracker Tracker() => new(_store, NullLogger<OutboundInterchangeTracker>.Instance);

    private const string Era835 =
        "ISA*00*          *00*          *ZZ*CHO            *ZZ*PAYEE01        *261010*0900*^*00501*000000555*1*P*:~" +
        "GS*HP*CHO*PAYEE01*20261010*0900*555*X*005010X221A1~ST*835*0001~SE*2*0001~GE*1*555~IEA*1*000000555~";

    private static string PartnerTa1(string ackCode, string note, string partner = "PAYEE01") =>
        $"ISA*00*          *00*          *ZZ*{Field(partner, 15)}*ZZ*CHO            *261010*1000*^*00501*000000901*0*P*:~" +
        $"TA1*000000555*261010*0900*{ackCode}*{note}~IEA*0*000000901~";

    [Fact]
    public async Task RecordSent_KeepsEnvelopeOnly()
    {
        var record = await Tracker().RecordSentAsync("t1", Era835, null, "env-1");

        record!.TransactionType.Should().Be("835");
        record.ControlNumber.Should().Be("000000555");
        record.ReceiverId.Should().Be("PAYEE01");
        record.AckRequested.Should().BeTrue();
        record.AckStatus.Should().Be(OutboundAckStatus.Pending);
    }

    [Fact]
    public async Task RecordSent_NeverThrows_OnContentThatIsNotX12()
    {
        (await Tracker().RecordSentAsync("t1", "garbage", "835", null)).Should().BeNull();
    }

    [Fact]
    public async Task RejectingTa1_MarksTheOutboundInterchangeRejected()
    {
        var tracker = Tracker();
        var sent = await tracker.RecordSentAsync("t1", Era835, "835", "env-1");

        var result = await tracker.ProcessInboundTa1Async("t1", PartnerTa1("R", "006"), "ta1.edi");

        var match = result.Results.Single();
        match.Status.Should().Be(Ta1MatchStatus.Matched);
        match.OutboundInterchangeId.Should().Be(sent!.Id);
        match.SourceReference.Should().Be("env-1");
        result.RejectedCount.Should().Be(1);

        var rejected = await _store.ListOutboundAsync("t1", OutboundAckStatus.Rejected, 10);
        rejected.Single().AckNoteCode.Should().Be("006");
        rejected.Single().AcknowledgmentId.Should().Be(match.AcknowledgmentId);

        var stored = await _store.GetAcknowledgmentAsync("t1", match.AcknowledgmentId);
        stored!.Direction.Should().Be(Ta1Direction.Received);
        stored.OutboundInterchangeId.Should().Be(sent.Id);
    }

    [Fact]
    public async Task AcceptingTa1_MarksAccepted()
    {
        var tracker = Tracker();
        await tracker.RecordSentAsync("t1", Era835, "835", null);

        await tracker.ProcessInboundTa1Async("t1", PartnerTa1("A", "000"), null);

        (await _store.ListOutboundAsync("t1", OutboundAckStatus.Accepted, 10)).Should().ContainSingle();
    }

    [Fact]
    public async Task Ta1FromAnotherPartner_OrTenant_DoesNotMatch()
    {
        var tracker = Tracker();
        await tracker.RecordSentAsync("t1", Era835, "835", null);

        (await tracker.ProcessInboundTa1Async("t1", PartnerTa1("R", "006", partner: "SOMEONEELSE"), null))
            .Results.Single().Status.Should().Be(Ta1MatchStatus.Unmatched);
        (await tracker.ProcessInboundTa1Async("t2", PartnerTa1("R", "006"), null))
            .Results.Single().Status.Should().Be(Ta1MatchStatus.Unmatched);

        (await _store.ListOutboundAsync("t1", OutboundAckStatus.Pending, 10)).Should().ContainSingle();
    }
}
