using System.Security.Cryptography;
using CloudHealthOffice.Infrastructure.Configuration;
using MemberService.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MemberService.Tests.Services;

/// <summary>
/// The fingerprint output is the database lookup key — embedding a key
/// version would change the lookup value and break every existing row.
/// So rotation is handled dual-read: write under current, read under all
/// accepted. These tests pin that contract down so a regression can't
/// silently produce duplicates during a rotation window.
/// </summary>
public class FingerprinterDualReadTests
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

    private static HmacSha256IdentifierFingerprinter Build(
        ISecretProvider secrets, MemberFingerprintingOptions options) =>
        new(
            new RotatingKeyProvider(secrets, NullLogger<RotatingKeyProvider>.Instance),
            secrets,
            NullLogger<HmacSha256IdentifierFingerprinter>.Instance,
            options);

    [Fact]
    public async Task Fingerprint_UnderCurrentVersion_MatchesOldRecord_InCandidateSet()
    {
        var v1Key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var v2Key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var secrets = new MultiKeySecretProvider(new Dictionary<string, string>
        {
            ["fp-v1"] = v1Key,
            ["fp-v2"] = v2Key
        });

        // Stage 1: written under v1.
        var v1Fp = Build(secrets, new MemberFingerprintingOptions
        {
            KeySecretPrefix = "fp", CurrentKeyVersion = "v1",
            AcceptedKeyVersions = new[] { "v1" }, LegacyKeySecretName = null
        });
        var storedFingerprint = await v1Fp.FingerprintAsync("123-45-6789");

        // Stage 2: v2 now current, v1 still accepted.
        var v2Fp = Build(secrets, new MemberFingerprintingOptions
        {
            KeySecretPrefix = "fp", CurrentKeyVersion = "v2",
            AcceptedKeyVersions = new[] { "v2", "v1" }, LegacyKeySecretName = null
        });

        // Write path now produces v2 fingerprint.
        var newV2Fingerprint = await v2Fp.FingerprintAsync("123-45-6789");
        newV2Fingerprint.Should().NotBe(storedFingerprint);

        // Read path produces candidates for BOTH versions — the stored v1 row is found.
        var candidates = await v2Fp.FingerprintCandidatesAsync("123-45-6789");
        candidates.Should().Contain(storedFingerprint);
        candidates.Should().Contain(newV2Fingerprint);
    }

    [Fact]
    public async Task FingerprintCandidates_SkipsUnresolvableVersion()
    {
        // v1 is accepted but the operator accidentally didn't publish fp-v1.
        // The candidates call must still return v2 (and log a warning for v1)
        // rather than throwing — otherwise a dedupe read would 500 instead of
        // matching on whatever versions DO resolve.
        var v2Key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var secrets = new MultiKeySecretProvider(new Dictionary<string, string>
        {
            ["fp-v2"] = v2Key
        });

        var fp = Build(secrets, new MemberFingerprintingOptions
        {
            KeySecretPrefix = "fp", CurrentKeyVersion = "v2",
            AcceptedKeyVersions = new[] { "v2", "v1" }, LegacyKeySecretName = null
        });

        var candidates = await fp.FingerprintCandidatesAsync("anything");
        candidates.Should().HaveCount(1);
    }

    [Fact]
    public async Task LegacyKeySecretName_UsedAsImplicitV1_WhenPrefixedSecretAbsent()
    {
        // Pre-A.7.3 deploy: only the single-name secret exists. The
        // fingerprinter must resolve v1 via LegacyKeySecretName so existing
        // rows continue to dedupe without the operator publishing fp-v1.
        var legacyKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var secrets = new MultiKeySecretProvider(new Dictionary<string, string>
        {
            ["legacy-fp"] = legacyKey
        });

        var fp = Build(secrets, new MemberFingerprintingOptions
        {
            KeySecretPrefix = "fp", CurrentKeyVersion = "v1",
            AcceptedKeyVersions = new[] { "v1" },
            LegacyKeySecretName = "legacy-fp"
        });

        var single = await fp.FingerprintAsync("123-45-6789");
        single.Should().NotBeNullOrEmpty();

        var candidates = await fp.FingerprintCandidatesAsync("123-45-6789");
        candidates.Should().ContainSingle().And.Contain(single);
    }
}
