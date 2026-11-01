using CloudHealthOffice.Testing.Mongo;
using EncounterSubmissionService.Models;
using EncounterSubmissionService.StateRoutes;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace CloudHealthOffice.EncounterSubmissionService.Tests.StateRoutes;

internal static class ResponseFiles
{
    public static string Isa(string icn = "000000555") =>
        "ISA*00*          *00*          *ZZ*TMHPTESTRCV    *ZZ*TPID12345      *261102*0900*^*00501*" + icn + "*0*T*:~";

    public static string Ack999(string gs06, string groupCode, params (string St, string Code, string[] Errors)[] sets)
    {
        var body = $"GS*FA*TMHPTESTRCV*TPID12345*20261102*0900*9001*X*005010X231A1~ST*999*0001*005010X231A1~AK1*HC*{gs06}*005010X222A1~";
        foreach (var (st, code, errors) in sets)
        {
            body += $"AK2*837*{st}*005010X222A1~";
            foreach (var e in errors) body += e + "~";
            body += $"IK5*{code}~";
        }
        body += $"AK9*{groupCode}*{sets.Length}*{sets.Length}*{sets.Count(s => s.Code is "A" or "E")}~SE*9*0001~GE*1*9001~IEA*1*000000555~";
        return Isa() + body;
    }

    public static string Ack277(params (string Pcn, string Stc, string? Icn)[] claims)
    {
        var body = "GS*HN*TMHPTESTRCV*TPID12345*20261102*0900*9002*X*005010X214~ST*277*0001*005010X214~" +
                   "BHT*0085*08*277CA1*20261102*0900*TH~HL*1**20*1~NM1*PR*2*TMHP*****PI*TXMCD~" +
                   "HL*2*1*21*1~NM1*41*2*SAMPLE HEALTH PLAN*****46*TPID12345~HL*3*2*19*1~NM1*85*2*AUSTIN PEDIATRICS PA*****XX*1234567893~";
        var hl = 4;
        foreach (var (pcn, stc, icn) in claims)
        {
            body += $"HL*{hl++}*3*PT~NM1*QC*1*DOE*ALEX****MI*512345678~TRN*2*{pcn}~STC*{stc}*20261102*WQ*250~";
            if (icn is not null) body += $"REF*1K*{icn}~";
            body += "DTP*472*D8*20261003~SVC*HC:99213*250~STC*A1:20*20261102*WQ*250~";
        }
        return Isa() + body + "SE*20*0001~GE*1*9002~IEA*1*000000555~";
    }
}

public class StateResponseParserTests
{
    [Fact]
    public void Delimiters_ComeFromTheIsa()
    {
        var content = ResponseFiles.Ack999("100", "A").Replace('*', '|').Replace('~', '\n');
        var x12 = X12Segments.Parse(content);

        x12.TransactionType.Should().Be("999");
        Ack999Parser.Parse(x12).Should().ContainSingle().Which.GroupControlNumber.Should().Be("100");
    }

    [Fact]
    public void Ack999_ReadsPerTransactionCodesAndErrors()
    {
        var x12 = X12Segments.Parse(ResponseFiles.Ack999("100", "P",
            ("0001", "A", Array.Empty<string>()),
            ("0002", "R", new[] { "IK3*N4*12*2010BA*8", "IK4*3**6*7870" })));

        var group = Ack999Parser.Parse(x12).Single();
        group.GroupCode.Should().Be("P");
        group.Transactions.Select(t => (t.TransactionSetControlNumber, t.Code)).Should().Equal(("0001", "A"), ("0002", "R"));
        group.Transactions[1].Errors.Should().Contain(e => e.Contains("segment N4") && e.Contains("2010BA"))
            .And.Contain(e => e.Contains("element 3") && e.Contains("7870"));
    }

    [Fact]
    public void Ack277_ReadsClaimStatus_AndIgnoresServiceLineStatus()
    {
        var x12 = X12Segments.Parse(ResponseFiles.Ack277(("CLM-a", "A2:20", "TX123"), ("CLM-b", "A7:562:85", null)));

        var acks = Ack277Parser.Parse(x12);
        acks.Should().HaveCount(2);
        acks[0].Should().Match<ClaimAcknowledgment>(a =>
            a.PatientControlNumber == "CLM-a" && a.IsAccepted && a.StateClaimReference == "TX123" && a.ChargeAmount == 250m);
        acks[0].StatusCodes.Should().Equal("A2:20");
        acks[1].IsRejected.Should().BeTrue();
        acks[1].StatusCodes.Should().Equal("A7:562:85");
    }

    [Fact]
    public void NotAnInterchange_IsRefused()
    {
        var act = () => X12Segments.Parse("hello");
        act.Should().Throw<FormatException>();
    }
}

[Collection(MongoRunnerFixture.CollectionName)]
public sealed class StateResponseReconcilerTests : IAsyncLifetime
{
    private readonly MongoRunnerFixture _mongo;
    private readonly IMongoDatabase _db;
    private readonly IMongoCollection<EncounterSubmission> _submissions;
    private readonly StateResponseReconciler _reconciler;

    public StateResponseReconcilerTests(MongoRunnerFixture mongo)
    {
        _mongo = mongo;
        _db = mongo.CreateDatabase("enc_responses");
        _submissions = _db.GetCollection<EncounterSubmission>("encounter_submissions");
        _reconciler = new StateResponseReconciler(_db, NullLogger<StateResponseReconciler>.Instance);
    }

    public async Task InitializeAsync()
    {
        await _submissions.InsertManyAsync(new[]
        {
            Sent("a", "0001"),
            Sent("b", "0002"),
            Sent("a", "0001", tenant: "tenant-2"),
            Sent("c", "0001", state: "CA")
        });
    }

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_db);

    private static EncounterSubmission Sent(string claim, string st, string tenant = "tenant-1", string state = "TX") => new()
    {
        Id = $"{tenant}:{state}:{claim}",
        TenantId = tenant,
        ClaimId = claim,
        StateCode = state,
        Status = EncounterSubmissionStatus.Batched,
        InterchangeControlNumber = "000000100",
        GroupControlNumber = "100",
        TransactionSetControlNumber = st,
        PatientControlNumber = "CLM-" + claim,
        UpdatedAt = DateTime.UtcNow
    };

    private Task<EncounterSubmission> Get(string id) =>
        _submissions.Find(s => s.Id == id).FirstAsync();

    [Fact]
    public async Task Ack999_IsAppliedPerTransactionSet()
    {
        var result = await _reconciler.ApplyAsync(StateEncounterRoute.Texas, "tenant-1", "999.x12",
            ResponseFiles.Ack999("100", "P",
                ("0001", "A", Array.Empty<string>()),
                ("0002", "R", new[] { "IK3*N4*12*2010BA*8" })));

        result.Kind.Should().Be("999");
        result.Matched.Should().Be(2);
        result.Accepted.Should().Be(1);
        result.Rejected.Should().Be(1);

        var a = await Get("tenant-1:TX:a");
        a.Status.Should().Be(EncounterSubmissionStatus.SyntaxAccepted);
        a.ResponseHistory.Should().ContainSingle().Which.Kind.Should().Be("999");

        var b = await Get("tenant-1:TX:b");
        b.Status.Should().Be(EncounterSubmissionStatus.Rejected);
        b.LastError.Should().Contain("TMHP 999 rejected").And.Contain("segment N4");
        b.AcknowledgmentCode.Should().Be("R");

        (await Get("tenant-2:TX:a")).Status.Should().Be(EncounterSubmissionStatus.Batched, "another tenant's record is untouched");
        (await Get("tenant-1:CA:c")).Status.Should().Be(EncounterSubmissionStatus.Batched, "another state's record is untouched");

        var stored = await _db.GetCollection<StateEncounterResponse>("encounter_state_responses").Find(_ => true).ToListAsync();
        stored.Should().ContainSingle().Which.Should().Match<StateEncounterResponse>(r =>
            r.TenantId == "tenant-1" && r.StateCode == "TX" && r.Matched == 2 && r.FileName == "999.x12");
    }

    [Fact]
    public async Task Ack999_WithoutTransactionDetail_TakesTheGroupCode()
    {
        await _reconciler.ApplyAsync(StateEncounterRoute.Texas, "tenant-1", null, ResponseFiles.Ack999("100", "A"));

        (await Get("tenant-1:TX:a")).Status.Should().Be(EncounterSubmissionStatus.SyntaxAccepted);
        (await Get("tenant-1:TX:b")).Status.Should().Be(EncounterSubmissionStatus.SyntaxAccepted);

        await _submissions.UpdateManyAsync(s => s.TenantId == "tenant-1",
            Builders<EncounterSubmission>.Update.Set(s => s.Status, EncounterSubmissionStatus.Batched));
        await _reconciler.ApplyAsync(StateEncounterRoute.Texas, "tenant-1", null, ResponseFiles.Ack999("100", "R"));

        (await Get("tenant-1:TX:a")).Status.Should().Be(EncounterSubmissionStatus.Rejected);
    }

    [Fact]
    public async Task Ack277_AcceptsWithTheStateReference_RejectsWithCodes_AndReportsUnmatched()
    {
        var result = await _reconciler.ApplyAsync(StateEncounterRoute.Texas, "tenant-1", "277ca.x12",
            ResponseFiles.Ack277(("CLM-a", "A2:20", "TX123"), ("CLM-b", "A7:562:85", null), ("CLM-z", "A2:20", "TX999")));

        result.Kind.Should().Be("277CA");
        result.Accepted.Should().Be(1);
        result.Rejected.Should().Be(1);
        result.Unmatched.Should().Equal("CLM01 CLM-z");

        var a = await Get("tenant-1:TX:a");
        a.Status.Should().Be(EncounterSubmissionStatus.StateFrontEndAccepted);
        a.StateClaimReference.Should().Be("TX123");
        a.StateStatusCodes.Should().Equal("A2:20");

        var b = await Get("tenant-1:TX:b");
        b.Status.Should().Be(EncounterSubmissionStatus.Rejected);
        b.LastError.Should().Contain("A7:562:85");
    }

    [Fact]
    public async Task A999_ArrivingAfterThe277_DoesNotRegressAcceptance()
    {
        await _reconciler.ApplyAsync(StateEncounterRoute.Texas, "tenant-1", null,
            ResponseFiles.Ack277(("CLM-a", "A2:20", "TX123")));
        var result = await _reconciler.ApplyAsync(StateEncounterRoute.Texas, "tenant-1", null,
            ResponseFiles.Ack999("100", "A", ("0001", "A", Array.Empty<string>())));

        result.Matched.Should().Be(1, "only CLM-b was still waiting on its 999");
        (await Get("tenant-1:TX:a")).Status.Should().Be(EncounterSubmissionStatus.StateFrontEndAccepted);
    }

    [Fact]
    public async Task California_IsReconciledOnItsOwnRoute()
    {
        var result = await _reconciler.ApplyAsync(StateEncounterRoute.California, "tenant-1", null,
            ResponseFiles.Ack277(("CLM-c", "A1:19", null)));

        result.Kind.Should().Be("277");
        (await Get("tenant-1:CA:c")).Status.Should().Be(EncounterSubmissionStatus.StateFrontEndAccepted);
        (await Get("tenant-1:TX:a")).Status.Should().Be(EncounterSubmissionStatus.Batched);
    }

    [Fact]
    public async Task EvrXml_IsRefusedUntilItsSchemaIsImplemented()
    {
        var act = () => _reconciler.ApplyAsync(StateEncounterRoute.California, "tenant-1", "evr.xml",
            "<?xml version=\"1.0\"?><EVR/>");

        await act.Should().ThrowAsync<NotSupportedException>().WithMessage("*EVR*");
    }

    [Fact]
    public async Task Florida_UsesItsOwnAcknowledgmentPath()
    {
        var act = () => _reconciler.ApplyAsync(StateEncounterRoute.Florida, "tenant-1", null, ResponseFiles.Ack999("100", "A"));

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
