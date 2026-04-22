using System.Security.Cryptography;
using CloudHealthOffice.Infrastructure.Configuration;
using MemberService.Controllers;
using MemberService.Models;
using MemberService.Services;
using MemberService.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace MemberService.Tests.Controllers;

/// <summary>
/// Verifies that PII identifier dedupe survives encryption nonce randomness
/// (same plaintext SSN encrypted twice yields different ciphertexts, but the
/// fingerprint matches) and normalization (dashes/spaces/case stripped).
/// </summary>
public class PiiIdentifierDedupeTests
{
    private const string Tenant = "t1";

    private sealed class StaticSecretProvider : ISecretProvider
    {
        private readonly string _secret;
        public StaticSecretProvider(string secret) { _secret = secret; }
        public Task<string?> GetSecretAsync(string n, CancellationToken ct = default) => Task.FromResult<string?>(_secret);
        public Task<IDictionary<string, string>> GetSecretsAsync(string p, CancellationToken ct = default)
            => Task.FromResult<IDictionary<string, string>>(new Dictionary<string, string>());
        public Task<bool> HealthCheckAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<string?> GetSecretByVersionAsync(string n, string v, CancellationToken ct = default)
            => Task.FromResult<string?>(_secret);
        public Task<IReadOnlyList<SecretVersionInfo>> ListSecretVersionsAsync(string n, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SecretVersionInfo>>(Array.Empty<SecretVersionInfo>());
    }

    private static (IdentifiersController ctl, InMemoryMemberRepository repo) Build(
        IIdentifierEncryptor encryptor,
        IIdentifierFingerprinter fingerprinter)
    {
        var repo = new InMemoryMemberRepository();
        var events = new InMemoryMemberEventRepository();
        var publisher = new CosmosMemberEventPublisher(events, NullLogger<CosmosMemberEventPublisher>.Instance);
        var ctl = new IdentifiersController(repo, encryptor, fingerprinter, publisher);

        var http = new DefaultHttpContext();
        http.Items["TenantId"] = Tenant;
        ctl.ControllerContext = new ControllerContext { HttpContext = http };

        repo.Members.Add(new Member
        {
            TenantId = Tenant, MemberId = "M-001",
            GroupNumber = "G", IsSubscriber = true,
            FirstName = "A", LastName = "B",
            DateOfBirth = new DateTime(2000, 1, 1),
            EffectiveDate = new DateTime(2024, 1, 1)
        });
        return (ctl, repo);
    }

    private static (KeyVaultIdentifierEncryptor enc, HmacSha256IdentifierFingerprinter fp) RealKeys()
    {
        var encKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var fpKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var encSecrets = new StaticSecretProvider(encKey);
        var fpSecrets = new StaticSecretProvider(fpKey);
        var encOptions = new MemberEncryptionOptions
        {
            KeySecretPrefix = "enc", CurrentKeyVersion = "v1",
            AcceptedKeyVersions = new[] { "v1" },
            LegacyKeySecretName = "enc",
            EmitLegacyEnvelope = true
        };
        var fpOptions = new MemberFingerprintingOptions
        {
            KeySecretPrefix = "fp", CurrentKeyVersion = "v1",
            AcceptedKeyVersions = new[] { "v1" },
            LegacyKeySecretName = "fp"
        };
        var enc = new KeyVaultIdentifierEncryptor(
            new RotatingKeyProvider(encSecrets, NullLogger<RotatingKeyProvider>.Instance),
            encSecrets,
            NullLogger<KeyVaultIdentifierEncryptor>.Instance,
            encOptions);
        var fp = new HmacSha256IdentifierFingerprinter(
            new RotatingKeyProvider(fpSecrets, NullLogger<RotatingKeyProvider>.Instance),
            fpSecrets,
            NullLogger<HmacSha256IdentifierFingerprinter>.Instance,
            fpOptions);
        return (enc, fp);
    }

    [Fact]
    public async Task SameSsn_AddedTwice_AcrossDifferentNonces_Returns409()
    {
        var (enc, fp) = RealKeys();
        var (ctl, repo) = Build(enc, fp);

        var req1 = new AddIdentifierRequest { Type = MemberIdentifierType.SSN, Value = "123-45-6789" };
        var req2 = new AddIdentifierRequest { Type = MemberIdentifierType.SSN, Value = "123-45-6789" };

        var first = await ctl.Add("M-001", req1, CancellationToken.None);
        first.Should().BeOfType<CreatedAtActionResult>();

        // Different AES-GCM nonce on the second call → different ciphertext.
        // Fingerprint-based dedupe must still catch this.
        var second = await ctl.Add("M-001", req2, CancellationToken.None);
        second.Should().BeOfType<ConflictObjectResult>();

        repo.Members[0].Identifiers.Should().ContainSingle();
        // Ciphertext stored is not equal to plaintext — encryption really happened.
        repo.Members[0].Identifiers[0].Value.Should().NotBe("123-45-6789");
        repo.Members[0].Identifiers[0].ValueFingerprint.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task NormalizedSsn_DashesVsRaw_Returns409()
    {
        var (enc, fp) = RealKeys();
        var (ctl, _) = Build(enc, fp);

        (await ctl.Add("M-001",
            new AddIdentifierRequest { Type = MemberIdentifierType.SSN, Value = "123-45-6789" },
            CancellationToken.None)).Should().BeOfType<CreatedAtActionResult>();

        (await ctl.Add("M-001",
            new AddIdentifierRequest { Type = MemberIdentifierType.SSN, Value = "123456789" },
            CancellationToken.None)).Should().BeOfType<ConflictObjectResult>();

        (await ctl.Add("M-001",
            new AddIdentifierRequest { Type = MemberIdentifierType.SSN, Value = "123 45 6789" },
            CancellationToken.None)).Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task NormalizedMbi_CaseAndDashVariations_Returns409()
    {
        var (enc, fp) = RealKeys();
        var (ctl, _) = Build(enc, fp);

        (await ctl.Add("M-001",
            new AddIdentifierRequest { Type = MemberIdentifierType.MedicareMbi, Value = "1eg4-te5-mk73" },
            CancellationToken.None)).Should().BeOfType<CreatedAtActionResult>();

        (await ctl.Add("M-001",
            new AddIdentifierRequest { Type = MemberIdentifierType.MedicareMbi, Value = "1EG4TE5MK73" },
            CancellationToken.None)).Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task DifferentSsns_BothPersist()
    {
        var (enc, fp) = RealKeys();
        var (ctl, repo) = Build(enc, fp);

        await ctl.Add("M-001",
            new AddIdentifierRequest { Type = MemberIdentifierType.SSN, Value = "111-22-3333" },
            CancellationToken.None);
        await ctl.Add("M-001",
            new AddIdentifierRequest { Type = MemberIdentifierType.SSN, Value = "444-55-6666" },
            CancellationToken.None);

        repo.Members[0].Identifiers.Should().HaveCount(2);
    }
}
