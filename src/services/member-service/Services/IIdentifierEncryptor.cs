namespace MemberService.Services;

/// <summary>
/// Field-level encryption for PII identifiers (SSN, MBI, Medicaid IDs).
/// Implementations back the data key with Azure Key Vault via <see cref="CloudHealthOffice.Infrastructure.Configuration.ISecretProvider"/>.
/// </summary>
public interface IIdentifierEncryptor
{
    /// <summary>True when the impl performs real encryption (false = no-op dev shim).</summary>
    bool IsEnabled { get; }

    /// <summary>Encrypt plaintext. Returns ciphertext (opaque string). Null/empty in = null/empty out.</summary>
    Task<string?> EncryptAsync(string? plaintext, CancellationToken ct = default);

    /// <summary>Decrypt ciphertext produced by <see cref="EncryptAsync"/>.</summary>
    Task<string?> DecryptAsync(string? ciphertext, CancellationToken ct = default);
}
