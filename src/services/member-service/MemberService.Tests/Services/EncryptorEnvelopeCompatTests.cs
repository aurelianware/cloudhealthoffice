using System.Security.Cryptography;
using System.Text;
using CloudHealthOffice.Infrastructure.Configuration;
using MemberService.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MemberService.Tests.Services;

/// <summary>
/// The most important tests in the A.7.3 PR: a ciphertext written under
/// pre-A.7.3 code (0x01 envelope, hardcoded key) must still decrypt on
/// post-A.7.3 code using the MemberEncryption:LegacyKeySecretName safety
/// net. And a 0x02 envelope written under one key version must still
/// decrypt as long as that version remains in AcceptedKeyVersions.
/// </summary>
public class EncryptorEnvelopeCompatTests
{
    private sealed class MultiKeySecretProvider : ISecretProvider
    {
        private readonly Dictionary<string, string> _secrets;
        public MultiKeySecretProvider(Dictionary<string, string> secrets) { _secrets = secrets; }
        public Task<string?> GetSecretAsync(string name, CancellationToken ct = default)
            => Task.FromResult(_secrets.TryGetValue(name, out var v) ? v : null);
        public Task<IDictionary<string, string>> GetSecretsAsync(string prefix, CancellationToken ct = default)
            => Task.FromResult<IDictionary<string, string>>(new Dictionary<string, string>(_secrets));
        public Task<bool> HealthCheckAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<string?> GetSecretByVersionAsync(string n, string v, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<SecretVersionInfo>> ListSecretVersionsAsync(string n, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SecretVersionInfo>>(Array.Empty<SecretVersionInfo>());
    }

    private static byte[] Base64UrlDecode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }

    /// <summary>
    /// Build a pre-A.7.3-shaped 0x01 envelope:
    ///   [0x01][12 IV][16 tag][ciphertext]
    /// encrypted under <paramref name="key"/>. This simulates ciphertext
    /// that was written to the database before this PR landed.
    /// </summary>
    private static string ProduceV1Envelope(byte[] key, string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(nonce, plain, cipher, tag);

        var env = new byte[1 + 12 + 16 + cipher.Length];
        env[0] = 0x01;
        Buffer.BlockCopy(nonce, 0, env, 1, 12);
        Buffer.BlockCopy(tag, 0, env, 13, 16);
        Buffer.BlockCopy(cipher, 0, env, 29, cipher.Length);
        return Convert.ToBase64String(env).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    [Fact]
    public async Task Post_A_7_3_Encryptor_Decrypts_Pre_A_7_3_V1_Envelope()
    {
        var legacyKey = RandomNumberGenerator.GetBytes(32);
        var legacySecretName = "member-id-dek";

        var secrets = new MultiKeySecretProvider(new Dictionary<string, string>
        {
            [legacySecretName] = Convert.ToBase64String(legacyKey)
        });

        var options = new MemberEncryptionOptions
        {
            KeySecretPrefix = legacySecretName,
            CurrentKeyVersion = "v2",
            AcceptedKeyVersions = new[] { "v1", "v2" },
            LegacyKeySecretName = legacySecretName,
            EmitLegacyEnvelope = false
        };
        var keys = new RotatingKeyProvider(secrets, NullLogger<RotatingKeyProvider>.Instance);
        var enc = new KeyVaultIdentifierEncryptor(
            keys, secrets, NullLogger<KeyVaultIdentifierEncryptor>.Instance, options);

        // Pre-A.7.3 ciphertext produced directly with the same key.
        var legacyCiphertext = ProduceV1Envelope(legacyKey, "123-45-6789");

        var plain = await enc.DecryptAsync(legacyCiphertext);
        plain.Should().Be("123-45-6789");
    }

    [Fact]
    public async Task V2_Envelope_RoundTrip_EmbedsKeyVersion()
    {
        var v1Key = RandomNumberGenerator.GetBytes(32);
        var v2Key = RandomNumberGenerator.GetBytes(32);
        var secrets = new MultiKeySecretProvider(new Dictionary<string, string>
        {
            ["dek-v1"] = Convert.ToBase64String(v1Key),
            ["dek-v2"] = Convert.ToBase64String(v2Key)
        });
        var options = new MemberEncryptionOptions
        {
            KeySecretPrefix = "dek",
            CurrentKeyVersion = "v2",
            AcceptedKeyVersions = new[] { "v1", "v2" },
            LegacyKeySecretName = null,
            EmitLegacyEnvelope = false
        };
        var keys = new RotatingKeyProvider(secrets, NullLogger<RotatingKeyProvider>.Instance);
        var enc = new KeyVaultIdentifierEncryptor(
            keys, secrets, NullLogger<KeyVaultIdentifierEncryptor>.Instance, options);

        var cipher = await enc.EncryptAsync("123-45-6789");
        cipher.Should().NotBeNull();
        var bytes = Base64UrlDecode(cipher!);
        bytes[0].Should().Be(0x02);
        var keyVerLen = bytes[1];
        var keyVer = Encoding.UTF8.GetString(bytes, 2, keyVerLen);
        keyVer.Should().Be("v2");

        var plain = await enc.DecryptAsync(cipher);
        plain.Should().Be("123-45-6789");
    }

    [Fact]
    public async Task V2_Rotation_OldCiphertextStillDecrypts_NewEmitsNewVersion()
    {
        // Simulates a rotation window: service originally ran with v1
        // current; operator rotates to v2. Old v2-not-yet ciphertexts
        // (which we simulate here by pre-encrypting under v1) must still
        // decrypt; new ciphertexts must use v2.
        var v1Key = RandomNumberGenerator.GetBytes(32);
        var v2Key = RandomNumberGenerator.GetBytes(32);
        var secrets = new MultiKeySecretProvider(new Dictionary<string, string>
        {
            ["dek-v1"] = Convert.ToBase64String(v1Key),
            ["dek-v2"] = Convert.ToBase64String(v2Key)
        });

        // Stage 1: v1 current, encrypt under v1.
        var v1Options = new MemberEncryptionOptions
        {
            KeySecretPrefix = "dek", CurrentKeyVersion = "v1",
            AcceptedKeyVersions = new[] { "v1" },
            LegacyKeySecretName = null, EmitLegacyEnvelope = false
        };
        var v1Keys = new RotatingKeyProvider(secrets, NullLogger<RotatingKeyProvider>.Instance);
        var v1Enc = new KeyVaultIdentifierEncryptor(
            v1Keys, secrets, NullLogger<KeyVaultIdentifierEncryptor>.Instance, v1Options);
        var writtenUnderV1 = await v1Enc.EncryptAsync("before-rotation");

        // Stage 2: rotate to v2, window [v1, v2].
        var v2Options = new MemberEncryptionOptions
        {
            KeySecretPrefix = "dek", CurrentKeyVersion = "v2",
            AcceptedKeyVersions = new[] { "v1", "v2" },
            LegacyKeySecretName = null, EmitLegacyEnvelope = false
        };
        var v2Keys = new RotatingKeyProvider(secrets, NullLogger<RotatingKeyProvider>.Instance);
        var v2Enc = new KeyVaultIdentifierEncryptor(
            v2Keys, secrets, NullLogger<KeyVaultIdentifierEncryptor>.Instance, v2Options);

        // Old ciphertext still decrypts.
        (await v2Enc.DecryptAsync(writtenUnderV1)).Should().Be("before-rotation");

        // New ciphertext carries v2.
        var writtenUnderV2 = await v2Enc.EncryptAsync("after-rotation");
        var bytes = Base64UrlDecode(writtenUnderV2!);
        var keyVer = Encoding.UTF8.GetString(bytes, 2, bytes[1]);
        keyVer.Should().Be("v2");
    }

    [Fact]
    public async Task StaleEncryptionKey_WhenAcceptedVersionMissingFromSecretProvider()
    {
        // v1 accepted (in envelope) but the secret provider no longer has
        // dek-v1 (e.g. an operator accidentally deleted it).
        var v2Key = RandomNumberGenerator.GetBytes(32);
        var secretsWithoutV1 = new MultiKeySecretProvider(new Dictionary<string, string>
        {
            ["dek-v2"] = Convert.ToBase64String(v2Key)
        });
        var options = new MemberEncryptionOptions
        {
            KeySecretPrefix = "dek", CurrentKeyVersion = "v2",
            AcceptedKeyVersions = new[] { "v1", "v2" },
            LegacyKeySecretName = null, EmitLegacyEnvelope = false
        };
        var keys = new RotatingKeyProvider(secretsWithoutV1, NullLogger<RotatingKeyProvider>.Instance);
        var enc = new KeyVaultIdentifierEncryptor(
            keys, secretsWithoutV1, NullLogger<KeyVaultIdentifierEncryptor>.Instance, options);

        // Hand-craft a v1-tagged envelope (arbitrary contents — won't reach
        // decrypt because the key lookup must fail first).
        var envelope = new byte[1 + 1 + 2 + 12 + 16 + 4];
        envelope[0] = 0x02;
        envelope[1] = 2;
        envelope[2] = (byte)'v'; envelope[3] = (byte)'1';
        var b64 = Convert.ToBase64String(envelope).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var act = async () => await enc.DecryptAsync(b64);
        var ex = await act.Should().ThrowAsync<StaleEncryptionKeyException>();
        ex.Which.KeyVersion.Should().Be("v1");
    }
}
