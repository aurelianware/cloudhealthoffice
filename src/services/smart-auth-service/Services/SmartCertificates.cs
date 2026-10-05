using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Server;

namespace SmartAuthService.Services;

/// <summary>One entry of <c>SmartAuth:SigningCertificates</c> or <c>SmartAuth:EncryptionCertificates</c>.</summary>
public sealed class SmartCertificateOptions
{
    /// <summary>
    /// Base64 PKCS#12 (PFX). A Key Vault certificate exposes exactly this as
    /// its secret value, so a Key Vault certificate named
    /// <c>SmartAuth--SigningCertificates--0--Pfx</c> lands here.
    /// </summary>
    public string? Pfx { get; set; }

    /// <summary>Path of a PFX file (e.g. mounted by the Key Vault CSI driver), instead of <see cref="Pfx"/>.</summary>
    public string? Path { get; set; }

    /// <summary>PFX password; empty for Key Vault certificates.</summary>
    public string? Password { get; set; }

    /// <summary>
    /// Loaded and trusted, but not used to sign or encrypt: a signing
    /// certificate in standby is published in the JWKS ahead of its use (or
    /// after it), an encryption certificate in standby still decrypts codes
    /// and refresh tokens. The first entry not in standby is the active one.
    /// </summary>
    public bool Standby { get; set; }
}

/// <summary>A loaded set: every certificate, and the one in use.</summary>
public sealed record SmartCertificateSet(IReadOnlyList<X509Certificate2> All, X509Certificate2 Active);

/// <summary>
/// The OpenIddict server's signing and encryption certificates.
///
/// Outside Development and Testing they must come from configuration (Key
/// Vault): every pod must sign with the same key (resource servers validate
/// against the published JWKS) and decrypt the authorization codes and
/// refresh tokens another pod issued. The per-machine development
/// certificates are used only on Development and Testing hosts, and only
/// when nothing is configured.
/// </summary>
public static class SmartCertificates
{
    public const string SigningSection = "SmartAuth:SigningCertificates";
    public const string EncryptionSection = "SmartAuth:EncryptionCertificates";

    public static bool DevelopmentCertificatesAllowed(IHostEnvironment environment)
        => environment.IsDevelopment() || environment.IsEnvironment("Testing");

    /// <summary>The configured signing set, or null (development certificate) where that is allowed.</summary>
    public static SmartCertificateSet? LoadSigning(IConfiguration configuration, IHostEnvironment environment, TimeProvider? time = null)
        => Load(configuration, environment, SigningSection, X509KeyUsageFlags.DigitalSignature, time ?? TimeProvider.System);

    /// <summary>The configured encryption set, or null (development certificate) where that is allowed.</summary>
    public static SmartCertificateSet? LoadEncryption(IConfiguration configuration, IHostEnvironment environment, TimeProvider? time = null)
        => Load(configuration, environment, EncryptionSection, X509KeyUsageFlags.KeyEncipherment, time ?? TimeProvider.System);

    private static SmartCertificateSet? Load(
        IConfiguration configuration, IHostEnvironment environment, string section,
        X509KeyUsageFlags usage, TimeProvider time)
    {
        var entries = configuration.GetSection(section).GetChildren()
            .Select(child => (Key: child.Path, Options: child.Get<SmartCertificateOptions>() ?? new SmartCertificateOptions()))
            .Where(e => !string.IsNullOrWhiteSpace(e.Options.Pfx) || !string.IsNullOrWhiteSpace(e.Options.Path))
            .ToList();

        if (entries.Count == 0)
        {
            if (DevelopmentCertificatesAllowed(environment))
                return null;
            throw new InvalidOperationException(
                $"{section} is required outside Development/Testing: configure at least one certificate "
                + $"({section}:0:Pfx from Key Vault, or {section}:0:Path). See docs/security/smart-auth-tenancy.md.");
        }

        var now = time.GetUtcNow().UtcDateTime;
        var all = new List<X509Certificate2>();
        X509Certificate2? active = null;
        foreach (var (key, options) in entries)
        {
            var certificate = Read(key, options);
            Check(key, certificate, usage);

            if (certificate.NotAfter.ToUniversalTime() <= now)
            {
                if (!options.Standby)
                    throw new InvalidOperationException($"{key}: certificate {certificate.Thumbprint} expired on {certificate.NotAfter:u}.");
                Console.WriteLine($"smart-auth: skipping expired standby certificate {key} ({certificate.Thumbprint}).");
                continue;
            }

            if (all.Any(c => c.Thumbprint == certificate.Thumbprint))
                throw new InvalidOperationException($"{key}: certificate {certificate.Thumbprint} is configured twice.");

            all.Add(certificate);
            if (!options.Standby && active is null)
            {
                if (certificate.NotBefore.ToUniversalTime() > now)
                    throw new InvalidOperationException($"{key}: certificate {certificate.Thumbprint} is not valid until {certificate.NotBefore:u}; keep it in Standby until then.");
                active = certificate;
            }
        }

        if (active is null)
            throw new InvalidOperationException($"{section}: every certificate is in Standby; exactly one must be in use.");

        return new SmartCertificateSet(all, active);
    }

    private static X509Certificate2 Read(string key, SmartCertificateOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Pfx) && !string.IsNullOrWhiteSpace(options.Path))
            throw new InvalidOperationException($"{key}: set Pfx or Path, not both.");

        byte[] bytes;
        try
        {
            bytes = !string.IsNullOrWhiteSpace(options.Pfx)
                ? Convert.FromBase64String(options.Pfx.Trim())
                : File.ReadAllBytes(options.Path!);
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"{key}: the certificate could not be read ({ex.GetType().Name}).", ex);
        }

        // Keys stay in memory only (no per-user key files on Linux).
        var flags = OperatingSystem.IsWindows() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;
        try
        {
            return new X509Certificate2(bytes, string.IsNullOrEmpty(options.Password) ? null : options.Password, flags);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException($"{key}: not a PKCS#12 certificate, or the password is wrong.", ex);
        }
    }

    private static void Check(string key, X509Certificate2 certificate, X509KeyUsageFlags usage)
    {
        using var rsa = certificate.GetRSAPrivateKey();
        if (rsa is null)
            throw new InvalidOperationException($"{key}: certificate {certificate.Thumbprint} must carry an RSA private key.");
        if (rsa.KeySize < 2048)
            throw new InvalidOperationException($"{key}: certificate {certificate.Thumbprint} has a {rsa.KeySize}-bit key; at least 2048 bits are required.");

        var keyUsage = certificate.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        if (keyUsage is not null && !keyUsage.KeyUsages.HasFlag(usage))
            throw new InvalidOperationException($"{key}: certificate {certificate.Thumbprint} does not allow {usage}.");
    }

    /// <summary>
    /// Adds the configured certificates (or, where allowed and nothing is
    /// configured, the development ones) to the OpenIddict server.
    /// </summary>
    public static OpenIddictServerBuilder AddSmartCertificates(
        this OpenIddictServerBuilder builder, SmartCertificateSet? signing, SmartCertificateSet? encryption)
    {
        if (signing is null) builder.AddDevelopmentSigningCertificate();
        else foreach (var certificate in signing.All) builder.AddSigningCertificate(certificate);

        if (encryption is null) builder.AddDevelopmentEncryptionCertificate();
        else foreach (var certificate in encryption.All) builder.AddEncryptionCertificate(certificate);

        // OpenIddict orders credentials itself (furthest expiry first), which
        // would start signing with a standby certificate the moment it is
        // added. Put the active ones first again, after its own ordering.
        builder.Services.AddSingleton<IPostConfigureOptions<OpenIddictServerOptions>>(
            new ActiveCertificateFirst(signing?.Active.Thumbprint, encryption?.Active.Thumbprint));
        return builder;
    }

    private sealed class ActiveCertificateFirst(string? signing, string? encryption) : IPostConfigureOptions<OpenIddictServerOptions>
    {
        public void PostConfigure(string? name, OpenIddictServerOptions options)
        {
            MoveFirst(options.SigningCredentials, signing, c => c.Key);
            MoveFirst(options.EncryptionCredentials, encryption, c => c.Key);
        }

        private static void MoveFirst<T>(List<T> credentials, string? thumbprint, Func<T, SecurityKey> key)
        {
            if (thumbprint is null) return;
            var index = credentials.FindIndex(c => key(c) is X509SecurityKey x
                && string.Equals(x.Certificate.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase));
            if (index <= 0) return;
            var active = credentials[index];
            credentials.RemoveAt(index);
            credentials.Insert(0, active);
        }
    }
}
