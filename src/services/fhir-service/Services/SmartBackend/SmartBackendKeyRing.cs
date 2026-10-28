using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace FhirService.Services.SmartBackend;

/// <summary>One loaded signing key: what signs, and what is published.</summary>
public sealed record SmartBackendSigningKey(string KeyId, string Algorithm, SecurityKey Key, JsonObject PublicJwk, bool Standby);

/// <summary>
/// CHO's client-assertion signing keys, loaded once from
/// <see cref="SmartBackendClientOptions.SigningKeys"/> and checked strictly:
/// an unreadable key, a key too weak or of the wrong type for its algorithm,
/// a duplicate <c>kid</c>, or a set with no active key stops the service at
/// startup rather than at the first Payer-to-Payer call.
///
/// The published JWKS is built field by field from the public parameters, so
/// no private component can reach it whatever the key type.
/// </summary>
public sealed class SmartBackendKeyRing
{
    private static readonly HashSet<string> SupportedAlgorithms = new(StringComparer.Ordinal)
    {
        SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512,
    };

    public SmartBackendKeyRing(IOptions<SmartBackendClientOptions> options)
    {
        var keys = new List<SmartBackendSigningKey>();
        for (var i = 0; i < options.Value.SigningKeys.Count; i++)
        {
            var entry = options.Value.SigningKeys[i];
            if (string.IsNullOrWhiteSpace(entry.PrivateKeyPem) && string.IsNullOrWhiteSpace(entry.Path)) continue;

            var key = Load($"{SmartBackendClientOptions.SectionName}:SigningKeys:{i}", entry);
            if (keys.Any(k => k.KeyId == key.KeyId))
                throw new InvalidOperationException(
                    $"{SmartBackendClientOptions.SectionName}:SigningKeys:{i}: kid '{key.KeyId}' is used by another key.");
            keys.Add(key);
        }

        All = keys;
        Active = keys.FirstOrDefault(k => !k.Standby);
        if (keys.Count > 0 && Active is null)
            throw new InvalidOperationException(
                $"{SmartBackendClientOptions.SectionName}:SigningKeys: every key is in Standby; exactly one must sign.");
    }

    /// <summary>Every loaded key, active and standby.</summary>
    public IReadOnlyList<SmartBackendSigningKey> All { get; }

    /// <summary>The key that signs, or null when none is configured.</summary>
    public SmartBackendSigningKey? Active { get; }

    /// <summary>The public JWK Set, or null when no key is configured.</summary>
    public JsonObject? PublicJwks()
    {
        if (All.Count == 0) return null;
        var keys = new JsonArray();
        foreach (var key in All) keys.Add(key.PublicJwk.DeepClone());
        return new JsonObject { ["keys"] = keys };
    }

    private static SmartBackendSigningKey Load(string path, SmartBackendSigningKeyOptions entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.PrivateKeyPem) && !string.IsNullOrWhiteSpace(entry.Path))
            throw new InvalidOperationException($"{path}: set PrivateKeyPem or Path, not both.");

        var pem = !string.IsNullOrWhiteSpace(entry.PrivateKeyPem)
            ? entry.PrivateKeyPem
            : File.ReadAllText(entry.Path!);

        if (pem.Contains("BEGIN EC PRIVATE KEY", StringComparison.Ordinal) || IsEcPkcs8(pem))
            return LoadEc(path, pem, entry);
        return LoadRsa(path, pem, entry);
    }

    private static SmartBackendSigningKey LoadRsa(string path, string pem, SmartBackendSigningKeyOptions entry)
    {
        var rsa = RSA.Create();
        try { rsa.ImportFromPem(pem); }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            rsa.Dispose();
            throw new InvalidOperationException($"{path}: not a readable RSA or EC private key in PEM form.");
        }

        if (rsa.KeySize < 2048)
            throw new InvalidOperationException($"{path}: RSA keys must be at least 2048 bits (this one is {rsa.KeySize}).");

        var algorithm = string.IsNullOrWhiteSpace(entry.Algorithm) ? SecurityAlgorithms.RsaSha384 : entry.Algorithm.Trim();
        if (!SupportedAlgorithms.Contains(algorithm) || !algorithm.StartsWith("RS", StringComparison.Ordinal))
            throw new InvalidOperationException($"{path}: algorithm '{algorithm}' does not fit an RSA key; use RS384.");

        var parameters = rsa.ExportParameters(includePrivateParameters: false);
        var n = Base64UrlEncoder.Encode(parameters.Modulus);
        var e = Base64UrlEncoder.Encode(parameters.Exponent);
        var kid = KeyIdOrThumbprint(entry, $"{{\"e\":\"{e}\",\"kty\":\"RSA\",\"n\":\"{n}\"}}");

        var jwk = new JsonObject
        {
            ["kty"] = "RSA", ["kid"] = kid, ["use"] = "sig", ["alg"] = algorithm, ["n"] = n, ["e"] = e,
        };
        return new SmartBackendSigningKey(kid, algorithm, new RsaSecurityKey(rsa) { KeyId = kid }, jwk, entry.Standby);
    }

    private static SmartBackendSigningKey LoadEc(string path, string pem, SmartBackendSigningKeyOptions entry)
    {
        var ec = ECDsa.Create();
        try { ec.ImportFromPem(pem); }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            ec.Dispose();
            throw new InvalidOperationException($"{path}: not a readable EC private key in PEM form.");
        }

        var parameters = ec.ExportParameters(includePrivateParameters: false);
        var (crv, impliedAlgorithm) = ec.KeySize switch
        {
            256 => ("P-256", SecurityAlgorithms.EcdsaSha256),
            384 => ("P-384", SecurityAlgorithms.EcdsaSha384),
            521 => ("P-521", SecurityAlgorithms.EcdsaSha512),
            _ => throw new InvalidOperationException($"{path}: EC keys must be on P-256, P-384 or P-521."),
        };

        var algorithm = string.IsNullOrWhiteSpace(entry.Algorithm) ? impliedAlgorithm : entry.Algorithm.Trim();
        if (algorithm != impliedAlgorithm)
            throw new InvalidOperationException($"{path}: algorithm '{algorithm}' does not fit a {crv} key; use {impliedAlgorithm}.");

        var x = Base64UrlEncoder.Encode(parameters.Q.X);
        var y = Base64UrlEncoder.Encode(parameters.Q.Y);
        var kid = KeyIdOrThumbprint(entry, $"{{\"crv\":\"{crv}\",\"kty\":\"EC\",\"x\":\"{x}\",\"y\":\"{y}\"}}");

        var jwk = new JsonObject
        {
            ["kty"] = "EC", ["kid"] = kid, ["use"] = "sig", ["alg"] = algorithm, ["crv"] = crv, ["x"] = x, ["y"] = y,
        };
        return new SmartBackendSigningKey(kid, algorithm, new ECDsaSecurityKey(ec) { KeyId = kid }, jwk, entry.Standby);
    }

    /// <summary>A PKCS#8 "PRIVATE KEY" block holding an EC key (RSA otherwise).</summary>
    private static bool IsEcPkcs8(string pem)
    {
        if (!pem.Contains("BEGIN PRIVATE KEY", StringComparison.Ordinal)) return false;
        using var probe = ECDsa.Create();
        try { probe.ImportFromPem(pem); return true; }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException) { return false; }
    }

    /// <summary>The configured kid, or the RFC 7638 thumbprint of the required members (already in lexical order).</summary>
    private static string KeyIdOrThumbprint(SmartBackendSigningKeyOptions entry, string canonicalJwk)
        => string.IsNullOrWhiteSpace(entry.KeyId)
            ? Base64UrlEncoder.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJwk)))
            : entry.KeyId.Trim();
}
