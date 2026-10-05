using System.Security.Cryptography;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.TokenService.Signing;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CloudHealthOffice.TokenService.Tests;

public sealed class SigningTests
{
    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "token-service";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Theory]
    [InlineData("RS256")]
    [InlineData("ES256")]
    public async Task Remote_signing_key_produces_tokens_that_verify_with_the_public_key(string alg)
    {
        // Stands in for Key Vault: the signer receives only a SHA-256 digest.
        AsymmetricSecurityKey publicKey;
        Func<byte[], byte[]> signDigest;
        if (alg == "RS256")
        {
            var rsa = RSA.Create(2048);
            publicKey = new RsaSecurityKey(rsa.ExportParameters(false));
            signDigest = d => rsa.SignHash(d, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        else
        {
            var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            publicKey = new ECDsaSecurityKey(PemKeys.PublicOnly(ec));
            signDigest = d => ec.SignHash(d); // IEEE P1363 (r||s), as Key Vault returns
        }
        publicKey.KeyId = PemKeys.Thumbprint(publicKey);

        var remote = new RemoteSigningKey(publicKey, alg, signDigest);
        var credentials = new SigningCredentials(remote, alg) { CryptoProviderFactory = remote.CryptoProviderFactory };
        var token = new ChoTokenIssuer("cho-token-service", "cho-api", credentials, TimeSpan.FromMinutes(5))
            .IssueUserToken("user-1", "acme", ["ClaimsExaminer"]);

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = "cho-token-service",
            ValidAudience = "cho-api",
            IssuerSigningKey = ChoKeyFromPem(PemKeys.PublicKeyPem(PemKeys.PublicJwk(publicKey))),
        });

        result.IsValid.Should().BeTrue(result.Exception?.Message);
        new JsonWebToken(token).Alg.Should().Be(alg);
        new JsonWebToken(token).Kid.Should().Be(publicKey.KeyId);
    }

    private static SecurityKey ChoKeyFromPem(string pem)
    {
        try
        {
            var ec = ECDsa.Create();
            ec.ImportFromPem(pem);
            return new ECDsaSecurityKey(ec);
        }
        catch (CryptographicException)
        {
            var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            return new RsaSecurityKey(rsa);
        }
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Symmetric_keys_are_refused_outside_development_and_testing(string environment)
    {
        var act = () => TokenSigningSetup.Create(
            new TokenSigningOptions { SymmetricKey = ChoDevelopmentAuth.SymmetricKey }, new Env(environment));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Development or Testing*");
    }

    [Fact]
    public void Pem_keys_are_refused_in_production()
    {
        var pem = ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportPkcs8PrivateKeyPem();
        var act = () => TokenSigningSetup.Create(new TokenSigningOptions { PrivateKeyPem = pem }, new Env("Production"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*KeyVaultKeyId*");
    }

    [Fact]
    public async Task Pem_keys_are_allowed_in_staging_and_publish_only_public_material()
    {
        var pem = RSA.Create(2048).ExportPkcs8PrivateKeyPem();
        var source = TokenSigningSetup.Create(new TokenSigningOptions { PrivateKeyPem = pem }, new Env("Staging"));

        var material = await source.GetAsync(CancellationToken.None);
        material.PublicJwk.Should().NotBeNull();
        material.PublicJwk!.D.Should().BeNull();
        material.PublicJwk.P.Should().BeNull();
        material.Credentials.Algorithm.Should().Be(SecurityAlgorithms.RsaSha256);
    }

    [Fact]
    public async Task Development_symmetric_key_is_allowed_in_development()
    {
        var source = TokenSigningSetup.Create(
            new TokenSigningOptions { Issuer = ChoDevelopmentAuth.UserIssuer, SymmetricKey = ChoDevelopmentAuth.SymmetricKey },
            new Env("Development"));

        (await source.GetAsync(CancellationToken.None)).PublicJwk.Should().BeNull();
    }

    [Fact]
    public void No_key_configured_fails_startup()
    {
        var act = () => TokenSigningSetup.Create(new TokenSigningOptions(), new Env("Production"));
        act.Should().Throw<InvalidOperationException>();
    }
}
