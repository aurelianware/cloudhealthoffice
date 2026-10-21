using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Google.Cloud.Kms.V1;
using Google.Protobuf;
using Microsoft.IdentityModel.Tokens;

namespace CloudHealthOffice.TokenService.Signing;

/// <summary>The public half of a Cloud KMS key version and the algorithm KMS reports for it.</summary>
public sealed record CloudKmsPublicKey(string Pem, CryptoKeyVersion.Types.CryptoKeyVersionAlgorithm Algorithm);

/// <summary>
/// The two Cloud KMS operations token-service needs. The production
/// implementation is <see cref="GoogleCloudKmsSigner"/>; tests substitute a
/// local key so the signing path is exercised without Google Cloud.
/// </summary>
public interface ICloudKmsSigner
{
    Task<CloudKmsPublicKey> GetPublicKeyAsync(string keyVersionName, CancellationToken ct);

    /// <summary>
    /// Signs a SHA-256 digest with the key version and returns the signature as
    /// Cloud KMS returns it: DER for ECDSA, PKCS#1 v1.5 bytes for RSA.
    /// </summary>
    byte[] SignDigest(string keyVersionName, byte[] sha256Digest);
}

/// <summary>
/// Cloud KMS through Application Default Credentials. On GKE that is the pod's
/// Workload Identity, which needs <c>roles/cloudkms.signerVerifier</c> on the key
/// (sign) and <c>roles/cloudkms.publicKeyViewer</c> (read the public key).
/// </summary>
public sealed class GoogleCloudKmsSigner : ICloudKmsSigner
{
    private readonly Lazy<KeyManagementServiceClient> _client = new(KeyManagementServiceClient.Create);

    public async Task<CloudKmsPublicKey> GetPublicKeyAsync(string keyVersionName, CancellationToken ct)
    {
        var key = await _client.Value.GetPublicKeyAsync(new GetPublicKeyRequest { Name = keyVersionName }, ct);
        if (!string.Equals(key.Name, keyVersionName, StringComparison.Ordinal))
            throw new CryptographicException("Cloud KMS returned the public key of a different key version.");
        return new CloudKmsPublicKey(key.Pem, key.Algorithm);
    }

    public byte[] SignDigest(string keyVersionName, byte[] sha256Digest)
    {
        var response = _client.Value.AsymmetricSign(new AsymmetricSignRequest
        {
            Name = keyVersionName,
            Digest = new Digest { Sha256 = ByteString.CopyFrom(sha256Digest) },
        });
        // KMS echoes the version that signed; anything else must not be published under our kid.
        if (!string.Equals(response.Name, keyVersionName, StringComparison.Ordinal))
            throw new CryptographicException("Cloud KMS signed with a different key version.");
        return response.Signature.ToByteArray();
    }
}

/// <summary>
/// Signs CHO tokens through Cloud KMS's asymmetric sign operation. The public
/// key is read once on first use; every signature is a call to Cloud KMS, so
/// the private key never leaves it. The Google Cloud counterpart of
/// <see cref="KeyVaultSigningMaterialSource"/>.
/// </summary>
public sealed class CloudKmsSigningMaterialSource : ISigningMaterialSource
{
    private static readonly Regex KeyVersionName = new(
        @"^projects/[^/]+/locations/[^/]+/keyRings/[^/]+/cryptoKeys/[^/]+/cryptoKeyVersions/[0-9]+$",
        RegexOptions.CultureInvariant);

    private const int P256FieldSize = 32;

    private readonly string _keyVersionName;
    private readonly ICloudKmsSigner _kms;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SigningMaterial? _material;

    public CloudKmsSigningMaterialSource(string keyVersionName, ICloudKmsSigner kms)
    {
        _keyVersionName = keyVersionName;
        _kms = kms;
    }

    /// <summary>Whether a value is a full, explicit Cloud KMS key version name.</summary>
    public static bool IsKeyVersionName(string value) => KeyVersionName.IsMatch(value);

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
        var key = await _kms.GetPublicKeyAsync(_keyVersionName, ct);

        AsymmetricSecurityKey publicKey;
        string algorithm;
        Func<byte[], byte[]> sign;
        switch (key.Algorithm)
        {
            case CryptoKeyVersion.Types.CryptoKeyVersionAlgorithm.EcSignP256Sha256:
            {
                var ec = ECDsa.Create();
                ec.ImportFromPem(key.Pem);
                publicKey = new ECDsaSecurityKey(ec);
                algorithm = SecurityAlgorithms.EcdsaSha256;
                // JWS ES256 carries R || S (RFC 7518 §3.4); Cloud KMS returns DER.
                sign = digest => DerToP1363(_kms.SignDigest(_keyVersionName, digest), P256FieldSize);
                break;
            }
            case CryptoKeyVersion.Types.CryptoKeyVersionAlgorithm.RsaSignPkcs12048Sha256:
            case CryptoKeyVersion.Types.CryptoKeyVersionAlgorithm.RsaSignPkcs13072Sha256:
            case CryptoKeyVersion.Types.CryptoKeyVersionAlgorithm.RsaSignPkcs14096Sha256:
            {
                var rsa = RSA.Create();
                rsa.ImportFromPem(key.Pem);
                publicKey = new RsaSecurityKey(rsa);
                algorithm = SecurityAlgorithms.RsaSha256;
                sign = digest => _kms.SignDigest(_keyVersionName, digest);
                break;
            }
            default:
                throw new InvalidOperationException(
                    $"Cloud KMS key algorithm {key.Algorithm} is not supported; use EC_SIGN_P256_SHA256 (ES256) " +
                    "or RSA_SIGN_PKCS1_2048/3072/4096_SHA256 (RS256).");
        }

        publicKey.KeyId = PemKeys.Thumbprint(publicKey);
        var signingKey = new RemoteSigningKey(publicKey, algorithm, sign);

        return new SigningMaterial(
            new SigningCredentials(signingKey, algorithm) { CryptoProviderFactory = signingKey.CryptoProviderFactory },
            PemKeys.PublicJwk(publicKey));
    }

    /// <summary>
    /// Converts a DER ECDSA signature (SEQUENCE { INTEGER r, INTEGER s }) to the
    /// fixed-width r || s form JWS requires.
    /// </summary>
    internal static byte[] DerToP1363(byte[] der, int fieldSize)
    {
        var reader = new AsnReader(der, AsnEncodingRules.DER);
        var sequence = reader.ReadSequence();
        var r = sequence.ReadIntegerBytes().Span;
        var s = sequence.ReadIntegerBytes().Span;
        sequence.ThrowIfNotEmpty();
        reader.ThrowIfNotEmpty();

        var result = new byte[fieldSize * 2];
        CopyUnsigned(r, result.AsSpan(0, fieldSize));
        CopyUnsigned(s, result.AsSpan(fieldSize, fieldSize));
        return result;
    }

    private static void CopyUnsigned(ReadOnlySpan<byte> integer, Span<byte> destination)
    {
        // DER integers are signed: a leading 0x00 keeps a high-bit value positive.
        while (integer.Length > 1 && integer[0] == 0)
            integer = integer[1..];
        if (integer.Length > destination.Length)
            throw new CryptographicException("ECDSA signature component is larger than the curve field.");
        destination.Clear();
        integer.CopyTo(destination[(destination.Length - integer.Length)..]);
    }
}
