namespace ConsentService.Services;

/// <summary>
/// Encrypts and decrypts PHI-adjacent free-text fields on a consent record
/// (<c>Reason</c>, <c>GrantedToName</c>, <c>GrantedToContact</c>,
/// <c>Purpose</c>). Null/empty values pass through unchanged so partial
/// records — consent created without a purpose, for example — do not
/// force spurious ciphertext.
/// </summary>
public interface IConsentFieldEncryptor
{
    Task<string?> EncryptAsync(string? plaintext, CancellationToken ct = default);
    Task<string?> DecryptAsync(string? ciphertext, CancellationToken ct = default);
}
