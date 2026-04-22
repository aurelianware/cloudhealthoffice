namespace PersonalRepresentativeService.Services;

/// <summary>
/// Encrypts and decrypts PHI-adjacent fields on a Personal Representative
/// record (name, contact, address, relationship notes). Null/empty values
/// pass through unchanged so partial records — a rep created without a
/// phone number, for example — do not force spurious ciphertext.
/// </summary>
public interface IPersonalRepFieldEncryptor
{
    Task<string?> EncryptAsync(string? plaintext, CancellationToken ct = default);
    Task<string?> DecryptAsync(string? ciphertext, CancellationToken ct = default);
}
