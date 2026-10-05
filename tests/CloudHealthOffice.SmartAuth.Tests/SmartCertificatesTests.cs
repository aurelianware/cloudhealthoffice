using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Bson;
using MongoDB.Driver;
using SmartAuthService.Services;
using Env = CloudHealthOffice.SmartAuth.Tests.ExternalLoginConfigurationTests.Env;

namespace CloudHealthOffice.SmartAuth.Tests;

/// <summary>
/// Token signing and encryption certificates: configured (Key Vault) outside
/// Development/Testing, every signing certificate published in the JWKS, and
/// the active one — not OpenIddict's choice — signing.
/// </summary>
[Collection(SmartAuthCollection.Name)]
public class SmartCertificatesTests
{
    private readonly SmartAuthTestFixture _fixture;

    public SmartCertificatesTests(SmartAuthTestFixture fixture) => _fixture = fixture;

    public static string Pfx(DateTimeOffset? notAfter = null, int bits = 2048,
        X509KeyUsageFlags usage = X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
        DateTimeOffset? notBefore = null, bool withPrivateKey = true)
    {
        using var rsa = RSA.Create(bits);
        var request = new CertificateRequest("CN=smart-auth-test-" + Guid.NewGuid().ToString("N")[..6], rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, critical: true));
        using var certificate = request.CreateSelfSigned(
            notBefore ?? DateTimeOffset.UtcNow.AddDays(-1), notAfter ?? DateTimeOffset.UtcNow.AddYears(1));
        if (!withPrivateKey)
        {
            using var publicOnly = new X509Certificate2(certificate.Export(X509ContentType.Cert));
            return Convert.ToBase64String(publicOnly.Export(X509ContentType.Pkcs12));
        }
        return Convert.ToBase64String(certificate.Export(X509ContentType.Pkcs12));
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => KeyValuePair.Create(v.Key, v.Value))).Build();

    // ── Startup ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void OutsideDevelopment_NoCertificates_RefusesToStart(string environment)
    {
        var signing = () => SmartCertificates.LoadSigning(Config(), new Env(environment));
        signing.Should().Throw<InvalidOperationException>().WithMessage("*SmartAuth:SigningCertificates*");
        var encryption = () => SmartCertificates.LoadEncryption(Config(), new Env(environment));
        encryption.Should().Throw<InvalidOperationException>().WithMessage("*SmartAuth:EncryptionCertificates*");
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void DevelopmentAndTesting_FallBackToDevelopmentCertificates(string environment)
    {
        SmartCertificates.LoadSigning(Config(), new Env(environment)).Should().BeNull();
        SmartCertificates.LoadEncryption(Config(), new Env(environment)).Should().BeNull();
    }

    [Fact]
    public void Host_OutsideDevelopment_WithoutCertificates_FailsToStart()
    {
        var host = new WebApplicationFactory<SmartAuthService.Program>().WithWebHostBuilder(b => b.UseEnvironment("Staging"));
        var act = () => host.CreateClient();
        act.Should().Throw<InvalidOperationException>().WithMessage("*SmartAuth:SigningCertificates*");
    }

    public static TheoryData<string, string> BadCertificates => new()
    {
        { "not base64", "%%%" },
        { "not a pfx", Convert.ToBase64String("hello"u8.ToArray()) },
        { "no private key", Pfx(withPrivateKey: false) },
        { "1024-bit key", Pfx(bits: 1024) },
        { "expired", Pfx(notBefore: DateTimeOffset.UtcNow.AddYears(-2), notAfter: DateTimeOffset.UtcNow.AddDays(-1)) },
        { "not yet valid", Pfx(notBefore: DateTimeOffset.UtcNow.AddDays(3)) },
        { "no signature usage", Pfx(usage: X509KeyUsageFlags.KeyEncipherment) },
    };

    [Theory]
    [MemberData(nameof(BadCertificates))]
    public void ABadActiveCertificate_RefusesToStart(string why, string pfx)
    {
        var act = () => SmartCertificates.LoadSigning(Config(("SmartAuth:SigningCertificates:0:Pfx", pfx)), new Env("Production"));
        act.Should().Throw<InvalidOperationException>(why).WithMessage("*SmartAuth:SigningCertificates:0*");
    }

    [Fact]
    public void EveryCertificateInStandby_RefusesToStart()
    {
        var act = () => SmartCertificates.LoadSigning(Config(
            ("SmartAuth:SigningCertificates:0:Pfx", Pfx()),
            ("SmartAuth:SigningCertificates:0:Standby", "true")), new Env("Production"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*Standby*");
    }

    [Fact]
    public void TheFirstCertificateNotInStandby_IsActive_AndAnExpiredStandbyIsSkipped()
    {
        var set = SmartCertificates.LoadSigning(Config(
            ("SmartAuth:SigningCertificates:0:Pfx", Pfx(notBefore: DateTimeOffset.UtcNow.AddYears(-2), notAfter: DateTimeOffset.UtcNow.AddDays(-1))),
            ("SmartAuth:SigningCertificates:0:Standby", "true"),
            ("SmartAuth:SigningCertificates:1:Pfx", Pfx()),
            ("SmartAuth:SigningCertificates:1:Standby", "true"),
            ("SmartAuth:SigningCertificates:2:Pfx", Pfx())), new Env("Production"))!;

        set.All.Should().HaveCount(2);
        set.Active.Should().BeSameAs(set.All[1]);
    }

    [Fact]
    public void APfxFile_CanBeUsedInsteadOfBase64()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pfx");
        File.WriteAllBytes(path, Convert.FromBase64String(Pfx()));
        try
        {
            SmartCertificates.LoadEncryption(Config(("SmartAuth:EncryptionCertificates:0:Path", path)), new Env("Production"))!
                .All.Should().ContainSingle();
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── JWKS and the signer ───────────────────────────────────────────────────

    private WebApplicationFactory<SmartAuthService.Program> Host(string database, params (string Key, string Value)[] settings)
        => _fixture.Factory.WithWebHostBuilder(b =>
        {
            // Own database: its Data Protection keys are encrypted with these certificates.
            b.UseSetting("MongoDb:DatabaseName", database);
            foreach (var (k, v) in settings) b.UseSetting(k, v);
        });

    private static string Modulus(string pfx)
    {
        using var certificate = new X509Certificate2(Convert.FromBase64String(pfx));
        return Base64UrlEncoder.Encode(certificate.GetRSAPublicKey()!.ExportParameters(false).Modulus);
    }

    private static async Task<JsonWebKeySet> JwksAsync(WebApplicationFactory<SmartAuthService.Program> host)
        => new(await host.CreateClient().GetStringAsync("/.well-known/jwks"));

    private static async Task<string> SignerModulusAsync(WebApplicationFactory<SmartAuthService.Program> host)
    {
        var smart = new SmartAuthDriver(host);
        var tenant = SmartAuthDriver.NewTenant();
        var (clientId, secret) = await smart.RegisterClientAsync(tenant, "backend", "system/*.read");
        var (status, body) = await smart.ClientCredentialsAsync(clientId, secret!, "system/*.read");
        status.Should().Be(HttpStatusCode.OK, body.ToString());
        var kid = SmartAuthDriver.Read(body.GetProperty("access_token").GetString()!).Header.Kid;
        return (await JwksAsync(host)).Keys.Single(k => k.Kid == kid).N;
    }

    [Fact]
    public async Task ConfiguredCertificate_IsTheOnlyKeyInTheJwks_AndSignsTokens()
    {
        var pfx = Pfx();
        await using var host = Host("certs-" + Guid.NewGuid().ToString("N")[..8],
            ("SmartAuth:SigningCertificates:0:Pfx", pfx),
            ("SmartAuth:EncryptionCertificates:0:Pfx", Pfx()));

        (await JwksAsync(host)).Keys.Select(k => k.N).Should().Equal(Modulus(pfx));
        (await SignerModulusAsync(host)).Should().Be(Modulus(pfx));
    }

    [Fact]
    public async Task Rotation_BothCertificatesArePublished_AndTheActiveOneSigns_EvenWhenTheStandbyExpiresLater()
    {
        // OpenIddict on its own would sign with the certificate that expires
        // last, i.e. start using "next" the moment it is published.
        var current = Pfx(notAfter: DateTimeOffset.UtcNow.AddMonths(6));
        var next = Pfx(notAfter: DateTimeOffset.UtcNow.AddYears(2));
        var database = "certs-" + Guid.NewGuid().ToString("N")[..8];

        await using (var staged = Host(database,
            ("SmartAuth:SigningCertificates:0:Pfx", current),
            ("SmartAuth:SigningCertificates:1:Pfx", next),
            ("SmartAuth:SigningCertificates:1:Standby", "true")))
        {
            (await JwksAsync(staged)).Keys.Select(k => k.N).Should().BeEquivalentTo([Modulus(current), Modulus(next)]);
            (await SignerModulusAsync(staged)).Should().Be(Modulus(current));
        }

        await using var promoted = Host(database,
            ("SmartAuth:SigningCertificates:0:Pfx", current),
            ("SmartAuth:SigningCertificates:0:Standby", "true"),
            ("SmartAuth:SigningCertificates:1:Pfx", next));
        (await JwksAsync(promoted)).Keys.Select(k => k.N).Should().BeEquivalentTo([Modulus(current), Modulus(next)]);
        (await SignerModulusAsync(promoted)).Should().Be(Modulus(next));
    }

    [Fact]
    public async Task ConfiguredEncryptionCertificates_LetAnotherPodRedeemTheCode_AndEncryptTheSharedKeyRing()
    {
        var database = "certs-" + Guid.NewGuid().ToString("N")[..8];
        var settings = new[]
        {
            ("SmartAuth:SigningCertificates:0:Pfx", Pfx()),
            ("SmartAuth:EncryptionCertificates:0:Pfx", Pfx()),
        };
        await using var podA = Host(database, settings);
        await using var podB = Host(database, settings);

        var smartA = new SmartAuthDriver(podA);
        var tenant = SmartAuthDriver.NewTenant();
        var (app, _) = await smartA.RegisterClientAsync(tenant, "patient-app", "openid", "launch/patient", "patient/*.read");
        var browser = await smartA.LinkedMemberAsync(tenant, "pat-enc");
        var outcome = await SmartAuthDriver.AuthorizeAsync(browser, app, "openid launch/patient patient/*.read");
        outcome.Code.Should().NotBeNull();

        // The code was encrypted by pod A; pod B decrypts it with the same certificate.
        var (status, body) = await new SmartAuthDriver(podB).ExchangeCodeAsync(app, outcome);
        status.Should().Be(HttpStatusCode.OK, body.ToString());

        var keys = await podA.Services.GetRequiredService<IMongoDatabase>()
            .GetCollection<BsonDocument>(MongoDataProtectionRepository.CollectionName)
            .Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
        keys.Should().NotBeEmpty();
        keys.Should().OnlyContain(k => k["xml"].AsString.Contains("EncryptedData"),
            "Data Protection keys are stored encrypted with the encryption certificate");
    }
}
