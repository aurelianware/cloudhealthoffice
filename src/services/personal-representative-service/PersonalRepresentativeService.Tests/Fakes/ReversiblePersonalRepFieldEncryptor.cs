using PersonalRepresentativeService.Services;

namespace PersonalRepresentativeService.Tests.Fakes;

/// <summary>
/// Deterministic reversible "encryptor" for controller tests. Prepends a
/// marker so tests can distinguish "raw plaintext" from
/// "plaintext-that-was-re-emitted-after-decryption" on the read path.
/// Tracks calls so tests can assert that decryption occurred on specific
/// fields (e.g. resolver displayName goes through the standard path).
/// </summary>
public sealed class ReversiblePersonalRepFieldEncryptor : IPersonalRepFieldEncryptor
{
    private const string Marker = "enc::";

    public int EncryptCalls { get; private set; }
    public int DecryptCalls { get; private set; }

    public Task<string?> EncryptAsync(string? plaintext, CancellationToken ct = default)
    {
        EncryptCalls++;
        if (string.IsNullOrEmpty(plaintext)) return Task.FromResult<string?>(plaintext);
        return Task.FromResult<string?>(Marker + plaintext);
    }

    public Task<string?> DecryptAsync(string? ciphertext, CancellationToken ct = default)
    {
        DecryptCalls++;
        if (string.IsNullOrEmpty(ciphertext)) return Task.FromResult<string?>(ciphertext);
        if (!ciphertext.StartsWith(Marker, StringComparison.Ordinal))
            return Task.FromResult<string?>(ciphertext);
        return Task.FromResult<string?>(ciphertext[Marker.Length..]);
    }

    public static bool LooksEncrypted(string? s) =>
        !string.IsNullOrEmpty(s) && s.StartsWith(Marker, StringComparison.Ordinal);
}
