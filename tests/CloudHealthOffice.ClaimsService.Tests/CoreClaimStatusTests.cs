using System.Net;
using System.Text;
using System.Xml.Linq;
using ClaimsService.EDI.ClaimStatusInquiry;
using ClaimsService.Models;
using CloudHealthOffice.Infrastructure.Edi.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests;

internal static class Edi276Samples
{
    public static string Edi276(string claimRefs, string control = "000000501", string dependent = "") =>
        $"ISA*00*          *00*          *ZZ*CLEARINGHOUSE1 *ZZ*CHO            *261030*1200*^*00501*{control}*0*P*:~" +
        "GS*HR*CLEARINGHOUSE1*CHO*20261030*1200*7*X*005010X212~" +
        "ST*276*0001*005010X212~" +
        "BHT*0010*13*REQ123*20261030*1200~" +
        "HL*1**20*1~NM1*PR*2*CHO HEALTH PLAN*****PI*CHOPAYER~" +
        "HL*2*1*21*1~NM1*41*2*CLEARINGHOUSE ONE*****46*CH1ETIN~" +
        "HL*3*2*19*1~NM1*1P*2*GOOD CLINIC*****XX*1234567893~" +
        $"HL*4*3*22*{(dependent.Length > 0 ? "1" : "0")}~NM1*IL*1*DOE*JANE****MI*M100~" +
        (dependent.Length > 0 ? $"HL*5*4*23*0~DMG*D8*20150101*F~NM1*QC*1*{dependent}*SAM~" : "") +
        "TRN*1*TRACE-1~" + claimRefs +
        "AMT*T3*150~DTP*472*RD8*20261001-20261001~" +
        "SE*14*0001~GE*1*7~" +
        $"IEA*1*{control}~";
}

public class Edi276ParserTests
{
    [Fact]
    public void Parse_ReadsTheInquiry()
    {
        var parsed = Edi276Parser.Parse(Edi276Samples.Edi276("REF*1K*claim-1~REF*EJ*PCN9~"));

        Assert.Equal("CLEARINGHOUSE1", parsed.InterchangeSenderId);
        Assert.Equal("CHO", parsed.InterchangeReceiverId);
        Assert.Equal("REQ123", parsed.ReferenceId);
        Assert.Equal("CHOPAYER", parsed.PayerId);
        var q = Assert.Single(parsed.Inquiries);
        Assert.Equal("TRACE-1", q.TraceNumber);
        Assert.Equal("M100", q.SubscriberId);
        Assert.Equal("1234567893", q.ProviderNpi);
        Assert.Equal("claim-1", q.PayerClaimControlNumber);
        Assert.Equal("PCN9", q.PatientControlNumber);
        Assert.Equal(150m, q.TotalChargeAmount);
        Assert.Equal(new DateTime(2026, 10, 1), q.ServiceDateFrom);
        Assert.False(q.IsDependent);
    }

    [Fact]
    public void Parse_ReadsADependent()
    {
        var q = Assert.Single(Edi276Parser.Parse(Edi276Samples.Edi276("", dependent: "DOE")).Inquiries);

        Assert.True(q.IsDependent);
        Assert.Equal("DOE", q.DependentLastName);
        Assert.Equal("M100", q.SubscriberId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ISA*short")]
    public void Parse_RejectsWhatIsNotA276(string edi) =>
        Assert.ThrowsAny<Exception>(() => Edi276Parser.Parse(edi));

    [Fact]
    public void Parse_RejectsAnotherTransactionSet() =>
        Assert.Throws<FormatException>(() => Edi276Parser.Parse(Edi276Samples.Edi276("").Replace("ST*276*", "ST*270*")));
}

public class Edi277ClaimStatusGeneratorTests
{
    private static readonly DateTime Now = new(2026, 10, 30, 12, 5, 0, DateTimeKind.Utc);

    private static Claim PaidClaim() => new()
    {
        Id = "claim-1", ClaimNumber = "PCN9", MemberId = "M100", BillingProviderNPI = "1234567893",
        Status = ClaimStatus.Paid, TotalChargeAmount = 150m,
        ServiceDateFrom = new DateTime(2026, 10, 1), ServiceDateTo = new DateTime(2026, 10, 1),
        AdjudicatedDate = new DateTime(2026, 10, 10), PaidDate = new DateTime(2026, 10, 12),
        AdjudicationResult = new AdjudicationResult { PayerPayment = 120m },
    };

    [Fact]
    public void Generate_ReportsAPaidClaim_AndEchoesTheTrace()
    {
        var parsed = Edi276Parser.Parse(Edi276Samples.Edi276("REF*1K*claim-1~"));
        var edi = Edi277ClaimStatusGenerator.Generate(parsed, [new ClaimStatusAnswer(parsed.Inquiries[0], [PaidClaim()])], 42, Now);

        Assert.StartsWith("ISA*00*          *00*          *ZZ*CHO            *ZZ*CLEARINGHOUSE1 *261030*1205*^*00501*000000042*0*P*:~", edi);
        Assert.Contains("GS*HN*CHO*CLEARINGHOUSE1*20261030*1205*42*X*005010X212~", edi);
        Assert.Contains("ST*277*0001*005010X212~BHT*0010*08*REQ123*20261030*1205*DG~", edi);
        Assert.Contains("TRN*2*TRACE-1~STC*F1:65*20261010**150*120*20261010**20261012~REF*1K*claim-1~REF*EJ*PCN9~DTP*472*RD8*20261001-20261001~", edi);
    }

    [Fact]
    public void Generate_CountsItsSegments()
    {
        var parsed = Edi276Parser.Parse(Edi276Samples.Edi276("", dependent: "DOE"));
        var edi = Edi277ClaimStatusGenerator.Generate(parsed, [new ClaimStatusAnswer(parsed.Inquiries[0], [])], 1, Now);

        var segments = edi.Split('~', StringSplitOptions.RemoveEmptyEntries);
        var st = Array.FindIndex(segments, s => s.StartsWith("ST*"));
        var se = Array.FindIndex(segments, s => s.StartsWith("SE*"));
        Assert.Equal($"SE*{se - st + 1}*0001", segments[se]);
        Assert.Contains("HL*5*4*23*0~NM1*QC*1*DOE*SAM~TRN*2*TRACE-1~STC*A4:35*20261030**150*0~", edi);
    }

    [Theory]
    [InlineData(ClaimStatus.Received, "A2:20")]
    [InlineData(ClaimStatus.InAdjudication, "P1:20")]
    [InlineData(ClaimStatus.Pended, "P2:20")]
    [InlineData(ClaimStatus.Approved, "P1:3")]
    [InlineData(ClaimStatus.Deny, "P1:3")]
    [InlineData(ClaimStatus.Paid, "F1:65")]
    [InlineData(ClaimStatus.Denied, "F2:1")]
    public void Stc01_MapsEachStatus(ClaimStatus status, string expected) =>
        Assert.Equal(expected, Edi277ClaimStatusGenerator.Stc01(status));
}

/// <summary>CAQH CORE real-time 276/277 at <c>/core/claimstatus</c>.</summary>
public class CoreClaimStatusEndpointTests : IClassFixture<ClaimsApiFactory>
{
    private const string Password = "core-test-password";
    private static readonly string PasswordHash = CorePasswordHash.Create(Password, iterations: 1000);
    private readonly ClaimsApiFactory _base;

    public CoreClaimStatusEndpointTests(ClaimsApiFactory factory) => _base = factory;

    private WebApplicationFactory<Program> Factory() =>
        _base.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CoreConnectivity:Enabled"] = "true",
            ["CoreConnectivity:Submitters:0:SenderId"] = "CLEARINGHOUSE1",
            ["CoreConnectivity:Submitters:0:TenantId"] = "core-tenant",
            ["CoreConnectivity:Submitters:0:UserName"] = "ch1",
            ["CoreConnectivity:Submitters:0:PasswordHash"] = PasswordHash,
        })));

    private static MultipartFormDataContent Mime(string payload, string payloadType = "X12_276_Request_005010X212") => new()
    {
        { new StringContent(payloadType), "PayloadType" },
        { new StringContent("RealTime"), "ProcessingMode" },
        { new StringContent(Guid.NewGuid().ToString()), "PayloadID" },
        { new StringContent("2026-10-30T12:00:00Z"), "TimeStamp" },
        { new StringContent("CLEARINGHOUSE1"), "SenderID" },
        { new StringContent("CHOPAYER"), "ReceiverID" },
        { new StringContent("2.2.0"), "CORERuleVersion" },
        { new StringContent("ch1"), "UserName" },
        { new StringContent(Password), "Password" },
        { new StringContent(payload), "Payload" },
    };

    private static async Task<Dictionary<string, string>> Fields(HttpResponseMessage response)
    {
        var boundary = response.Content.Headers.ContentType!.Parameters.Single(p => p.Name == "boundary").Value!.Trim('"');
        var reader = new Microsoft.AspNetCore.WebUtilities.MultipartReader(boundary, await response.Content.ReadAsStreamAsync());
        var fields = new Dictionary<string, string>();
        while (await reader.ReadNextSectionAsync() is { } section)
        {
            var name = Microsoft.Net.Http.Headers.ContentDispositionHeaderValue.Parse(section.ContentDisposition).Name.Value!.Trim('"');
            using var sr = new StreamReader(section.Body);
            fields[name] = await sr.ReadToEndAsync();
        }
        return fields;
    }

    private static Claim ClaimFor(string memberId, ClaimStatus status = ClaimStatus.Paid) => new()
    {
        Id = $"claim-{memberId}", ClaimNumber = "PCN9", MemberId = memberId, TenantId = "core-tenant",
        BillingProviderNPI = "1234567893", Status = status, TotalChargeAmount = 150m,
        ServiceDateFrom = new DateTime(2026, 10, 1), ServiceDateTo = new DateTime(2026, 10, 1),
        AdjudicatedDate = new DateTime(2026, 10, 10), AdjudicationResult = new AdjudicationResult { PayerPayment = 120m },
    };

    [Fact]
    public async Task A276ForTheSubscribersClaim_IsAnsweredWithItsStatus()
    {
        _base.ClaimRepository.GetByIdAsync("claim-M100").Returns(ClaimFor("M100"));
        using var client = Factory().CreateClient();

        var response = await client.PostAsync("/core/claimstatus", Mime(Edi276Samples.Edi276("REF*1K*claim-M100~", "000000601")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var fields = await Fields(response);
        Assert.Equal("X12_277_Response_005010X212", fields["PayloadType"]);
        Assert.Equal("Success", fields["ErrorCode"]);
        Assert.Contains("TRN*2*TRACE-1~STC*F1:65*", fields["Payload"]);
        Assert.Contains("REF*1K*claim-M100~", fields["Payload"]);
    }

    [Fact]
    public async Task AClaimOfAnotherMember_IsNotFound_NotDisclosed()
    {
        _base.ClaimRepository.GetByIdAsync("claim-OTHER").Returns(ClaimFor("OTHER"));
        using var client = Factory().CreateClient();

        var response = await client.PostAsync("/core/claimstatus", Mime(Edi276Samples.Edi276("REF*1K*claim-OTHER~", "000000602")));

        var payload = (await Fields(response))["Payload"];
        Assert.Contains("STC*A4:35*", payload);
        Assert.DoesNotContain("F1:65", payload);
    }

    [Fact]
    public async Task WithoutClaimReferences_TheSubscribersClaimsAreSearched()
    {
        _base.ClaimRepository.SearchAsync("M100", "1234567893", Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), null, null, 1, Arg.Any<int>())
            .Returns(new[] { ClaimFor("M100", ClaimStatus.Pended) });
        using var client = Factory().CreateClient();

        var response = await client.PostAsync("/core/claimstatus", Mime(Edi276Samples.Edi276("", "000000603")));

        Assert.Contains("STC*P2:20*", (await Fields(response))["Payload"]);
    }

    [Fact]
    public async Task ALookupFailure_IsE1ForThatInquiry()
    {
        _base.ClaimRepository.GetByIdAsync("claim-BROKEN").ThrowsAsync(new TimeoutException("db"));
        using var client = Factory().CreateClient();

        var response = await client.PostAsync("/core/claimstatus", Mime(Edi276Samples.Edi276("REF*1K*claim-BROKEN~", "000000604")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("STC*E1:0*", (await Fields(response))["Payload"]);
    }

    [Fact]
    public async Task A270_IsPayloadTypeIllegalHere()
    {
        using var client = Factory().CreateClient();

        var response = await client.PostAsync("/core/claimstatus", Mime("ISA*...", "X12_270_Request_005010X279A1"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("PayloadTypeIllegal", (await Fields(response))["ErrorCode"]);
    }

    [Fact]
    public async Task Wsdl_IsServed()
    {
        using var client = Factory().CreateClient();

        XDocument.Parse(await client.GetStringAsync("/core/claimstatus?wsdl"));
    }
}
