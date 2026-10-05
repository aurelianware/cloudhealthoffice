using System.Security.Cryptography;
using Azure.Identity;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Keys.Cryptography;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.IdentityModel.Tokens;
using JsonWebKey = Microsoft.IdentityModel.Tokens.JsonWebKey;

namespace CloudHealthOffice.TokenService.Signing;

/// <summary>
/// Bound from <c>TokenSigning</c>. Exactly one key source is used, chosen in
/// this order: <see cref="KeyVaultKeyId"/>, <see cref="PrivateKeyPem"/>,
/// <see cref="SymmetricKey"/>.
/// </summary>
public sealed class TokenSigningOptions
{
    public const string SectionName = "TokenSigning";

    /// <summary>The <c>iss</c> written into CHO tokens. Services trust it in <c>ChoAuth:Issuers</c>.</summary>
    public string Issuer { get; set; } = "cho-token-service";

    /// <summary>The <c>aud</c> written into CHO tokens.</summary>
    public string Audience { get; set; } = ChoDevelopmentAuth.Audience;

    /// <summary>
    /// Key Vault key identifier (https://{vault}.vault.azure.net/keys/{name}[/{version}]).
    /// RSA (RS256) or EC P-256 (ES256). Signing happens inside Key Vault; the
    /// private key never reaches this process. Without a version, the version
    /// current at startup is pinned until the next restart.
    /// </summary>
    public string? KeyVaultKeyId { get; set; }

    /// <summary>Client id of a user-assigned managed identity for Key Vault, if not the default.</summary>
    public string? ManagedIdentityClientId { get; set; }

    /// <summary>PEM private key (RSA or EC P-256). Not permitted in Production.</summary>
    public string? PrivateKeyPem { get; set; }

    /// <summary>Base64 HMAC key. Development and Testing only.</summary>
    public string? SymmetricKey { get; set; }
}

/// <summary>Key material ready to sign CHO tokens.</summary>
public sealed class SigningMaterial
{
    public SigningMaterial(SigningCredentials credentials, JsonWebKey? publicJwk)
    {
        Credentials = credentials;
        PublicJwk = publicJwk;
    }

    public SigningCredentials Credentials { get; }

    /// <summary>The public verification key, or null for a symmetric key (never published).</summary>
    public JsonWebKey? PublicJwk { get; }
}

public interface ISigningMaterialSource
{
    ValueTask<SigningMaterial> GetAsync(CancellationToken ct);
}

public static class TokenSigningSetup
{
    /// <summary>
    /// Picks the key source and refuses the ones not allowed on this host:
    /// symmetric keys outside Development/Testing (as the shared ChoAuth layer
    /// does), and PEM keys in Production, which must sign through Key Vault.
    /// </summary>
    public static ISigningMaterialSource Create(TokenSigningOptions options, IHostEnvironment environment)
    {
        if (string.IsNullOrWhiteSpace(options.Issuer) || string.IsNullOrWhiteSpace(options.Audience))
            throw new InvalidOperationException($"{TokenSigningOptions.SectionName}:Issuer and Audience are required.");

        var allowSymmetric = environment.IsDevelopment() || environment.IsEnvironment("Testing");

        if (!string.IsNullOrWhiteSpace(options.KeyVaultKeyId))
            return new KeyVaultSigningMaterialSource(options);

        if (!string.IsNullOrWhiteSpace(options.PrivateKeyPem))
        {
            if (environment.IsProduction())
                throw new InvalidOperationException(
                    $"{TokenSigningOptions.SectionName}:PrivateKeyPem is not permitted in Production. " +
                    $"Set {TokenSigningOptions.SectionName}:KeyVaultKeyId so tokens are signed inside Key Vault.");
            var credentials = PemKeys.SigningCredentials(options.PrivateKeyPem);
            return new StaticSigningMaterialSource(new SigningMaterial(credentials, PemKeys.PublicJwk(credentials.Key)));
        }

        if (!string.IsNullOrWhiteSpace(options.SymmetricKey))
        {
            if (!allowSymmetric)
                throw new InvalidOperationException(
                    $"{TokenSigningOptions.SectionName}:SymmetricKey is permitted only on a Development or Testing host. " +
                    "Use KeyVaultKeyId (or PrivateKeyPem outside Production).");
            return new StaticSigningMaterialSource(new SigningMaterial(
                new SigningCredentials(new SymmetricSecurityKey(Convert.FromBase64String(options.SymmetricKey)),
                    SecurityAlgorithms.HmacSha256), null));
        }

        throw new InvalidOperationException(
            $"No CHO token signing key is configured. Set {TokenSigningOptions.SectionName}:KeyVaultKeyId.");
    }
}

public sealed class StaticSigningMaterialSource(SigningMaterial material) : ISigningMaterialSource
{
    public ValueTask<SigningMaterial> GetAsync(CancellationToken ct) => ValueTask.FromResult(material);
}

internal static class PemKeys
{
    public static SigningCredentials SigningCredentials(string pem)
    {
        var ec = ECDsa.Create();
        try
        {
            ec.ImportFromPem(pem);
            if (ec.KeySize != 256)
                throw new InvalidOperationException("Only EC P-256 (ES256) keys are supported.");
            var key = new ECDsaSecurityKey(ec);
            key.KeyId = Thumbprint(key);
            return new SigningCredentials(key, SecurityAlgorithms.EcdsaSha256);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            ec.Dispose();
        }

        var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        var rsaKey = new RsaSecurityKey(rsa);
        rsaKey.KeyId = Thumbprint(rsaKey);
        return new SigningCredentials(rsaKey, SecurityAlgorithms.RsaSha256);
    }

    /// <summary>The public half of an asymmetric key as a JWK carrying the key id.</summary>
    public static JsonWebKey PublicJwk(SecurityKey key)
    {
        JsonWebKey jwk = key switch
        {
            RsaSecurityKey rsa => JsonWebKeyConverter.ConvertFromRSASecurityKey(
                new RsaSecurityKey(rsa.Rsa?.ExportParameters(false) ?? rsa.Parameters)),
            ECDsaSecurityKey ec => JsonWebKeyConverter.ConvertFromECDsaSecurityKey(
                new ECDsaSecurityKey(PublicOnly(ec.ECDsa))),
            _ => throw new InvalidOperationException("Only RSA and EC keys have a public JWK."),
        };
        jwk.KeyId = key.KeyId;
        jwk.Use = "sig";
        jwk.Alg = key is ECDsaSecurityKey ? SecurityAlgorithms.EcdsaSha256 : SecurityAlgorithms.RsaSha256;
        // Never publish private parameters.
        jwk.D = jwk.P = jwk.Q = jwk.DP = jwk.DQ = jwk.QI = null;
        return jwk;
    }

    /// <summary>RFC 7638 thumbprint of the public key, used as the <c>kid</c>.</summary>
    public static string Thumbprint(AsymmetricSecurityKey key)
    {
        JsonWebKey jwk = key switch
        {
            RsaSecurityKey rsa => JsonWebKeyConverter.ConvertFromRSASecurityKey(
                new RsaSecurityKey(rsa.Rsa?.ExportParameters(false) ?? rsa.Parameters)),
            ECDsaSecurityKey ec => JsonWebKeyConverter.ConvertFromECDsaSecurityKey(new ECDsaSecurityKey(PublicOnly(ec.ECDsa))),
            _ => throw new InvalidOperationException("Unsupported key type."),
        };
        return Base64UrlEncoder.Encode(jwk.ComputeJwkThumbprint());
    }

    public static ECDsa PublicOnly(ECDsa ec)
    {
        var pub = ECDsa.Create();
        pub.ImportParameters(ec.ExportParameters(false));
        return pub;
    }

    /// <summary>The public key in SubjectPublicKeyInfo PEM form, for <c>ChoAuth:Issuers:n:PublicKeyPem</c>.</summary>
    public static string PublicKeyPem(JsonWebKey jwk)
    {
        if (jwk.Kty == JsonWebAlgorithmsKeyTypes.EllipticCurve)
        {
            using var ec = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = Base64UrlEncoder.DecodeBytes(jwk.X), Y = Base64UrlEncoder.DecodeBytes(jwk.Y) },
            });
            return ec.ExportSubjectPublicKeyInfoPem();
        }

        using var rsa = RSA.Create(new RSAParameters
        {
            Modulus = Base64UrlEncoder.DecodeBytes(jwk.N),
            Exponent = Base64UrlEncoder.DecodeBytes(jwk.E),
        });
        return rsa.ExportSubjectPublicKeyInfoPem();
    }
}

/// <summary>
/// Signs through Azure Key Vault's sign operation. The key's public half is
/// read once (and its version pinned) on first use; every signature is a call
/// to Key Vault, so the private key never leaves it.
/// </summary>
public sealed class KeyVaultSigningMaterialSource : ISigningMaterialSource
{
    private readonly TokenSigningOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SigningMaterial? _material;

    public KeyVaultSigningMaterialSource(TokenSigningOptions options) => _options = options;

    public async ValueTask<SigningMaterial> GetAsync(CancellationToken ct)
    {
        if (_material != null)
            return _material;

        await _gate.WaitAsync(ct);
        try
        {
            return _material ??= await LoadAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SigningMaterial> LoadAsync(CancellationToken ct)
    {
        var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = string.IsNullOrWhiteSpace(_options.ManagedIdentityClientId)
                ? null
                : _options.ManagedIdentityClientId,
        });

        var id = new KeyVaultKeyIdentifier(new Uri(_options.KeyVaultKeyId!));
        var keyClient = new KeyClient(id.VaultUri, credential);
        KeyVaultKey key = await keyClient.GetKeyAsync(id.Name, id.Version, ct);

        // Pin the exact version: the kid we publish must match the key that signs.
        var crypto = new CryptographyClient(key.Id, credential);

        AsymmetricSecurityKey publicKey;
        string algorithm;
        SignatureAlgorithm kvAlgorithm;
        if (key.KeyType == KeyType.Rsa || key.KeyType == KeyType.RsaHsm)
        {
            publicKey = new RsaSecurityKey(key.Key.ToRSA(includePrivateParameters: false));
            algorithm = SecurityAlgorithms.RsaSha256;
            kvAlgorithm = SignatureAlgorithm.RS256;
        }
        else if ((key.KeyType == KeyType.Ec || key.KeyType == KeyType.EcHsm) && key.Key.CurveName == KeyCurveName.P256)
        {
            publicKey = new ECDsaSecurityKey(key.Key.ToECDsa(includePrivateParameters: false));
            algorithm = SecurityAlgorithms.EcdsaSha256;
            kvAlgorithm = SignatureAlgorithm.ES256;
        }
        else
        {
            throw new InvalidOperationException(
                $"Key Vault key type {key.KeyType} is not supported; use RSA (RS256) or EC P-256 (ES256).");
        }

        publicKey.KeyId = PemKeys.Thumbprint(publicKey);
        var signingKey = new RemoteSigningKey(publicKey, algorithm,
            digest => crypto.Sign(kvAlgorithm, digest).Signature);

        return new SigningMaterial(
            new SigningCredentials(signingKey, algorithm) { CryptoProviderFactory = signingKey.CryptoProviderFactory },
            PemKeys.PublicJwk(publicKey));
    }
}

/// <summary>
/// An asymmetric key whose private half lives elsewhere: signing hashes the
/// JWS input with SHA-256 and hands the digest to <c>signDigest</c> (Key
/// Vault's sign operation in production, a local key in tests).
/// </summary>
public sealed class RemoteSigningKey : AsymmetricSecurityKey
{
    private readonly AsymmetricSecurityKey _publicKey;

    public RemoteSigningKey(AsymmetricSecurityKey publicKey, string algorithm, Func<byte[], byte[]> signDigest)
    {
        if (algorithm != SecurityAlgorithms.RsaSha256 && algorithm != SecurityAlgorithms.EcdsaSha256)
            throw new ArgumentException("Only RS256 and ES256 are supported.", nameof(algorithm));
        _publicKey = publicKey;
        Algorithm = algorithm;
        SignDigest = signDigest;
        KeyId = publicKey.KeyId;
        CryptoProviderFactory = new RemoteCryptoProviderFactory();
    }

    public string Algorithm { get; }
    internal Func<byte[], byte[]> SignDigest { get; }

    [Obsolete("HasPrivateKey method is deprecated, please use PrivateKeyStatus.")]
    public override bool HasPrivateKey => true;

    public override PrivateKeyStatus PrivateKeyStatus => PrivateKeyStatus.Exists;

    public override int KeySize => _publicKey.KeySize;

    private sealed class RemoteCryptoProviderFactory : CryptoProviderFactory
    {
        public override bool IsSupportedAlgorithm(string algorithm, SecurityKey key)
            => key is RemoteSigningKey remote && remote.Algorithm == algorithm;

        public override SignatureProvider CreateForSigning(SecurityKey key, string algorithm)
            => CreateForSigning(key, algorithm, cacheProvider: false);

        public override SignatureProvider CreateForSigning(SecurityKey key, string algorithm, bool cacheProvider)
        {
            if (key is not RemoteSigningKey remote || remote.Algorithm != algorithm)
                throw new NotSupportedException($"Remote signing supports only {algorithm} with its own key.");
            return new RemoteSignatureProvider(remote);
        }

        public override SignatureProvider CreateForVerifying(SecurityKey key, string algorithm)
            => throw new NotSupportedException("A remote signing key does not verify; use its public key.");

        public override void ReleaseSignatureProvider(SignatureProvider signatureProvider)
            => signatureProvider.Dispose();
    }

    private sealed class RemoteSignatureProvider : SignatureProvider
    {
        private readonly RemoteSigningKey _key;

        public RemoteSignatureProvider(RemoteSigningKey key) : base(key, key.Algorithm) => _key = key;

        public override byte[] Sign(byte[] input) => _key.SignDigest(SHA256.HashData(input));

        public override byte[] Sign(byte[] input, int offset, int count)
            => _key.SignDigest(SHA256.HashData(input.AsSpan(offset, count)));

        public override bool Sign(ReadOnlySpan<byte> data, Span<byte> destination, out int bytesWritten)
        {
            var signature = _key.SignDigest(SHA256.HashData(data));
            if (signature.Length > destination.Length)
            {
                bytesWritten = 0;
                return false;
            }
            signature.CopyTo(destination);
            bytesWritten = signature.Length;
            return true;
        }

        public override bool Verify(byte[] input, byte[] signature)
            => throw new NotSupportedException("A remote signing key does not verify; use its public key.");

        public override bool Verify(byte[] input, int inputOffset, int inputLength, byte[] signature, int signatureOffset, int signatureLength)
            => throw new NotSupportedException("A remote signing key does not verify; use its public key.");

        protected override void Dispose(bool disposing)
        {
        }
    }
}
