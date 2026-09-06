using System.Security.Cryptography;
using FhirService.Services.Identity;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace CloudHealthOffice.FhirService.Tests.Identity;

/// <summary>
/// SEC-01 — signing-key rotation, refresh bounding, and IdP-outage behaviour.
///
/// Three properties have to hold simultaneously and they pull against each
/// other: rotation must work unattended (so an unknown kid triggers a refresh),
/// that trigger is attacker-controlled (so refreshes must be bounded and
/// single-flighted), and an IdP outage must not become an outage here (so
/// cached keys survive, but not forever).
/// </summary>
public class SmartSigningKeyRingTests
{
    private const string IssuerName = "https://idp.example.com";

    /// <summary>A fetcher whose responses and failures the test drives directly.</summary>
    private sealed class ScriptedFetcher : IIssuerMetadataFetcher
    {
        public List<string> KeyIds { get; set; } = ["key-A"];
        public bool ShouldFail { get; set; }
        public int FetchCount;
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;

        public async Task<IssuerMetadata> FetchAsync(
            TrustedIssuerOptions issuer, CancellationToken ct = default)
        {
            Interlocked.Increment(ref FetchCount);
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            if (ShouldFail) throw new IssuerMetadataException("issuer unreachable");

            return new IssuerMetadata
            {
                Issuer = issuer.Issuer,
                JwksUri = $"{issuer.Issuer}/jwks",
                SigningKeys = KeyIds.Select(CreateKey).ToList(),
            };
        }

        private static SecurityKey CreateKey(string kid)
            => new RsaSecurityKey(RSA.Create(2048)) { KeyId = kid };
    }

    private static (SmartSigningKeyRing Ring, ScriptedFetcher Fetcher, FakeTimeProvider Time) Build()
    {
        var options = new SmartTrustOptions
        {
            Mode = SmartTrustMode.ExternalIssuer,
            TrustedIssuers =
            [
                new TrustedIssuerOptions
                {
                    Issuer = IssuerName,
                    Audiences = ["https://api.cloudhealthoffice.com"],
                }
            ],
        };

        var fetcher = new ScriptedFetcher();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var ring = new SmartSigningKeyRing(
            fetcher, new TrustedIssuerRegistry(options),
            NullLogger<SmartSigningKeyRing>.Instance, time);

        return (ring, fetcher, time);
    }

    // ── The rotation story, start to finish ───────────────────────────────────

    [Fact]
    public void RotationSequence_OldKeyWorks_NewKidRefreshes_UnknownKeyStaysRejected()
    {
        var (ring, fetcher, time) = Build();

        // 1. The issuer publishes key A; a token signed with A validates.
        ring.ResolveKeys(IssuerName, "key-A").Should().ContainSingle()
            .Which.KeyId.Should().Be("key-A");
        fetcher.FetchCount.Should().Be(1);

        // 2. Nothing has changed, so no further fetch.
        ring.ResolveKeys(IssuerName, "key-A");
        fetcher.FetchCount.Should().Be(1, "a known kid must not trigger a refresh");

        // 3. The issuer rotates to key B. The only signal is a token whose kid
        //    the ring has never seen — so that has to be enough.
        fetcher.KeyIds = ["key-A", "key-B"];
        time.Advance(SmartSigningKeyRing.MinRefreshInterval + TimeSpan.FromSeconds(1));

        var afterRotation = ring.ResolveKeys(IssuerName, "key-B");
        fetcher.FetchCount.Should().Be(2, "an unknown kid must trigger exactly one refresh");
        afterRotation.Select(k => k.KeyId).Should().Contain("key-B");

        // 4. A key the issuer never published stays rejected: the refresh
        //    happened, and the kid is still absent. Rotation support must not
        //    become "any kid eventually works".
        time.Advance(SmartSigningKeyRing.MinRefreshInterval + TimeSpan.FromSeconds(1));
        ring.ResolveKeys(IssuerName, "key-forged")
            .Select(k => k.KeyId).Should().NotContain("key-forged");
    }

    // ── The unknown kid is attacker-controlled ────────────────────────────────

    [Fact]
    public void ABurstOfUnknownKids_IsRateLimitedToOneFetch()
    {
        // Anyone can present a token with a random kid, so "unknown kid means
        // refresh" is also an unauthenticated way to make CHO hammer its IdP.
        var (ring, fetcher, _) = Build();
        ring.ResolveKeys(IssuerName, "key-A");
        fetcher.FetchCount.Should().Be(1);

        for (var i = 0; i < 50; i++)
            ring.ResolveKeys(IssuerName, $"forged-{i}");

        fetcher.FetchCount.Should().Be(1,
            "50 forged kids inside one refresh interval cost no extra fetch at all — the keys "
            + "were retrieved moments ago, so re-fetching could not produce a kid the issuer "
            + "has not published");
    }

    [Fact]
    public void ConcurrentRequestsSeeingTheSameNewKid_ProduceOneFetch()
    {
        // The thundering herd: a real rotation makes EVERY in-flight request see
        // the new kid at the same instant.
        var (ring, fetcher, time) = Build();
        ring.ResolveKeys(IssuerName, "key-A");

        fetcher.KeyIds = ["key-A", "key-B"];
        fetcher.Delay = TimeSpan.FromMilliseconds(150);
        time.Advance(SmartSigningKeyRing.MinRefreshInterval + TimeSpan.FromSeconds(1));

        var before = fetcher.FetchCount;
        Parallel.For(0, 32, _ => ring.ResolveKeys(IssuerName, "key-B"));

        (fetcher.FetchCount - before).Should().Be(1,
            "32 concurrent requests seeing one new kid must single-flight into one fetch");
        ring.ResolveKeys(IssuerName, "key-B").Select(k => k.KeyId).Should().Contain("key-B");
    }

    // ── IdP outage ────────────────────────────────────────────────────────────

    [Fact]
    public void WhenTheIssuerGoesDown_PreviouslyCachedKeysKeepWorking()
    {
        // An IdP outage must degrade rotation, never signature validation.
        var (ring, fetcher, time) = Build();
        ring.ResolveKeys(IssuerName, "key-A").Should().ContainSingle();

        fetcher.ShouldFail = true;
        time.Advance(SmartSigningKeyRing.RefreshInterval + TimeSpan.FromMinutes(1));

        ring.ResolveKeys(IssuerName, "key-A").Should().ContainSingle()
            .Which.KeyId.Should().Be("key-A");
    }

    [Fact]
    public void DuringAnOutage_AnUnknownKidStillFailsClosed()
    {
        var (ring, fetcher, time) = Build();
        ring.ResolveKeys(IssuerName, "key-A");

        fetcher.ShouldFail = true;
        time.Advance(SmartSigningKeyRing.MinRefreshInterval + TimeSpan.FromSeconds(1));

        ring.ResolveKeys(IssuerName, "key-unknown")
            .Select(k => k.KeyId).Should().NotContain("key-unknown");
    }

    [Fact]
    public void CachedKeysPastTheStalenessBound_StopBeingTrusted()
    {
        // Indefinitely stale trust would keep honouring a key the issuer had
        // revoked, so the outage tolerance is bounded on purpose.
        var (ring, fetcher, time) = Build();
        ring.ResolveKeys(IssuerName, "key-A").Should().ContainSingle();

        fetcher.ShouldFail = true;
        time.Advance(SmartSigningKeyRing.MaxStaleAge + TimeSpan.FromMinutes(1));

        ring.ResolveKeys(IssuerName, "key-A").Should().BeEmpty(
            "keys older than the staleness bound are no longer trust material");
    }

    [Fact]
    public void AnIssuerThatWasNeverConfigured_ResolvesToNoKeys()
    {
        // Fail closed, and never fall through to another issuer's keys.
        var (ring, fetcher, _) = Build();

        ring.ResolveKeys("https://attacker.test", "key-A").Should().BeEmpty();
        fetcher.FetchCount.Should().Be(0, "an unknown issuer must not cause any fetch at all");
    }

    // ── Readiness projection ──────────────────────────────────────────────────

    [Fact]
    public void Status_ReportsTrustStateAndNeverKeyMaterial()
    {
        var (ring, _, _) = Build();
        ring.ResolveKeys(IssuerName, "key-A");

        var status = ring.Status().Should().ContainSingle().Subject;
        status.Issuer.Should().Be(IssuerName);
        status.HasKeys.Should().BeTrue();
        status.KeyCount.Should().Be(1);
        status.LastError.Should().BeNull();

        // The shape carries counts and times, not keys.
        typeof(IssuerTrustStatus).GetProperties().Select(p => p.Name)
            .Should().NotContain(n => n.Contains("Key") && n != "KeyCount" && n != "HasKeys");
    }
}

/// <summary>Deterministic clock for cache-window and staleness assertions.</summary>
internal sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
