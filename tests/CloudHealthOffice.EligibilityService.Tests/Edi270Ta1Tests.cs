using System.Net;
using System.Net.Http.Json;
using System.Text;
using CloudHealthOffice.Infrastructure.Edi.Interchange;
using CloudHealthOffice.Infrastructure.Security;
using EligibilityService.Models;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.EligibilityService.Tests;

/// <summary>
/// TA1 on the real-time 270 → 271 path: a rejected envelope is answered with
/// the TA1 alone (no 271), and the 271 that does go out is tracked so the
/// submitter's TA1 for it is recorded against it.
/// </summary>
public class Edi270Ta1Tests : IClassFixture<EligibilityApiFactory>
{
    private readonly EligibilityApiFactory _factory;
    private readonly HttpClient _client;

    public Edi270Ta1Tests(EligibilityApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateDefaultClient(new ChoDevelopmentTokenHandler());
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", "ta1-tenant");
    }

    private static string Edi270(string control, char isa14 = '0', string? iea02 = null) =>
        $"ISA*00*          *00*          *ZZ*PROVIDER01     *ZZ*CHO            *260309*1230*^*00501*{control}*{isa14}*P*:~" +
        "GS*HS*PROVIDER01*CHO*20260309*1230*1*X*005010X279A1~" +
        "ST*270*0001*005010X279A1~" +
        "BHT*0022*13*REF1*20260309*1230~" +
        "HL*1**20*1~NM1*PR*2*PAYER*****PI*PAY01~" +
        "HL*2*1*21*1~NM1*1P*2*PROVIDER*****XX*1234567890~" +
        "HL*3*2*22*0~NM1*IL*1*TESTMEMBER*ALEX****MI*SUB123~" +
        "DMG*D8*19800115*F~DTP*291*D8*20260301~EQ*30~" +
        "SE*12*0001~GE*1*1~" +
        $"IEA*1*{iea02 ?? control}~";

    private static StringContent Text(string s) => new(s, Encoding.UTF8, "text/plain");

    [Fact]
    public async Task RejectedEnvelope_IsAnsweredWithTheTa1Only()
    {
        var response = await _client.PostAsync("/api/eligibility/270", Text(Edi270("000000401", iea02: "000000499")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("TA1*000000401*260309*1230*R*001~", body);
        Assert.DoesNotContain("ST*271", body);
        Assert.DoesNotContain("ST*999", body);
        Assert.True(response.Headers.Contains("X-TA1-Acknowledgment-Id"));
    }

    [Fact]
    public async Task Outbound271_IsTracked_AndTheSubmittersRejectingTa1IsRecorded()
    {
        _factory.EligibilityService.ProcessInquiryAsync(Arg.Any<EligibilityInquiry>())
            .Returns(new EligibilityResponse { Id = "resp-ta1", TenantId = "ta1-tenant", ResponseCode = "AAA", IsCovered = true, CreatedDate = DateTime.UtcNow });

        var response = await _client.PostAsync("/api/eligibility/270", Text(Edi270("000000402", isa14: '1')));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("X-TA1-Acknowledgment-Id"));
        var edi271 = await response.Content.ReadAsStringAsync();
        var isa13 = X12EnvelopeReader.Read(edi271).Interchanges.Single().ControlNumber!;

        var outbound = await _client.GetFromJsonAsync<List<OutboundInterchangeRecord>>("/api/eligibility/interchange/outbound?status=Pending");
        var tracked = Assert.Single(outbound!, o => o.ControlNumber == isa13);
        Assert.Equal("271", tracked.TransactionType);
        Assert.Equal("resp-ta1", tracked.SourceReference);
        Assert.Equal("PROVIDER01", tracked.ReceiverId);

        var ta1 =
            "ISA*00*          *00*          *ZZ*PROVIDER01     *ZZ*CHO            *260309*1300*^*00501*000000777*0*P*:~" +
            $"TA1*{isa13}*260309*1230*R*008~IEA*0*000000777~";
        var posted = await _client.PostAsync("/api/eligibility/interchange/ta1/inbound", Text(ta1));
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);

        var rejected = await _client.GetFromJsonAsync<List<OutboundInterchangeRecord>>("/api/eligibility/interchange/outbound?status=Rejected");
        var record = Assert.Single(rejected!, o => o.ControlNumber == isa13);
        Assert.Equal("008", record.AckNoteCode);
    }

    [Fact]
    public async Task InboundEndpoint_RefusesWhatIsNotATa1()
    {
        var posted = await _client.PostAsync("/api/eligibility/interchange/ta1/inbound", Text("not x12"));
        Assert.Equal(HttpStatusCode.BadRequest, posted.StatusCode);
    }
}
