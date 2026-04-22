using System.Security.Cryptography;
using CloudHealthOffice.Infrastructure.Configuration;
using MemberService.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MemberService.Tests.Services;

public class IdentifierEncryptorTests
{
    private sealed class StaticSecretProvider : ISecretProvider
    {
        private readonly string _secret;
        public StaticSecretProvider(string secret) { _secret = secret; }
        public Task<string?> GetSecretAsync(string name, CancellationToken ct = default) => Task.FromResult<string?>(_secret);
        public Task<IDictionary<string, string>> GetSecretsAsync(string prefix, CancellationToken ct = default)
            => Task.FromResult<IDictionary<string, string>>(new Dictionary<string, string>());
        public Task<bool> HealthCheckAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<string?> GetSecretByVersionAsync(string name, string version, CancellationToken ct = default)
            => Task.FromResult<string?>(_secret);
        public Task<IReadOnlyList<SecretVersionInfo>> ListSecretVersionsAsync(string name, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SecretVersionInfo>>(Array.Empty<SecretVersionInfo>());
    }

    private static KeyVaultIdentifierEncryptor MakeEncryptor(
        ISecretProvider secrets,
        string keySecretName = "cho-member-id-dek",
        bool legacyEnvelope = true)
    {
        var options = new MemberEncryptionOptions
        {
            KeySecretPrefix = keySecretName,
            CurrentKeyVersion = "v1",
            AcceptedKeyVersions = new[] { "v1" },
            LegacyKeySecretName = keySecretName,
            EmitLegacyEnvelope = legacyEnvelope
        };
        var keys = new RotatingKeyProvider(secrets, NullLogger<RotatingKeyProvider>.Instance);
        return new KeyVaultIdentifierEncryptor(
            keys, secrets, NullLogger<KeyVaultIdentifierEncryptor>.Instance, options);
    }

    [Fact]
    public async Task NoOp_PassesThroughValues()
    {
        var enc = new NoOpIdentifierEncryptor();
        enc.IsEnabled.Should().BeFalse();
        (await enc.EncryptAsync("abc")).Should().Be("abc");
        (await enc.DecryptAsync("abc")).Should().Be("abc");
        (await enc.EncryptAsync(null)).Should().BeNull();
    }

    [Fact]
    public async Task KeyVaultEncryptor_RoundTripsPlaintext()
    {
        var keyBytes = RandomNumberGenerator.GetBytes(32);
        var enc = MakeEncryptor(new StaticSecretProvider(Convert.ToBase64String(keyBytes)));

        var cipher = await enc.EncryptAsync("123-45-6789");
        cipher.Should().NotBeNullOrEmpty();
        cipher.Should().NotBe("123-45-6789");

        var plain = await enc.DecryptAsync(cipher);
        plain.Should().Be("123-45-6789");
    }

    [Fact]
    public async Task KeyVaultEncryptor_EmptyIn_EmptyOut()
    {
        var enc = MakeEncryptor(new StaticSecretProvider(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))), keySecretName: "k");
        (await enc.EncryptAsync("")).Should().Be("");
        (await enc.DecryptAsync("")).Should().Be("");
    }

    [Fact]
    public async Task KeyVaultEncryptor_WrongKeyLength_Throws()
    {
        var enc = MakeEncryptor(new StaticSecretProvider("short"), keySecretName: "k");
        var act = async () => await enc.EncryptAsync("anything");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task KeyVaultEncryptor_MissingKey_Throws()
    {
        var provider = new Moq.Mock<ISecretProvider>();
        provider.Setup(p => p.GetSecretAsync(Moq.It.IsAny<string>(), Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        var enc = MakeEncryptor(provider.Object, keySecretName: "k");
        var act = async () => await enc.EncryptAsync("anything");
        await act.Should().ThrowAsync<CryptographicException>();
    }

    [Fact]
    public async Task KeyVaultEncryptor_TamperedCiphertext_ThrowsCrypto()
    {
        var keyBytes = RandomNumberGenerator.GetBytes(32);
        var enc = MakeEncryptor(new StaticSecretProvider(Convert.ToBase64String(keyBytes)), keySecretName: "k");
        var cipher = await enc.EncryptAsync("secret-mbi");
        cipher.Should().NotBeNull();
        // Flip a byte in the middle.
        var chars = cipher!.ToCharArray();
        chars[^5] = chars[^5] == 'A' ? 'B' : 'A';
        var tampered = new string(chars);

        var act = async () => await enc.DecryptAsync(tampered);
        await act.Should().ThrowAsync<CryptographicException>();
    }
}
