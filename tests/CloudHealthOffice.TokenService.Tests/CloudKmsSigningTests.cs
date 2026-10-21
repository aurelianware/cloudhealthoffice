using System.Security.Cryptography;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.TokenService.Signing;
using Google.Cloud.Kms.V1;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Alg = Google.Cloud.Kms.V1.CryptoKeyVersion.Types.CryptoKeyVersionAlgorithm;

namespace CloudHealthOffice.TokenService.Tests;

/// <summary>
/// Stands in for Cloud KMS: holds a local key, returns its public key as KMS
/// does (SubjectPublicKeyInfo PEM) and signs digests the way KMS returns them
/// (DER for ECDSA, PKCS#1 v1.5 for RSA).
/// </summary>
public sealed class FakeCloudKms : ICloudKmsSigner
{
    public const string KeyVersion =
        "projects/cho-prod/locations/us-west1/keyRings/cho/cryptoKeys/token-signing/cryptoKeyVersions/1";

    private readonly ECDsa? _ec;
    private readonly RSA? _rsa;
    private readonly Alg _algorithm;

    public FakeCloudKms(Alg algorithm)
    {
        _algorithm = algorithm;
        if (algorithm == Alg.EcSignP256Sha256)
            _ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        else
            _rsa = RSA.Create(2048);
    }

    public int SignCalls { get; private set; }
    public List<string> NamesSeen { get; } = new();

    public Task<CloudKmsPublicKey> GetPublicKeyAsync(string keyVersionName, CancellationToken ct)
    {
        NamesSeen.Add(keyVersionName);
        var pem = _ec != null ? _ec.ExportSubjectPublicKeyInfoPem() : _rsa!.ExportSubjectPublicKeyInfoPem();
        return Task.FromResult(new CloudKmsPublicKey(pem, _algorithm));
    }

    public byte[] SignDigest(string keyVersionName, byte[] sha256Digest)
    {
        SignCalls++;
        NamesSeen.Add(keyVersionName);
        sha256Digest.Should().HaveCount(32, "Cloud KMS is sent only a SHA-256 digest");
        return _ec != null
            ? _ec.SignHash(sha256Digest, DSASignatureFormat.Rfc3279DerSequence)
            : _rsa!.SignHash(sha256Digest, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }
}

public sealed class CloudKmsSigningTests
{
    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "token-service";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Theory]
    [InlineData(Alg.EcSignP256Sha256, "ES256")]
    [InlineData(Alg.RsaSignPkcs12048Sha256, "RS256")]
    public async Task Production_tokens_are_signed_inside_cloud_kms_and_verify_with_the_published_key(Alg kmsAlgorithm, string jwsAlgorithm)
    {
        var kms = new FakeCloudKms(kmsAlgorithm);
        var source = TokenSigningSetup.Create(
            new TokenSigningOptions { CloudKmsKeyVersion = FakeCloudKms.KeyVersion }, new Env("Production"), kms);

        var material = await source.GetAsync(CancellationToken.None);
        var token = new ChoTokenIssuer("cho-token-service", "cho-api", material.Credentials, TimeSpan.FromMinutes(5))
            .IssueUserToken("user-1", "acme", ["ClaimsExaminer"]);

        // Verify only with what token-service publishes at /.well-known/jwks.json.
        var jwk = material.PublicJwk!;
        jwk.D.Should().BeNull();
        jwk.P.Should().BeNull();
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = "cho-token-service",
            ValidAudience = "cho-api",
            IssuerSigningKey = jwk,
        });

        result.IsValid.Should().BeTrue(result.Exception?.Message);
        new JsonWebToken(token).Alg.Should().Be(jwsAlgorithm);
        new JsonWebToken(token).Kid.Should().Be(jwk.KeyId);
        kms.SignCalls.Should().Be(1);
        kms.NamesSeen.Should().OnlyContain(n => n == FakeCloudKms.KeyVersion);
    }

    [Fact]
    public async Task The_public_key_is_read_once()
    {
        var kms = new FakeCloudKms(Alg.EcSignP256Sha256);
        var source = TokenSigningSetup.Create(
            new TokenSigningOptions { CloudKmsKeyVersion = FakeCloudKms.KeyVersion }, new Env("Production"), kms);

        var first = await source.GetAsync(CancellationToken.None);
        var second = await source.GetAsync(CancellationToken.None);

        second.Should().BeSameAs(first);
        kms.NamesSeen.Should().HaveCount(1);
    }

    [Theory]
    [InlineData("projects/p/locations/l/keyRings/r/cryptoKeys/k")]
    [InlineData("projects/p/locations/l/keyRings/r/cryptoKeys/k/cryptoKeyVersions/latest")]
    [InlineData("token-signing")]
    public void A_key_version_must_be_explicit(string name)
    {
        var act = () => TokenSigningSetup.Create(
            new TokenSigningOptions { CloudKmsKeyVersion = name }, new Env("Production"), new FakeCloudKms(Alg.EcSignP256Sha256));

        act.Should().Throw<InvalidOperationException>().WithMessage("*cryptoKeyVersions*");
    }

    [Fact]
    public void Key_vault_and_cloud_kms_cannot_both_be_set()
    {
        var act = () => TokenSigningSetup.Create(
            new TokenSigningOptions
            {
                KeyVaultKeyId = "https://cho.vault.azure.net/keys/token-signing",
                CloudKmsKeyVersion = FakeCloudKms.KeyVersion,
            },
            new Env("Production"),
            new FakeCloudKms(Alg.EcSignP256Sha256));

        act.Should().Throw<InvalidOperationException>().WithMessage("*only one*");
    }

    [Fact]
    public async Task Unsupported_kms_algorithms_are_refused()
    {
        var kms = new UnsupportedKms();
        var source = TokenSigningSetup.Create(
            new TokenSigningOptions { CloudKmsKeyVersion = FakeCloudKms.KeyVersion }, new Env("Production"), kms);

        var act = async () => await source.GetAsync(CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not supported*");
    }

    [Fact]
    public void Der_signatures_convert_to_fixed_width_r_and_s()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var digest = SHA256.HashData("cho"u8);
        for (var i = 0; i < 50; i++)
        {
            // Many signatures, so r and s with leading zero bytes and high bits are covered.
            var der = ec.SignHash(digest, DSASignatureFormat.Rfc3279DerSequence);
            var p1363 = CloudKmsSigningMaterialSource.DerToP1363(der, 32);

            p1363.Should().HaveCount(64);
            ec.VerifyHash(digest, p1363, DSASignatureFormat.IeeeP1363FixedFieldConcatenation).Should().BeTrue();
        }
    }

    private sealed class UnsupportedKms : ICloudKmsSigner
    {
        public Task<CloudKmsPublicKey> GetPublicKeyAsync(string keyVersionName, CancellationToken ct)
            => Task.FromResult(new CloudKmsPublicKey(
                ECDsa.Create(ECCurve.NamedCurves.nistP384).ExportSubjectPublicKeyInfoPem(), Alg.EcSignP384Sha384));

        public byte[] SignDigest(string keyVersionName, byte[] sha256Digest) => throw new NotSupportedException();
    }
}
