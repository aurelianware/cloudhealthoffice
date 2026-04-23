using AppealsService.Services;

namespace AppealsService.Tests.Fakes;

/// <summary>
/// Deterministic reversible "encryptor" for controller tests. Prepends a
/// marker so tests can distinguish "raw plaintext" from
/// "plaintext-that-was-re-emitted-after-decryption" on the read path, and
/// assert that stored state looks encrypted at rest.
/// </summary>
public sealed class ReversibleAppealFieldEncryptor : IAppealFieldEncryptor
{
    private const string Marker = "enc::";

    public Task<string?> EncryptAsync(string? plaintext, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(plaintext)) return Task.FromResult<string?>(plaintext);
        return Task.FromResult<string?>(Marker + plaintext);
    }

    public Task<string?> DecryptAsync(string? ciphertext, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(ciphertext)) return Task.FromResult<string?>(ciphertext);
        if (!ciphertext.StartsWith(Marker, StringComparison.Ordinal))
            return Task.FromResult<string?>(ciphertext);
        return Task.FromResult<string?>(ciphertext[Marker.Length..]);
    }

    public static bool LooksEncrypted(string? s) =>
        !string.IsNullOrEmpty(s) && s.StartsWith(Marker, StringComparison.Ordinal);
}
