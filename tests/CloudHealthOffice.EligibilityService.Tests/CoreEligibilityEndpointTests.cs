using System.Net;
using System.Text;
using System.Xml.Linq;
using CloudHealthOffice.Infrastructure.Edi.Core;
using EligibilityService.Models;
using EligibilityService.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace CloudHealthOffice.EligibilityService.Tests;

/// <summary>
/// CAQH CORE Connectivity vC2.2.0 real-time 270/271 at <c>/core/eligibility</c>:
/// SOAP and MIME envelopes, submitter authentication, envelope field errors,
/// and the payload a 270 is answered with (271, TA1, 999, or a 271 with
/// AAA 42 when CHO cannot answer in time).
/// </summary>
public class CoreEligibilityEndpointTests : IClassFixture<EligibilityApiFactory>
{
    private const string Password = "core-test-password";
    private static readonly string PasswordHash = CorePasswordHash.Create(Password, iterations: 1000);

    private readonly EligibilityApiFactory _base;

    public CoreEligibilityEndpointTests(EligibilityApiFactory factory) => _base = factory;

    private WebApplicationFactory<Program> Factory(string budget = "00:00:10", Action<IServiceCollection>? services = null) =>
        _base.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CoreConnectivity:Enabled"] = "true",
                ["CoreConnectivity:ResponseBudget"] = budget,
                ["CoreConnectivity:ReceiverIds:0"] = "CHOPAYER",
                ["CoreConnectivity:Submitters:0:SenderId"] = "CLEARINGHOUSE1",
                ["CoreConnectivity:Submitters:0:TenantId"] = "core-tenant",
                ["CoreConnectivity:Submitters:0:UserName"] = "ch1",
                ["CoreConnectivity:Submitters:0:PasswordHash"] = PasswordHash,
            }));
            if (services is not null)
                b.ConfigureServices(services);
        });

    private static string Edi270(string control = "000000901", string? iea02 = null) =>
        $"ISA*00*          *00*          *ZZ*PROVIDER01     *ZZ*CHO            *260309*1230*^*00501*{control}*0*P*:~" +
        "GS*HS*PROVIDER01*CHO*20260309*1230*1*X*005010X279A1~" +
        "ST*270*0001*005010X279A1~" +
        "BHT*0022*13*REF1*20260309*1230~" +
        "HL*1**20*1~NM1*PR*2*PAYER*****PI*PAY01~" +
        "HL*2*1*21*1~NM1*1P*2*PROVIDER*****XX*1234567890~" +
        "HL*3*2*22*0~NM1*IL*1*TESTMEMBER*ALEX****MI*SUB123~" +
        "DMG*D8*19800115*F~DTP*291*D8*20260301~EQ*30~" +
        "SE*12*0001~GE*1*1~" +
        $"IEA*1*{iea02 ?? control}~";

    private static readonly string PayloadId = "f81d4fae-7dec-11d0-a765-00a0c91e6bf6";

    private static StringContent Soap(string payload, string user = "ch1", string password = Password,
        string? payloadId = null, string version = "2.2.0", string sender = "CLEARINGHOUSE1")
    {
        var idElement = payloadId == "" ? "" : $"<PayloadID>{payloadId ?? PayloadId}</PayloadID>";
        var xml = $"""
            <soapenv:Envelope xmlns:soapenv="http://www.w3.org/2003/05/soap-envelope" xmlns:cor="http://www.caqh.org/SOAP/WSDL/CORERule2.2.0.xsd">
              <soapenv:Header>
                <wsse:Security xmlns:wsse="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd">
                  <wsse:UsernameToken><wsse:Username>{user}</wsse:Username>
                  <wsse:Password Type="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordText">{password}</wsse:Password></wsse:UsernameToken>
                </wsse:Security>
              </soapenv:Header>
              <soapenv:Body>
                <cor:COREEnvelopeRealTimeRequest>
                  <PayloadType>X12_270_Request_005010X279A1</PayloadType>
                  <ProcessingMode>RealTime</ProcessingMode>
                  {idElement}
                  <TimeStamp>2026-10-30T10:20:34Z</TimeStamp>
                  <SenderID>{sender}</SenderID>
                  <ReceiverID>CHOPAYER</ReceiverID>
                  <CORERuleVersion>{version}</CORERuleVersion>
                  <Payload><![CDATA[{payload}]]></Payload>
                </cor:COREEnvelopeRealTimeRequest>
              </soapenv:Body>
            </soapenv:Envelope>
            """;
        return new StringContent(xml, Encoding.UTF8, "application/soap+xml");
    }

    private static XElement ResponseBody(string xml) =>
        XDocument.Parse(xml).Descendants().Single(e => e.Name.LocalName == "COREEnvelopeRealTimeResponse");

    private static string Field(XElement response, string name) =>
        response.Elements().Single(e => e.Name.LocalName == name).Value;

    private void Covered() =>
        _base.EligibilityService.ProcessInquiryAsync(Arg.Any<EligibilityInquiry>())
            .Returns(new EligibilityResponse { Id = Guid.NewGuid().ToString(), TenantId = "core-tenant", ResponseCode = "AAA", IsCovered = true, CreatedDate = DateTime.UtcNow });

    [Fact]
    public async Task Soap270_IsAnsweredWith271_InACoreEnvelope()
    {
        Covered();
        using var client = Factory().CreateClient();

        var response = await client.PostAsync("/core/eligibility", Soap(Edi270()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("application/soap+xml", response.Content.Headers.ContentType!.ToString());
        var body = ResponseBody(await response.Content.ReadAsStringAsync());
        Assert.Equal("X12_271_Response_005010X279A1", Field(body, "PayloadType"));
        Assert.Equal("Success", Field(body, "ErrorCode"));
        Assert.Equal(PayloadId, Field(body, "PayloadID"));
        Assert.Equal("CHOPAYER", Field(body, "SenderID"));
        Assert.Equal("CLEARINGHOUSE1", Field(body, "ReceiverID"));
        Assert.Equal("2.2.0", Field(body, "CORERuleVersion"));
        Assert.Contains("ST*271*", Field(body, "Payload"));
    }

    [Fact]
    public async Task TheSubmittersTenant_ComesFromItsRegistration()
    {
        Covered();
        using var client = Factory().CreateClient();

        await client.PostAsync("/core/eligibility", Soap(Edi270("000000902")));

        await _base.EligibilityService.Received().ProcessInquiryAsync(Arg.Is<EligibilityInquiry>(i => i.TenantId == "core-tenant"));
    }

    [Fact]
    public async Task Mime270_IsAnsweredWith271_InAMimeEnvelope()
    {
        Covered();
        using var client = Factory().CreateClient();
        var form = new MultipartFormDataContent
        {
            { new StringContent("X12_270_Request_005010X279A1"), "PayloadType" },
            { new StringContent("RealTime"), "ProcessingMode" },
            { new StringContent(PayloadId), "PayloadID" },
            { new StringContent("2026-10-30T10:20:34Z"), "TimeStamp" },
            { new StringContent("CLEARINGHOUSE1"), "SenderID" },
            { new StringContent("CHOPAYER"), "ReceiverID" },
            { new StringContent("2.2.0"), "CORERuleVersion" },
            { new StringContent("ch1"), "UserName" },
            { new StringContent(Password), "Password" },
            { new StringContent(Edi270("000000903")), "Payload" },
        };

        var response = await client.PostAsync("/core/eligibility", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var parts = await new MultipartReaderHelper(response).ReadFieldsAsync();
        Assert.Equal("X12_271_Response_005010X279A1", parts["PayloadType"]);
        Assert.Equal("Success", parts["ErrorCode"]);
        Assert.Contains("ST*271*", parts["Payload"]);
    }

    [Fact]
    public async Task WrongPassword_IsUnAuthorized_AndNoInquiryRuns()
    {
        using var client = Factory().CreateClient();
        _base.EligibilityService.ClearReceivedCalls();

        var response = await client.PostAsync("/core/eligibility", Soap(Edi270(), password: "wrong"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UnAuthorized", Field(ResponseBody(await response.Content.ReadAsStringAsync()), "ErrorCode"));
        await _base.EligibilityService.DidNotReceive().ProcessInquiryAsync(Arg.Any<EligibilityInquiry>());
    }

    [Fact]
    public async Task CredentialsOfAnotherSender_AreUnAuthorized()
    {
        using var client = Factory().CreateClient();

        var response = await client.PostAsync("/core/eligibility", Soap(Edi270(), sender: "SOMEONEELSE"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MissingPayloadId_IsPayloadIDRequired()
    {
        using var client = Factory().CreateClient();

        var response = await client.PostAsync("/core/eligibility", Soap(Edi270(), payloadId: ""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("PayloadIDRequired", Field(ResponseBody(await response.Content.ReadAsStringAsync()), "ErrorCode"));
    }

    [Fact]
    public async Task OtherRuleVersion_IsVersionMismatch()
    {
        using var client = Factory().CreateClient();

        var response = await client.PostAsync("/core/eligibility", Soap(Edi270(), version: "1.1.0"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("VersionMismatch", Field(ResponseBody(await response.Content.ReadAsStringAsync()), "ErrorCode"));
    }

    [Fact]
    public async Task RejectedInterchange_IsAnsweredWithATa1Payload()
    {
        using var client = Factory().CreateClient();

        var response = await client.PostAsync("/core/eligibility", Soap(Edi270("000000904", iea02: "000000999")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = ResponseBody(await response.Content.ReadAsStringAsync());
        Assert.Equal("X12_TA1_Response_00501X231A1", Field(body, "PayloadType"));
        Assert.Contains("TA1*000000904*", Field(body, "Payload"));
        Assert.DoesNotContain("ST*271", Field(body, "Payload"));
    }

    [Fact]
    public async Task UnparseableContent_IsAnsweredWithA999Rejection()
    {
        var parser = Substitute.For<IEdi270Parser>();
        parser.Parse(Arg.Any<string>()).Throws(new FormatException("bad 270"));
        using var client = Factory(services: s => s.AddScoped(_ => parser)).CreateClient();

        var response = await client.PostAsync("/core/eligibility", Soap(Edi270("000000905")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = ResponseBody(await response.Content.ReadAsStringAsync());
        Assert.Equal("X12_999_Response_005010X231A1", Field(body, "PayloadType"));
        var edi999 = Field(body, "Payload");
        Assert.Contains("AK1*HS*1*005010X279A1~", edi999);
        Assert.Contains("AK2*270*0001*005010X279A1~", edi999);
        Assert.Contains("IK5*R*5~", edi999);
        Assert.Contains("AK9*R*1*1*0~", edi999);
    }

    [Fact]
    public async Task CoverageUnavailable_IsA271WithAaa42()
    {
        _base.EligibilityService.ProcessInquiryAsync(Arg.Any<EligibilityInquiry>())
            .ThrowsAsync(new CoverageLookupUnavailableException(new HttpRequestException("down")));
        using var client = Factory().CreateClient();

        var response = await client.PostAsync("/core/eligibility", Soap(Edi270("000000906")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = ResponseBody(await response.Content.ReadAsStringAsync());
        Assert.Equal("X12_271_Response_005010X279A1", Field(body, "PayloadType"));
        Assert.Contains("AAA*Y**42*R~", Field(body, "Payload"));
    }

    [Fact]
    public async Task SlowInquiry_IsAnsweredWithAaa42_WithinTheBudget()
    {
        _base.EligibilityService.ProcessInquiryAsync(Arg.Any<EligibilityInquiry>())
            .Returns(new TaskCompletionSource<EligibilityResponse>().Task);
        using var client = Factory(budget: "00:00:00.300").CreateClient();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var response = await client.PostAsync("/core/eligibility", Soap(Edi270("000000907")));
        watch.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("AAA*Y**42*R~", Field(ResponseBody(await response.Content.ReadAsStringAsync()), "Payload"));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task Wsdl_IsServed()
    {
        using var client = Factory().CreateClient();

        var wsdl = await client.GetStringAsync("/core/eligibility?wsdl");

        Assert.Contains("RealTimeTransaction", wsdl);
        Assert.Contains("COREEnvelopeRealTimeRequest", wsdl);
        XDocument.Parse(wsdl);
    }

    [Fact]
    public async Task Endpoint_IsNotMapped_UnlessEnabled()
    {
        using var client = _base.CreateClient();

        var response = await client.PostAsync("/core/eligibility", Soap(Edi270()));

        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized, response.StatusCode.ToString());
    }

    /// <summary>Reads a multipart/form-data response into field name → value.</summary>
    private sealed class MultipartReaderHelper(HttpResponseMessage response)
    {
        public async Task<Dictionary<string, string>> ReadFieldsAsync()
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
    }
}
