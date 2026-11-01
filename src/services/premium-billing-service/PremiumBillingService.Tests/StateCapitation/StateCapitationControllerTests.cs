using System.Text;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using PremiumBillingService.Controllers;
using PremiumBillingService.Models;
using PremiumBillingService.StateCapitation;

namespace PremiumBillingService.Tests.StateCapitation;

/// <summary>
/// Finance's monthly state capitation close for Texas and Medi-Cal: load the
/// state rate sheet and the expected member-months, record the state's 820s,
/// read the reconciliation.
/// </summary>
public class StateCapitationControllerTests
{
    /// <summary>In-memory store with the Mongo store's keys: one tenant, replace-by-month, unique trace.</summary>
    private sealed class MemoryStore : IStateCapitationStore
    {
        private readonly Dictionary<string, StoredStateRate> _rates = new();
        private readonly Dictionary<string, StoredExpectedMonth> _expected = new();
        private readonly Dictionary<string, StoredStatePayment> _payments = new();

        public Task<int> UpsertRatesAsync(string state, IReadOnlyList<StoredStateRate> rates)
        {
            foreach (var r in rates) { r.State = state; _rates[StateCapitationStoreMongo.RateId("t", state, r.RateCell, r.EffectiveFrom)] = r; }
            return Task.FromResult(rates.Count);
        }

        public Task<IReadOnlyList<StateCapitationRate>> GetRatesAsync(string state) =>
            Task.FromResult<IReadOnlyList<StateCapitationRate>>(_rates.Values.Where(r => r.State == state).Select(r => r.ToRate()).ToList());

        public Task<int> ReplaceExpectedAsync(string state, IReadOnlyCollection<DateOnly> months, IReadOnlyList<StoredExpectedMonth> rows)
        {
            foreach (var key in _expected.Where(e => e.Value.State == state && months.Contains(DateOnly.FromDateTime(e.Value.Month))).Select(e => e.Key).ToList())
                _expected.Remove(key);
            foreach (var r in rows) { r.State = state; _expected[StateCapitationStoreMongo.ExpectedId("t", state, r.MemberId, r.Month)] = r; }
            return Task.FromResult(rows.Count);
        }

        public Task<IReadOnlyList<ExpectedMemberMonth>> GetExpectedAsync(string state, DateOnly fromMonth, DateOnly toMonth) =>
            Task.FromResult<IReadOnlyList<ExpectedMemberMonth>>(_expected.Values
                .Where(e => e.State == state && DateOnly.FromDateTime(e.Month) >= fromMonth && DateOnly.FromDateTime(e.Month) <= toMonth)
                .Select(e => e.ToExpected()).ToList());

        public Task<bool> TryAddPaymentAsync(StoredStatePayment payment) =>
            Task.FromResult(_payments.TryAdd(StateCapitationStoreMongo.PaymentId("t", payment.State, payment.TraceNumber), payment));

        public Task<IReadOnlyList<RemittanceAdvice>> GetPaymentsAsync(string state) =>
            Task.FromResult<IReadOnlyList<RemittanceAdvice>>(_payments.Values.Where(p => p.State == state).Select(p => p.Advice).ToList());
    }

    private readonly MemoryStore _store = new();

    private StateCapitationController Controller(string? body = null)
    {
        var http = new DefaultHttpContext();
        if (body != null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            http.Request.Body = new MemoryStream(bytes);
            http.Request.ContentLength = bytes.Length;
        }
        var actor = Mock.Of<ICurrentActor>(a => a.UserId == "finance-1" && a.TenantId == "t");
        return new StateCapitationController(_store, actor, NullLogger<StateCapitationController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private static JsonElement Json(IActionResult result)
    {
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        return JsonSerializer.SerializeToElement(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static string Medi820(string trace, params (string Member, decimal Paid, string Cell)[] lines)
    {
        var segs = new List<string>
        {
            "ST*820*0001*005010X218",
            $"BPR*C*{lines.Sum(l => l.Paid):0.00}*C*ACH*CCP*01*121000358*DA*1*1946000000**01*122000247*DA*2*20270110",
            $"TRN*1*{trace}*1946000000",
            "N1*PR*CALIFORNIA DHCS",
        };
        var n = 1;
        foreach (var l in lines)
        {
            segs.Add($"ENT*{n++}*2J*34*{l.Member}");
            segs.Add($"NM1*IL*1*ROE*ANA****C1*{l.Member}");
            segs.Add($"RMR*AZ*{l.Member}**{l.Paid:0.00}");
            segs.Add($"REF*ZZ*{l.Cell}");
            segs.Add("DTM*582****RD8*20270101-20270131");
        }
        segs.Add($"SE*{segs.Count + 1}*0001");
        return "ISA*00*          *00*          *ZZ*DHCS           *ZZ*PLAN           *270110*0900*^*00501*000000002*0*P*:~"
               + "GS*RA*DHCS*PLAN*20270110*0900*2*X*005010X218~" + string.Join("~", segs) + "~GE*1*2~IEA*1*000000002~";
    }

    [Fact]
    public async Task MediCal_MonthlyClose_LoadsReconcilesAndExports()
    {
        Json(await Controller("rate_cell,effective_from,effective_to,pmpm\nLA-ACA-EXP,2027-01-01,,530.25\nLA-CHILD,2027-01-01,2027-12-31,180.40\n")
            .LoadRates("ca")).GetProperty("ratesLoaded").GetInt32().Should().Be(2);
        Json(await Controller("member_id,month,rate_cell\n91000001A,2027-01,LA-ACA-EXP\n91000002B,2027-01,LA-CHILD\n91000003C,2027-01,LA-CHILD\n")
            .LoadExpected("CA")).GetProperty("memberMonthsLoaded").GetInt32().Should().Be(3);

        var file = Medi820("DHCS-202701", ("91000001A", 530.25m, "LA-ACA-EXP"), ("91000002B", 180.40m, "LA-CHILD"));
        Json(await Controller(file).Record820("CA")).GetProperty("recorded").GetArrayLength().Should().Be(1);
        // The same file again is not counted twice.
        Json(await Controller(file).Record820("CA")).GetProperty("duplicates").GetArrayLength().Should().Be(1);

        var rec = Json(await Controller().Reconciliation("CA", "2027-01", "2027-01"));
        rec.GetProperty("totalExpected").GetDecimal().Should().Be(530.25m + 180.40m * 2);
        rec.GetProperty("totalReceived").GetDecimal().Should().Be(710.65m);
        rec.GetProperty("exceptionCount").GetInt32().Should().Be(1);
        rec.GetProperty("exceptions")[0].GetProperty("memberId").GetString().Should().Be("91000003C");
        rec.GetProperty("exceptions")[0].GetProperty("outcome").GetString().Should().Be("NotPaid");
        rec.GetProperty("profileVerified").GetBoolean().Should().BeFalse();

        var csv = await Controller().ReconciliationCsv("CA", "2027-01", "2027-01");
        var text = Encoding.UTF8.GetString(csv.Should().BeOfType<FileContentResult>().Subject.FileContents);
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(4);
        text.Should().Contain("91000003C,2027-01,LA-CHILD,,180.40,0.00,-180.40,NotPaid");
    }

    [Fact]
    public async Task ACorrectedRoster_ReplacesItsMonth()
    {
        await Controller("member_id,month,rate_cell\nA1,2027-01,STAR-X\nA2,2027-01,STAR-X\nA3,2027-02,STAR-X\n").LoadExpected("TX");
        await Controller("member_id,month,rate_cell\nA1,2027-01,STAR-Y\n").LoadExpected("TX");

        var expected = await _store.GetExpectedAsync("TX", new DateOnly(2027, 1, 1), new DateOnly(2027, 2, 1));

        expected.Select(e => (e.MemberId, e.RateCell)).Should().BeEquivalentTo(new[] { ("A1", "STAR-Y"), ("A3", "STAR-X") });
    }

    [Fact]
    public async Task ABadRow_RefusesTheWholeFile()
    {
        var result = await Controller("rate_cell,effective_from,pmpm\nGOOD,2027-01-01,10.00\nBAD,2027-13-01,10.00\n").LoadRates("TX");

        result.Should().BeOfType<BadRequestObjectResult>();
        (await _store.GetRatesAsync("TX")).Should().BeEmpty();
    }

    [Theory]
    [InlineData("NY", "2027-01", "2027-01", typeof(NotFoundObjectResult))]
    [InlineData("TX", "2027-02", "2027-01", typeof(BadRequestObjectResult))]
    [InlineData("TX", "2027-01", "2030-01", typeof(BadRequestObjectResult))]
    [InlineData("TX", "Jan", "2027-01", typeof(BadRequestObjectResult))]
    public async Task UnknownStateOrBadWindow_IsRefused(string state, string from, string to, Type expected)
    {
        (await Controller().Reconciliation(state, from, to)).Should().BeOfType(expected);
    }

    [Fact]
    public async Task AnUnreadable820_RecordsNothing()
    {
        (await Controller("ISA*garbage~").Record820("TX")).Should().BeOfType<BadRequestObjectResult>();
        (await _store.GetPaymentsAsync("TX")).Should().BeEmpty();
    }

    [Fact]
    public void Csv_ReadsQuotedFields_AndOptionalColumns()
    {
        var rows = StateCapitationCsv.ReadExpected("Month,Member_ID,Rate_Cell,Days_Enrolled\n2027-02,\"M,1\",STAR-HARRIS-ADULT,14\n", out var errors);

        errors.Should().BeEmpty();
        rows.Single().MemberId.Should().Be("M,1");
        rows.Single().DaysEnrolled.Should().Be(14);
    }

    [Fact]
    public async Task Mlr_UsesTheCapitationReceivedInThePeriod_AndTheLoadedMemberMonths()
    {
        await Controller("member_id,month,rate_cell\n91000001A,2027-01,LA-ACA-EXP\n91000002B,2027-01,LA-CHILD\n").LoadExpected("CA");
        await Controller(Medi820("DHCS-1", ("91000001A", 530.25m, "LA-ACA-EXP"), ("91000002B", 180.40m, "LA-CHILD"))).Record820("CA");

        var body = Json(await Controller().Mlr("CA", new MlrRequest
        {
            From = "2027-01", To = "2027-12", IncurredClaims = 600m, QualityImprovement = 10m, OtherPremiumRevenue = 89.35m
        }));

        body.GetProperty("capitationReceived").GetDecimal().Should().Be(710.65m);
        body.GetProperty("memberMonths").GetInt32().Should().Be(2);
        body.GetProperty("mlr").GetProperty("adjustedPremiumRevenue").GetDecimal().Should().Be(800m);
        body.GetProperty("mlr").GetProperty("unadjustedMlr").GetDecimal().Should().Be(0.7625m);
        body.GetProperty("mlr").GetProperty("credibility").GetInt32().Should().Be((int)PremiumBillingService.Mlr.MlrCredibility.NonCredible);
    }
}
