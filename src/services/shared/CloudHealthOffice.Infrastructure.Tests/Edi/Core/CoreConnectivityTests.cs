using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using CloudHealthOffice.Infrastructure.Edi.Core;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CloudHealthOffice.Infrastructure.Tests.Edi.Core;

public class CoreSoapCodecTests
{
    private const string Request = """
        <soapenv:Envelope xmlns:soapenv="http://www.w3.org/2003/05/soap-envelope">
          <soapenv:Header>
            <wsse:Security xmlns:wsse="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd">
              <wsse:UsernameToken><wsse:Username>ch1</wsse:Username><wsse:Password>secret</wsse:Password></wsse:UsernameToken>
            </wsse:Security>
          </soapenv:Header>
          <soapenv:Body>
            <ns1:COREEnvelopeRealTimeRequest xmlns:ns1="http://www.caqh.org/SOAP/WSDL/CORERule2.2.0.xsd">
              <PayloadType>X12_270_Request_005010X279A1</PayloadType>
              <ProcessingMode>RealTime</ProcessingMode>
              <PayloadID>f81d4fae-7dec-11d0-a765-00a0c91e6bf6</PayloadID>
              <TimeStamp>2026-10-30T10:20:34Z</TimeStamp>
              <SenderID>CH1</SenderID>
              <ReceiverID>CHOPAYER</ReceiverID>
              <CORERuleVersion>2.2.0</CORERuleVersion>
              <Payload><![CDATA[ISA*00*  ~ST*270*0001~]]></Payload>
            </ns1:COREEnvelopeRealTimeRequest>
          </soapenv:Body>
        </soapenv:Envelope>
        """;

    [Fact]
    public void Parse_ReadsTheEnvelopeFields_AndTheUsernameToken()
    {
        var e = CoreSoapCodec.Parse(Request);

        Assert.Equal(CoreEnvelopeFormat.Soap, e.Format);
        Assert.Equal(CorePayloadTypes.Eligibility270, e.PayloadType);
        Assert.Equal("RealTime", e.ProcessingMode);
        Assert.Equal("f81d4fae-7dec-11d0-a765-00a0c91e6bf6", e.PayloadId);
        Assert.Equal("CH1", e.SenderId);
        Assert.Equal("CHOPAYER", e.ReceiverId);
        Assert.Equal("2.2.0", e.CoreRuleVersion);
        Assert.Equal("ISA*00*  ~ST*270*0001~", e.Payload);
        Assert.Equal("ch1", e.UserName);
        Assert.Equal("secret", e.Password);
    }

    [Fact]
    public void Parse_IgnoresADigestPassword()
    {
        var xml = Request.Replace("<wsse:Password>", "<wsse:Password Type=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest\">");

        Assert.Null(CoreSoapCodec.Parse(xml).Password);
    }

    [Theory]
    [InlineData("not xml")]
    [InlineData("<Envelope/>")]
    [InlineData("<soapenv:Envelope xmlns:soapenv=\"http://www.w3.org/2003/05/soap-envelope\"><soapenv:Body/></soapenv:Envelope>")]
    public void Parse_RejectsWhatIsNotACoreSoapRequest(string xml)
    {
        Assert.Throws<CoreEnvelopeFormatException>(() => CoreSoapCodec.Parse(xml));
    }

    [Fact]
    public void Parse_RefusesADtd()
    {
        var xml = "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]>" + Request;

        Assert.Throws<CoreEnvelopeFormatException>(() => CoreSoapCodec.Parse(xml));
    }

    [Fact]
    public void Write_RoundTripsThePayload_IncludingACdataTerminator()
    {
        var xml = CoreSoapCodec.Write(new CoreEnvelopeResponse
        {
            PayloadType = CorePayloadTypes.Eligibility271,
            PayloadId = "id-1",
            TimeStamp = "2026-10-30T10:20:35Z",
            SenderId = "CHOPAYER",
            ReceiverId = "CH1",
            Payload = "ISA*00~MSG*a]]>b~",
        });

        var doc = System.Xml.Linq.XDocument.Parse(xml);
        var body = doc.Descendants().Single(e => e.Name.LocalName == "COREEnvelopeRealTimeResponse");
        Assert.Equal(CoreRule.XsdNamespace, body.Name.NamespaceName);
        Assert.Equal("ISA*00~MSG*a]]>b~", body.Elements().Single(e => e.Name.LocalName == "Payload").Value);
        Assert.Equal("Success", body.Elements().Single(e => e.Name.LocalName == "ErrorCode").Value);
        Assert.Equal("None", body.Elements().Single(e => e.Name.LocalName == "ErrorMessage").Value);
    }
}

public class CorePasswordHashTests
{
    [Fact]
    public void Verify_AcceptsTheRightPassword_Only()
    {
        var hash = CorePasswordHash.Create("pw", iterations: 1000);

        Assert.True(CorePasswordHash.Verify("pw", hash));
        Assert.False(CorePasswordHash.Verify("pW", hash));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("plain-text-password")]
    [InlineData("pbkdf2-sha256$x$abc$def")]
    [InlineData("pbkdf2-sha256$1000$!!!$!!!")]
    public void Verify_NeverAcceptsAnUnusableStoredValue(string? stored)
    {
        Assert.False(CorePasswordHash.Verify("plain-text-password", stored));
        Assert.False(CorePasswordHash.Verify("dummy-password-for-timing", stored));
    }
}

public class CoreSubmitterAuthenticatorTests
{
    private static readonly string Hash = CorePasswordHash.Create("pw", iterations: 1000);

    private static CoreSubmitterAuthenticator Build(Action<CoreConnectivityOptions>? configure = null)
    {
        var options = new CoreConnectivityOptions
        {
            ClientCertificateHeader = "X-Client-Cert",
            Submitters =
            [
                new CoreSubmitterRegistration
                {
                    SenderId = "CH1", TenantId = "t1", UserName = "ch1", PasswordHash = Hash,
                    OAuthClientIds = ["ch1-client"],
                },
                new CoreSubmitterRegistration { SenderId = "CH2", TenantId = "t2", UserName = "ch2", PasswordHash = Hash, Enabled = false },
            ],
        };
        configure?.Invoke(options);
        return new CoreSubmitterAuthenticator(new StaticMonitor<CoreConnectivityOptions>(options));
    }

    private static CoreEnvelopeRequest Envelope(string sender = "CH1", string? user = "ch1", string? password = "pw") =>
        new() { SenderId = sender, UserName = user, Password = password };

    [Fact]
    public void UserNamePassword_ResolvesTheRegisteredTenant()
    {
        var submitter = Build().Authenticate(new DefaultHttpContext(), Envelope(), "eligibility:check", out _);

        Assert.NotNull(submitter);
        Assert.Equal("t1", submitter!.TenantId);
        Assert.Equal(CoreAuthenticationMethod.UserNamePassword, submitter.Method);
    }

    [Theory]
    [InlineData("CH1", "ch1", "nope")]
    [InlineData("CH1", "other", "pw")]
    [InlineData("CHX", "ch1", "pw")]
    [InlineData("CH2", "ch2", "pw")]
    public void UserNamePassword_WrongOrDisabled_IsRefused(string sender, string user, string password)
    {
        var submitter = Build().Authenticate(new DefaultHttpContext(), Envelope(sender, user, password), "eligibility:check", out var failure);

        Assert.Null(submitter);
        Assert.NotNull(failure);
    }

    [Fact]
    public void NoCredential_IsRefused()
    {
        Assert.Null(Build().Authenticate(new DefaultHttpContext(), Envelope(user: null, password: null), "eligibility:check", out _));
    }

    private static DefaultHttpContext Bearer(string clientId, string tenant, params string[] permissions)
    {
        var claims = new List<Claim> { new(ChoClaimTypes.AuthorizedParty, clientId), new(ChoClaimTypes.TenantId, tenant) };
        claims.AddRange(permissions.Select(p => new Claim(ChoClaimTypes.Permission, p)));
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")) };
        http.Items["TenantId"] = tenant;
        return http;
    }

    [Fact]
    public void BearerToken_OfARegisteredClient_InItsTenant_IsAccepted()
    {
        var submitter = Build().Authenticate(Bearer("ch1-client", "t1", "eligibility:check"), Envelope(user: null, password: null), "eligibility:check", out _);

        Assert.Equal(CoreAuthenticationMethod.BearerToken, submitter!.Method);
    }

    [Theory]
    [InlineData("ch1-client", "t2", "eligibility:check")]
    [InlineData("other-client", "t1", "eligibility:check")]
    [InlineData("ch1-client", "t1", "claims:read")]
    public void BearerToken_OtherTenantClientOrPermission_IsRefused(string client, string tenant, string permission)
    {
        Assert.Null(Build().Authenticate(Bearer(client, tenant, permission), Envelope(user: null, password: null), "eligibility:check", out _));
    }

    [Fact]
    public void BearerToken_WhenTurnedOff_IsRefused()
    {
        var auth = Build(o => o.AllowBearerTokens = false);

        Assert.Null(auth.Authenticate(Bearer("ch1-client", "t1", "eligibility:check"), Envelope(user: null, password: null), "eligibility:check", out _));
    }

    private static (X509Certificate2 Cert, string Pem) Certificate(DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=ch1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cert = request.CreateSelfSigned(notBefore, notAfter);
        return (cert, cert.ExportCertificatePem());
    }

    [Fact]
    public void ClientCertificate_FromTheIngressHeader_MatchesItsThumbprint()
    {
        var (cert, pem) = Certificate(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var thumbprint = Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256)).ToLowerInvariant();
        var auth = Build(o => o.Submitters[0].CertificateThumbprints = [thumbprint]);
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Client-Cert"] = Uri.EscapeDataString(pem);

        var submitter = auth.Authenticate(http, Envelope(user: null, password: null), "eligibility:check", out _);

        Assert.Equal(CoreAuthenticationMethod.ClientCertificate, submitter!.Method);
        Assert.Equal("t1", submitter.TenantId);
    }

    [Fact]
    public void ClientCertificate_NotRegistered_IsRefused_EvenWithAGoodPassword()
    {
        var (_, pem) = Certificate(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Client-Cert"] = Uri.EscapeDataString(pem);

        Assert.Null(Build().Authenticate(http, Envelope(), "eligibility:check", out _));
    }

    [Fact]
    public void ClientCertificate_Expired_IsRefused()
    {
        var (cert, pem) = Certificate(DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(-1));
        var thumbprint = Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256));
        var auth = Build(o => o.Submitters[0].CertificateThumbprints = [thumbprint]);
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Client-Cert"] = Uri.EscapeDataString(pem);

        Assert.Null(auth.Authenticate(http, Envelope(user: null, password: null), "eligibility:check", out _));
    }

    [Fact]
    public void ClientCertificateHeader_IsIgnored_UnlessConfigured()
    {
        var (cert, pem) = Certificate(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var thumbprint = Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256));
        var auth = Build(o => { o.ClientCertificateHeader = null; o.Submitters[0].CertificateThumbprints = [thumbprint]; });
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Client-Cert"] = Uri.EscapeDataString(pem);

        Assert.Null(auth.Authenticate(http, Envelope(user: null, password: null), "eligibility:check", out _));
    }
}

public class CoreEnvelopeValidationTests
{
    private sealed class Handler : ICoreRealTimeHandler
    {
        public string RequestPayloadType => CorePayloadTypes.Eligibility270;
        public Task<CorePayloadResult> HandleAsync(CoreRealTimeRequest request, CancellationToken ct) => throw new NotSupportedException();
        public CorePayloadResult? UnableToRespond(CoreRealTimeRequest request) => null;
    }

    private static readonly CoreRealTimeProcessor Processor = new(
        [new Handler()],
        new CoreSubmitterAuthenticator(new StaticMonitor<CoreConnectivityOptions>(new())),
        new StaticMonitor<CoreConnectivityOptions>(new()),
        NullLogger<CoreRealTimeProcessor>.Instance);

    private static CoreEnvelopeRequest Valid(string payload = "ISA*00~") => new()
    {
        PayloadType = CorePayloadTypes.Eligibility270,
        ProcessingMode = "RealTime",
        PayloadId = Guid.NewGuid().ToString(),
        TimeStamp = "2026-10-30T10:20:34Z",
        SenderId = "CH1",
        ReceiverId = "CHOPAYER",
        CoreRuleVersion = "2.2.0",
        Payload = payload,
    };

    private static string? Code(CoreEnvelopeRequest e, CoreConnectivityOptions? o = null) =>
        Processor.Validate(e, o ?? new CoreConnectivityOptions())?.Code;

    [Fact]
    public void AValidEnvelope_HasNoError() => Assert.Null(Code(Valid()));

    [Fact]
    public void Batch_IsProcessingModeIllegal() =>
        Assert.Equal("ProcessingModeIllegal", Code(new CoreEnvelopeRequest { PayloadType = CorePayloadTypes.Eligibility270, ProcessingMode = "Batch", PayloadId = Guid.NewGuid().ToString(), TimeStamp = "2026-10-30T10:20:34Z", SenderId = "a", ReceiverId = "b", CoreRuleVersion = "2.2.0", Payload = "x" }));

    [Fact]
    public void A276_AtTheEligibilityEndpoint_IsPayloadTypeIllegal()
    {
        var e = Valid();
        Assert.Equal("PayloadTypeIllegal", Code(new CoreEnvelopeRequest { PayloadType = CorePayloadTypes.ClaimStatus276, ProcessingMode = e.ProcessingMode, PayloadId = e.PayloadId, TimeStamp = e.TimeStamp, SenderId = e.SenderId, ReceiverId = e.ReceiverId, CoreRuleVersion = e.CoreRuleVersion, Payload = e.Payload }));
    }

    [Fact]
    public void ANonUuidPayloadId_IsPayloadIDIllegal()
    {
        var e = Valid();
        Assert.Equal("PayloadIDIllegal", Code(new CoreEnvelopeRequest { PayloadType = e.PayloadType, ProcessingMode = e.ProcessingMode, PayloadId = "12345", TimeStamp = e.TimeStamp, SenderId = e.SenderId, ReceiverId = e.ReceiverId, CoreRuleVersion = e.CoreRuleVersion, Payload = e.Payload }));
    }

    [Fact]
    public void AnUnservedReceiver_IsReceiverIDIllegal() =>
        Assert.Equal("ReceiverIDIllegal", Code(Valid(), new CoreConnectivityOptions { ReceiverIds = ["SOMEONE"] }));

    [Fact]
    public void AChecksum_MustMatchThePayload()
    {
        var e = Valid("ISA*00~");
        var good = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes("ISA*00~")));
        Assert.Null(Code(With(e, checksum: good.ToLowerInvariant())));
        Assert.Equal("ChecksumMismatch", Code(With(e, checksum: "00")));
    }

    [Fact]
    public void APayloadLength_MustMatchThePayload()
    {
        var e = Valid("ISA*00~");
        Assert.Null(Code(With(e, length: "7")));
        Assert.Equal("PayloadLengthMismatch", Code(With(e, length: "8")));
    }

    private static CoreEnvelopeRequest With(CoreEnvelopeRequest e, string? checksum = null, string? length = null) => new()
    {
        PayloadType = e.PayloadType, ProcessingMode = e.ProcessingMode, PayloadId = e.PayloadId, TimeStamp = e.TimeStamp,
        SenderId = e.SenderId, ReceiverId = e.ReceiverId, CoreRuleVersion = e.CoreRuleVersion, Payload = e.Payload,
        Checksum = checksum, PayloadLength = length,
    };
}

public class X12999RejectionTests
{
    private const string Edi270 =
        "ISA*00*          *00*          *ZZ*PROVIDER01     *ZZ*CHO            *260309*1230*^*00501*000000401*0*T*:~" +
        "GS*HS*PROVIDER01*CHO*20260309*1230*17*X*005010X279A1~" +
        "ST*270*0042*005010X279A1~BHT*0022*13*REF1*20260309*1230~SE*3*0042~GE*1*17~IEA*1*000000401~";

    [Fact]
    public void Build_RejectsEachSet_AndGoesBackToTheSender()
    {
        var edi = X12999Rejection.Build(Edi270, 12, new DateTime(2026, 10, 30, 9, 5, 0, DateTimeKind.Utc))!;

        Assert.StartsWith("ISA*00*          *00*          *ZZ*CHO            *ZZ*PROVIDER01     *261030*0905*^*00501*000000012*0*T*:~", edi);
        Assert.Contains("GS*FA*CHO*PROVIDER01*20261030*0905*12*X*005010X231A1~", edi);
        Assert.Contains("ST*999*0001*005010X231A1~AK1*HS*17*005010X279A1~AK2*270*0042*005010X279A1~IK5*R*5~AK9*R*1*1*0~SE*6*0001~", edi);
        Assert.EndsWith("GE*1*12~IEA*1*000000012~", edi);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not x12")]
    [InlineData("ISA*00*          *00*          *ZZ*PROVIDER01     *ZZ*CHO            *260309*1230*^*00501*000000401*0*T*:~IEA*0*000000401~")]
    public void Build_NeedsAnIsaAndAGs(string x12) => Assert.Null(X12999Rejection.Build(x12, 1, DateTime.UtcNow));
}

internal sealed class StaticMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
