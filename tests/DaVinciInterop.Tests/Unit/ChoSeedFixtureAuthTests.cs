using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace DaVinciInterop.Tests.Unit;

/// <summary>
/// The harness and <c>scripts/interop/seed-cho.mjs</c> mint the same development
/// token from the same block of <c>interop/cho-seed.json</c>. These tests check
/// the C# side reads that block rather than restating it, because the symptom of
/// a drift is an opaque 401 from a service that cannot say which side is wrong.
/// </summary>
[Trait("Category", "DaVinciInteropUnit")]
public sealed class ChoSeedFixtureAuthTests
{
    private static readonly ChoSeedFixture Fixture = ChoSeedFixture.Load();

    [Fact]
    public void The_fixture_declares_the_identity_both_readers_use()
    {
        Fixture.TenantId.Should().NotBeNullOrWhiteSpace();
        Fixture.Member.MemberId.Should().NotBeNullOrWhiteSpace(
            "the seeded member id is what a suite is told to search for");

        Fixture.Auth.Issuer.Should().NotBeNullOrWhiteSpace();
        Fixture.Auth.Audience.Should().NotBeNullOrWhiteSpace();
        Fixture.Auth.LifetimeSeconds.Should().BeGreaterThan(0);
        Fixture.Auth.Permissions.Should().NotBeEmpty();
    }

    [Fact]
    public void The_seed_script_reads_the_same_block_rather_than_its_own_constants()
    {
        var script = File.ReadAllText(
            Path.Combine(InteropPaths.RepositoryRoot, "scripts", "interop", "seed-cho.mjs"));

        script.Should().Contain("fixture.auth",
            "the seeder must take its identity from the fixture, not from literals of its own");

        script.Should().NotContain($"'{Fixture.Auth.Issuer}'",
            "an issuer written into the script as well as the fixture is a copy that can drift");
    }

    [Fact]
    public void The_minted_token_carries_the_claims_the_fixture_declares()
    {
        var parts = Fixture.DevelopmentToken().Split('.');
        parts.Should().HaveCount(3);

        using var payload = JsonDocument.Parse(Decode(parts[1]));
        var root = payload.RootElement;

        root.GetProperty("iss").GetString().Should().Be(Fixture.Auth.Issuer);
        root.GetProperty("aud").GetString().Should().Be(Fixture.Auth.Audience);
        root.GetProperty("tenant_id").GetString().Should().Be(Fixture.TenantId);
        root.GetProperty("sub").GetString().Should().Be($"{Fixture.Auth.SubjectPrefix}-{Fixture.TenantId}");

        root.GetProperty("permissions").EnumerateArray().Select(p => p.GetString())
            .Should().BeEquivalentTo(Fixture.Auth.Permissions);

        root.GetProperty("exp").GetInt64().Should()
            .Be(root.GetProperty("iat").GetInt64() + Fixture.Auth.LifetimeSeconds);
    }

    [Fact]
    public void The_signature_verifies_against_the_declared_key()
    {
        var token = Fixture.DevelopmentToken();
        var lastDot = token.LastIndexOf('.');

        using var hmac = new HMACSHA256(Convert.FromBase64String(Fixture.Auth.SigningKeyBase64));
        var expected = hmac.ComputeHash(Encoding.UTF8.GetBytes(token[..lastDot]));

        Decode(token[(lastDot + 1)..]).Should().Equal(expected);
    }

    [Fact]
    public void Two_tokens_are_distinguishable()
    {
        // jti is what makes a replayed token identifiable. A constant one would
        // make two runs indistinguishable in a service's audit log.
        var first = JsonDocument.Parse(Decode(Fixture.DevelopmentToken().Split('.')[1]));
        var second = JsonDocument.Parse(Decode(Fixture.DevelopmentToken().Split('.')[1]));

        first.RootElement.GetProperty("jti").GetString()
            .Should().NotBe(second.RootElement.GetProperty("jti").GetString());
    }

    private static byte[] Decode(string segment)
    {
        var padded = segment.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - (padded.Length % 4)) % 4);
        return Convert.FromBase64String(padded);
    }
}
