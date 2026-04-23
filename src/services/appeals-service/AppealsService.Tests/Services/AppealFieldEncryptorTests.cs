using System.Security.Cryptography;
using AppealsService.Services;
using AppealsService.Tests.Fakes;
using CloudHealthOffice.Infrastructure.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppealsService.Tests.Services;

public class AppealFieldEncryptorTests
{
    private static (AppealFieldEncryptor Encryptor, DictionarySecretProvider Secrets) BuildEncryptor(
        string currentVersion = "v1",
        IReadOnlyList<string>? acceptedVersions = null,
        params (string Version, byte[] Key)[] seeded)
    {
        var secrets = new DictionarySecretProvider();
        foreach (var s in seeded)
        {
            secrets.Secrets[$"appeal-body-encryption-key-{s.Version}"] = Convert.ToBase64String(s.Key);
        }
        var keys = new RotatingKeyProvider(secrets, NullLogger<RotatingKeyProvider>.Instance);
        var options = new AppealEncryptionOptions
        {
            KeySecretPrefix = "appeal-body-encryption-key",
            CurrentKeyVersion = currentVersion,
            AcceptedKeyVersions = acceptedVersions ?? new[] { currentVersion }
        };
        return (new AppealFieldEncryptor(keys, NullLogger<AppealFieldEncryptor>.Instance, options), secrets);
    }

    [Fact]
    public async Task RoundTrip_RestoresPlaintext()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var (enc, _) = BuildEncryptor(seeded: new[] { ("v1", key) });

        var cipher = await enc.EncryptAsync("patient name");
        cipher.Should().NotBeNullOrEmpty();
        cipher.Should().NotBe("patient name");

        var plain = await enc.DecryptAsync(cipher);
        plain.Should().Be("patient name");
    }

    [Fact]
    public async Task Encrypt_PassesThroughNullAndEmpty()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var (enc, _) = BuildEncryptor(seeded: new[] { ("v1", key) });

        (await enc.EncryptAsync(null)).Should().BeNull();
        (await enc.EncryptAsync("")).Should().Be("");
    }

    [Fact]
    public async Task Rotation_OldEnvelopeDecryptsIfVersionAccepted()
    {
        var v1Key = RandomNumberGenerator.GetBytes(32);
        var v2Key = RandomNumberGenerator.GetBytes(32);

        var (v1Enc, _) = BuildEncryptor(
            currentVersion: "v1", acceptedVersions: new[] { "v1" },
            seeded: new[] { ("v1", v1Key) });
        var oldCipher = await v1Enc.EncryptAsync("old secret");

        var (v2Enc, _) = BuildEncryptor(
            currentVersion: "v2", acceptedVersions: new[] { "v2", "v1" },
            seeded: new[] { ("v1", v1Key), ("v2", v2Key) });

        var plain = await v2Enc.DecryptAsync(oldCipher);
        plain.Should().Be("old secret");

        var newCipher = await v2Enc.EncryptAsync("new secret");
        newCipher.Should().NotBe(oldCipher);
        (await v2Enc.DecryptAsync(newCipher)).Should().Be("new secret");
    }

    [Fact]
    public async Task StaleKey_ThrowsStaleEncryptionKeyException()
    {
        var v1Key = RandomNumberGenerator.GetBytes(32);
        var (v1Enc, _) = BuildEncryptor(
            currentVersion: "v1", acceptedVersions: new[] { "v1" },
            seeded: new[] { ("v1", v1Key) });
        var cipher = await v1Enc.EncryptAsync("will become stale");

        // New deployment: v1 is still in AcceptedKeyVersions but the secret
        // was dropped from the provider (operator mistake) — decrypt must
        // surface StaleEncryptionKeyException, not a silent CryptographicException.
        var (staleEnc, _) = BuildEncryptor(
            currentVersion: "v2", acceptedVersions: new[] { "v2", "v1" },
            seeded: new[] { ("v2", RandomNumberGenerator.GetBytes(32)) });

        await FluentActions.Invoking(() => staleEnc.DecryptAsync(cipher))
            .Should().ThrowAsync<StaleEncryptionKeyException>();
    }

    [Fact]
    public async Task EnvelopeKeyVersion_NotInAcceptedList_Throws()
    {
        var v1Key = RandomNumberGenerator.GetBytes(32);
        var (v1Enc, _) = BuildEncryptor(
            currentVersion: "v1", acceptedVersions: new[] { "v1" },
            seeded: new[] { ("v1", v1Key) });
        var cipher = await v1Enc.EncryptAsync("classified");

        var (v2Only, _) = BuildEncryptor(
            currentVersion: "v2", acceptedVersions: new[] { "v2" },
            seeded: new[] { ("v1", v1Key), ("v2", RandomNumberGenerator.GetBytes(32)) });

        await FluentActions.Invoking(() => v2Only.DecryptAsync(cipher))
            .Should().ThrowAsync<CryptographicException>()
            .Where(ex => ex.Message.Contains("AcceptedKeyVersions"));
    }

    [Fact]
    public async Task WrongKeyLength_Throws()
    {
        var badKey = RandomNumberGenerator.GetBytes(16); // AES-128, not AES-256
        var (enc, _) = BuildEncryptor(seeded: new[] { ("v1", badKey) });

        await FluentActions.Invoking(() => enc.EncryptAsync("whatever"))
            .Should().ThrowAsync<InvalidOperationException>()
            .Where(ex => ex.Message.Contains("32 bytes"));
    }

    [Fact]
    public async Task DecryptMalformedCiphertext_ThrowsCryptographicException()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var (enc, _) = BuildEncryptor(seeded: new[] { ("v1", key) });

        await FluentActions.Invoking(() => enc.DecryptAsync("not-base64-at-all"))
            .Should().ThrowAsync<CryptographicException>();
    }

    [Fact]
    public void Options_InvalidValues_ConstructorThrows()
    {
        var secrets = new DictionarySecretProvider();
        var keys = new RotatingKeyProvider(secrets, NullLogger<RotatingKeyProvider>.Instance);

        Action missingPrefix = () => new AppealFieldEncryptor(keys, NullLogger<AppealFieldEncryptor>.Instance,
            new AppealEncryptionOptions { KeySecretPrefix = "", CurrentKeyVersion = "v1" });
        missingPrefix.Should().Throw<ArgumentException>();

        Action missingVersion = () => new AppealFieldEncryptor(keys, NullLogger<AppealFieldEncryptor>.Instance,
            new AppealEncryptionOptions { CurrentKeyVersion = "" });
        missingVersion.Should().Throw<ArgumentException>();

        Action emptyAccepted = () => new AppealFieldEncryptor(keys, NullLogger<AppealFieldEncryptor>.Instance,
            new AppealEncryptionOptions { AcceptedKeyVersions = Array.Empty<string>() });
        emptyAccepted.Should().Throw<ArgumentException>();
    }
}
