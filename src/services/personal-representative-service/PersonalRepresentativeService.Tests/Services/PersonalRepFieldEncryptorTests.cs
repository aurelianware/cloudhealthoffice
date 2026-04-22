using System.Security.Cryptography;
using CloudHealthOffice.Infrastructure.Configuration;
using PersonalRepresentativeService.Services;
using PersonalRepresentativeService.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace PersonalRepresentativeService.Tests.Services;

public class PersonalRepFieldEncryptorTests
{
    private const string Prefix = "personal-rep-body-encryption-key";

    private static string Key32Base64(byte fill) =>
        Convert.ToBase64String(Enumerable.Repeat(fill, 32).ToArray());

    private static (PersonalRepFieldEncryptor enc, DictionarySecretProvider secrets)
        Build(PersonalRepEncryptionOptions options, Action<DictionarySecretProvider>? seed = null)
    {
        var secrets = new DictionarySecretProvider();
        seed?.Invoke(secrets);
        var keys = new RotatingKeyProvider(secrets, NullLogger<RotatingKeyProvider>.Instance);
        var enc = new PersonalRepFieldEncryptor(keys, NullLogger<PersonalRepFieldEncryptor>.Instance, options);
        return (enc, secrets);
    }

    [Fact]
    public async Task RoundTrip_V1_ReturnsOriginalPlaintext()
    {
        var (enc, _) = Build(
            new PersonalRepEncryptionOptions
            {
                KeySecretPrefix = Prefix,
                CurrentKeyVersion = "v1",
                AcceptedKeyVersions = new[] { "v1" }
            },
            s => s.Secrets[$"{Prefix}-v1"] = Key32Base64(0xAA));

        var plaintext = "Alice Rep — 555-0100 — 100 Main St";
        var ct = await enc.EncryptAsync(plaintext);
        ct.Should().NotBeNull();
        ct.Should().NotBe(plaintext);

        var back = await enc.DecryptAsync(ct);
        back.Should().Be(plaintext);
    }

    [Fact]
    public async Task RotationWindow_ReadsV1AndWritesV2()
    {
        var opts1 = new PersonalRepEncryptionOptions
        {
            KeySecretPrefix = Prefix,
            CurrentKeyVersion = "v1",
            AcceptedKeyVersions = new[] { "v1" }
        };
        var (encV1, secrets) = Build(opts1, s => s.Secrets[$"{Prefix}-v1"] = Key32Base64(0x01));
        var ctV1 = await encV1.EncryptAsync("hello");

        secrets.Secrets[$"{Prefix}-v2"] = Key32Base64(0x02);
        var opts2 = new PersonalRepEncryptionOptions
        {
            KeySecretPrefix = Prefix,
            CurrentKeyVersion = "v2",
            AcceptedKeyVersions = new[] { "v1", "v2" }
        };
        var keys2 = new RotatingKeyProvider(secrets, NullLogger<RotatingKeyProvider>.Instance);
        var encV2 = new PersonalRepFieldEncryptor(keys2, NullLogger<PersonalRepFieldEncryptor>.Instance, opts2);

        (await encV2.DecryptAsync(ctV1)).Should().Be("hello");

        var ctV2 = await encV2.EncryptAsync("world");
        ctV2.Should().NotBeNull();
        (await encV2.DecryptAsync(ctV2)).Should().Be("world");
    }

    [Fact]
    public async Task StaleKey_DroppedFromAccepted_ThrowsCryptographicException()
    {
        var optsWide = new PersonalRepEncryptionOptions
        {
            KeySecretPrefix = Prefix,
            CurrentKeyVersion = "v1",
            AcceptedKeyVersions = new[] { "v1" }
        };
        var (encWide, secrets) = Build(optsWide,
            s => s.Secrets[$"{Prefix}-v1"] = Key32Base64(0x11));
        var ct = await encWide.EncryptAsync("secret");

        var optsNarrow = new PersonalRepEncryptionOptions
        {
            KeySecretPrefix = Prefix,
            CurrentKeyVersion = "v2",
            AcceptedKeyVersions = new[] { "v2" }
        };
        secrets.Secrets[$"{Prefix}-v2"] = Key32Base64(0x22);
        var keys = new RotatingKeyProvider(secrets, NullLogger<RotatingKeyProvider>.Instance);
        var encNarrow = new PersonalRepFieldEncryptor(keys, NullLogger<PersonalRepFieldEncryptor>.Instance, optsNarrow);

        var act = async () => await encNarrow.DecryptAsync(ct);
        await act.Should().ThrowAsync<CryptographicException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task NullOrEmpty_PassThrough(string? input)
    {
        var (enc, _) = Build(new PersonalRepEncryptionOptions
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
            new PersonalRepEncryptionOptions
            {
                KeySecretPrefix = Prefix,
                CurrentKeyVersion = "v1",
                AcceptedKeyVersions = new[] { "v1" }
            },
            s => s.Secrets[$"{Prefix}-v1"] = Key32Base64(0x33));

        var ct = await enc.EncryptAsync("hello");
        var mid = ct!.Length / 2;
        var tampered = ct[..mid] + (ct[mid] == 'A' ? 'B' : 'A') + ct[(mid + 1)..];

        var act = async () => await enc.DecryptAsync(tampered);
        await act.Should().ThrowAsync<CryptographicException>();
    }

    [Fact]
    public async Task WrongEnvelopeVersionByte_ThrowsCryptographicException()
    {
        // 0x01 is the legacy envelope version — personal-rep-service doesn't
        // support it (greenfield service, 0x02 only).
        var (enc, _) = Build(
            new PersonalRepEncryptionOptions
            {
                KeySecretPrefix = Prefix,
                CurrentKeyVersion = "v1",
                AcceptedKeyVersions = new[] { "v1" }
            },
            s => s.Secrets[$"{Prefix}-v1"] = Key32Base64(0x44));

        var bogus = new byte[] { 0x01, 0x00, 0x00 };
        var envelope = Convert.ToBase64String(bogus).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var act = async () => await enc.DecryptAsync(envelope);
        await act.Should().ThrowAsync<CryptographicException>();
    }
}
