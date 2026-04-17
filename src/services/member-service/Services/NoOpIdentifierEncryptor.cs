namespace MemberService.Services;

/// <summary>
/// No-op identifier encryptor for dev / test environments with no Key Vault.
/// Passes values through unchanged. Never register in production.
/// </summary>
public sealed class NoOpIdentifierEncryptor : IIdentifierEncryptor
{
    public bool IsEnabled => false;

    public Task<string?> EncryptAsync(string? plaintext, CancellationToken ct = default)
        => Task.FromResult(plaintext);

    public Task<string?> DecryptAsync(string? ciphertext, CancellationToken ct = default)
        => Task.FromResult(ciphertext);
}
