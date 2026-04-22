using System.Security.Cryptography;
using CloudHealthOffice.Infrastructure.Configuration;
using ConsentService.Services;
using ConsentService.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConsentService.Tests.Services;

public class ConsentFieldEncryptorTests
{
    private const string Prefix = "consent-body-encryption-key";

    private static string Key32Base64(byte fill)
    {
        var bytes = Enumerable.Repeat(fill, 32).ToArray();
        return Convert.ToBase64String(bytes);
    }

    private static (ConsentFieldEncryptor enc, DictionarySecretProvider secrets)
        Build(ConsentEncryptionOptions options, Action<DictionarySecretProvider>? seed = null)
    {
        var secrets = new DictionarySecretProvider();
        seed?.Invoke(secrets);
        var keys = new RotatingKeyProvider(secrets, NullLogger<RotatingKeyProvider>.Instance);
        var enc = new ConsentFieldEncryptor(keys, NullLogger<ConsentFieldEncryptor>.Instance, options);
        return (enc, secrets);
    }

    [Fact]
    public async Task RoundTrip_V1_ReturnsOriginalPlaintext()
    {
        var (enc, _) = Build(
            new ConsentEncryptionOptions
            {
                KeySecretPrefix = Prefix,
                CurrentKeyVersion = "v1",
                AcceptedKeyVersions = new[] { "v1" }
            },
            s => s.Secrets[$"{Prefix}-v1"] = Key32Base64(0xAA));

        var plaintext = "for continuity of care — Dr. Smith, 2026-04-22";
        var ct = await enc.EncryptAsync(plaintext);
        ct.Should().NotBeNull();
        ct.Should().NotBe(plaintext);

        var back = await enc.DecryptAsync(ct);
        back.Should().Be(plaintext);
    }

    [Fact]
    public async Task RotationWindow_ReadsV1AndWritesV2()
    {
        // Encrypt under v1, rotate to v2 (keeping v1 accepted), confirm the
        // v1 ciphertext still decrypts and new writes go under v2.
        var opts1 = new ConsentEncryptionOptions
        {
            KeySecretPrefix = Prefix,
            CurrentKeyVersion = "v1",
            AcceptedKeyVersions = new[] { "v1" }
        };
        var (encV1, secrets) = Build(opts1, s => s.Secrets[$"{Prefix}-v1"] = Key32Base64(0x01));
        var ctV1 = await encV1.EncryptAsync("hello");

        // Stand up a second encryptor sharing the same secret store but with
        // v2 as current and both versions accepted.
        secrets.Secrets[$"{Prefix}-v2"] = Key32Base64(0x02);
        var opts2 = new ConsentEncryptionOptions
        {
            KeySecretPrefix = Prefix,
            CurrentKeyVersion = "v2",
            AcceptedKeyVersions = new[] { "v1", "v2" }
        };
        var keys2 = new RotatingKeyProvider(secrets, NullLogger<RotatingKeyProvider>.Instance);
        var encV2 = new ConsentFieldEncryptor(keys2, NullLogger<ConsentFieldEncryptor>.Instance, opts2);

        (await encV2.DecryptAsync(ctV1)).Should().Be("hello");

        var ctV2 = await encV2.EncryptAsync("world");
        // The envelope changes even for the same plaintext: different key,
        // random IV. "hello" and "world" are different anyway — this just
        // ensures no exception is thrown on v2 emit.
        ctV2.Should().NotBeNull();
        (await encV2.DecryptAsync(ctV2)).Should().Be("world");
    }

    [Fact]
    public async Task StaleKey_DroppedFromAccepted_ThrowsCryptographicException()
    {
        // Encrypt under v1, then drop v1 from AcceptedKeyVersions so the
        // stored ciphertext is no longer decryptable.
        var optsWide = new ConsentEncryptionOptions
        {
            KeySecretPrefix = Prefix,
            CurrentKeyVersion = "v1",
            AcceptedKeyVersions = new[] { "v1" }
        };
        var (encWide, secrets) = Build(optsWide,
            s => s.Secrets[$"{Prefix}-v1"] = Key32Base64(0x11));
        var ct = await encWide.EncryptAsync("secret");

        var optsNarrow = new ConsentEncryptionOptions
        {
            KeySecretPrefix = Prefix,
            CurrentKeyVersion = "v2",
            AcceptedKeyVersions = new[] { "v2" }
        };
        secrets.Secrets[$"{Prefix}-v2"] = Key32Base64(0x22);
        var keys = new RotatingKeyProvider(secrets, NullLogger<RotatingKeyProvider>.Instance);
        var encNarrow = new ConsentFieldEncryptor(keys, NullLogger<ConsentFieldEncryptor>.Instance, optsNarrow);

        var act = async () => await encNarrow.DecryptAsync(ct);
        await act.Should().ThrowAsync<CryptographicException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task NullOrEmpty_PassThrough(string? input)
    {
        var (enc, _) = Build(new ConsentEncryptionOptions
        {
            KeySecretPrefix = Prefix,
            CurrentKeyVersion = "v1",
            AcceptedKeyVersions = new[] { "v1" }
        });
        (await enc.EncryptAsync(input)).Should().Be(input);
        (await enc.DecryptAsync(input)).Should().Be(input);
    }

    [Fact]
    public async Task TamperedCiphertext_ThrowsCryptographicException()
    {
        var (enc, _) = Build(
            new ConsentEncryptionOptions
            {
                KeySecretPrefix = Prefix,
                CurrentKeyVersion = "v1",
                AcceptedKeyVersions = new[] { "v1" }
            },
            s => s.Secrets[$"{Prefix}-v1"] = Key32Base64(0x33));

        var ct = await enc.EncryptAsync("hello");
        // Flip a character in the middle of the base64url envelope — NOT the
        // last char, which encodes padding bits that some base64 decoders
        // silently accept alternative encodings for (the "flip 'A' to 'B' at
        // the tail" approach can decode to identical bytes and leave AES-GCM
        // authentication intact).
        var mid = ct!.Length / 2;
        var tampered = ct[..mid] + (ct[mid] == 'A' ? 'B' : 'A') + ct[(mid + 1)..];

        var act = async () => await enc.DecryptAsync(tampered);
        await act.Should().ThrowAsync<CryptographicException>();
    }

    [Fact]
    public async Task WrongEnvelopeVersionByte_ThrowsCryptographicException()
    {
        var (enc, _) = Build(
            new ConsentEncryptionOptions
            {
                KeySecretPrefix = Prefix,
                CurrentKeyVersion = "v1",
                AcceptedKeyVersions = new[] { "v1" }
            },
            s => s.Secrets[$"{Prefix}-v1"] = Key32Base64(0x44));

        // 0x01 is the legacy envelope version — consent-service doesn't
        // support it. Handcraft an envelope with an unsupported marker.
        var bogus = new byte[] { 0x01, 0x00, 0x00 };
        var envelope = Convert.ToBase64String(bogus).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var act = async () => await enc.DecryptAsync(envelope);
        await act.Should().ThrowAsync<CryptographicException>();
    }
}
