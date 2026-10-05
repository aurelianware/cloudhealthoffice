using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CloudHealthOffice.NachaTransmission;

/// <summary>
/// A tenant's bank SFTP drop, from tenant-service
/// <c>configuration.paymentControls.nachaTransmission</c>. It never holds a
/// credential: only the names of Key Vault secrets, which must start with
/// <see cref="NachaSecretNames.Prefix"/> for the tenant.
/// </summary>
public sealed class NachaTransmissionSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("host")]
    public string? Host { get; set; }

    [JsonPropertyName("port")]
    public int Port { get; set; } = 22;

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    /// <summary>Key Vault secret name holding the OpenSSH/PEM private key.</summary>
    [JsonPropertyName("privateKeySecretRef")]
    public string? PrivateKeySecretRef { get; set; }

    /// <summary>Key Vault secret name holding the private key's passphrase, or the password when there is no key.</summary>
    [JsonPropertyName("passwordSecretRef")]
    public string? PasswordSecretRef { get; set; }

    /// <summary>
    /// The bank's SSH host key, pinned: <c>SHA256:&lt;base64&gt;</c> as
    /// <c>ssh-keygen -lf</c> prints it. Required; nothing connects without it.
    /// </summary>
    [JsonPropertyName("hostKeyFingerprint")]
    public string? HostKeyFingerprint { get; set; }

    [JsonPropertyName("remoteDirectory")]
    public string? RemoteDirectory { get; set; }

    /// <summary>
    /// Null when the settings can be used to transmit for <paramref name="tenantId"/>,
    /// else why not. Never includes a secret (there is none here to include).
    /// </summary>
    public string? Problem(string tenantId)
    {
        if (!Enabled) return "NACHA transmission is not enabled for this tenant (paymentControls.nachaTransmission.enabled).";
        if (string.IsNullOrWhiteSpace(Host)) return "NACHA transmission has no host.";
        if (Port is < 1 or > 65535) return "NACHA transmission port is out of range.";
        if (string.IsNullOrWhiteSpace(Username)) return "NACHA transmission has no username.";
        if (string.IsNullOrWhiteSpace(HostKeyFingerprint))
            return "NACHA transmission has no hostKeyFingerprint: the bank's SSH host key must be pinned before anything is sent.";
        if (HostKeyPin.Parse(HostKeyFingerprint) == null)
            return "NACHA transmission hostKeyFingerprint must be 'SHA256:<base64>' (ssh-keygen -lf -E sha256).";
        if (string.IsNullOrWhiteSpace(PrivateKeySecretRef) && string.IsNullOrWhiteSpace(PasswordSecretRef))
            return "NACHA transmission has neither privateKeySecretRef nor passwordSecretRef.";
        if (NachaSecretNames.Validate(PrivateKeySecretRef, tenantId, "privateKeySecretRef") is { } keyProblem) return keyProblem;
        if (NachaSecretNames.Validate(PasswordSecretRef, tenantId, "passwordSecretRef") is { } passwordProblem) return passwordProblem;
        if (string.IsNullOrWhiteSpace(RemoteDirectory)) return "NACHA transmission has no remoteDirectory.";
        if (RemoteDirectory.Contains("..", StringComparison.Ordinal)) return "NACHA transmission remoteDirectory may not contain '..'.";
        return null;
    }
}

/// <summary>
/// Key Vault secret names for NACHA transmission credentials. Like
/// trading-partner's <c>tp--{tenant}--</c> rule: a name must start with
/// <c>nacha--{tenantId}--</c>, so a tenant can only point at its own bank
/// credentials, never at another tenant's or at a platform secret.
/// </summary>
public static class NachaSecretNames
{
    private static readonly Regex SecretNameChars = new("^[0-9A-Za-z-]+$", RegexOptions.Compiled);

    public static string Prefix(string tenantId) => $"nacha--{tenantId}--";

    /// <summary>Null when the name is acceptable for the tenant (or absent), else the reason (never the name's value beyond the field).</summary>
    public static string? Validate(string? name, string tenantId, string field)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        var prefix = Prefix(tenantId);
        if (name.Length > 127
            || !SecretNameChars.IsMatch(name)
            || !name.StartsWith(prefix, StringComparison.Ordinal)
            || name.Length == prefix.Length)
        {
            return $"{field} must be a Key Vault secret name (letters, digits and '-', at most 127) that starts with " +
                   $"'{prefix}'. Put the credential in Key Vault and give its name, never the credential itself.";
        }

        return null;
    }
}

/// <summary>A pinned SSH host key: the SHA-256 of the server's public host key blob.</summary>
public static class HostKeyPin
{
    /// <summary>The 32-byte hash from <c>SHA256:base64</c> (padding optional), or null when the text is not one.</summary>
    public static byte[]? Parse(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
            return null;
        var text = fingerprint.Trim();
        if (!text.StartsWith("SHA256:", StringComparison.Ordinal))
            return null;
        var b64 = text["SHA256:".Length..].TrimEnd('=');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        try
        {
            var bytes = Convert.FromBase64String(b64);
            return bytes.Length == 32 ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>OpenSSH's form of a host key blob's fingerprint.</summary>
    public static string Of(byte[] hostKeyBlob)
        => "SHA256:" + Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(hostKeyBlob)).TrimEnd('=');

    /// <summary>Whether the presented host key blob is the pinned one (constant time).</summary>
    public static bool Matches(string? pinned, byte[] hostKeyBlob)
    {
        var expected = Parse(pinned);
        if (expected == null) return false;
        var actual = System.Security.Cryptography.SHA256.HashData(hostKeyBlob);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
