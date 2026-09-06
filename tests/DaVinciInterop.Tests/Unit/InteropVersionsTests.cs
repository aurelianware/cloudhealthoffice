using FluentAssertions;

namespace DaVinciInterop.Tests.Unit;

/// <summary>
/// The pins are the whole reproducibility story: if a target can drift, an interop
/// result means nothing a week later. These tests fail the build rather than let a
/// floating tag reach CI.
/// </summary>
[Trait("Category", "DaVinciInteropUnit")]
public sealed class InteropVersionsTests
{
    private static readonly InteropVersions Versions = InteropVersions.Load();

    [Fact]
    public void Every_external_target_is_pinned_reproducibly()
    {
        foreach (var target in Versions.Targets)
        {
            target.Pin.IsReproducible.Should().BeTrue(
                "'{0}' must be pinned to an immutable upstream artifact (image digest, or tag plus commit SHA), " +
                "never a floating tag or branch. Pin reference was '{1}'.",
                target.Name, target.Pin.Reference);
        }
    }

    [Fact]
    public void Image_pins_reference_a_digest_rather_than_a_tag()
    {
        foreach (var target in Versions.Targets.Where(t => t.Pin.ParsedKind == PinKind.ImageDigest))
        {
            target.Pin.Reference.Should().Contain("@sha256:",
                "'{0}' is started from an image, so the compose file must reference it by digest", target.Name);
            target.Pin.Reference.Should().NotContain(":latest");
        }
    }

    [Fact]
    public void The_compose_file_starts_exactly_the_pinned_images()
    {
        var compose = File.ReadAllText(InteropPaths.ComposeFile);

        foreach (var target in Versions.Targets.Where(t => t.Pin.ParsedKind == PinKind.ImageDigest))
        {
            compose.Should().Contain(target.Pin.Reference,
                "interop/docker-compose.interop.yml must start '{0}' at the digest recorded in " +
                "interop/versions.json — a pin nothing enforces is not a pin", target.Name);
        }
    }

    [Fact]
    public void Every_target_records_its_upstream_repository_and_license()
    {
        foreach (var target in Versions.Targets)
        {
            target.UpstreamRepository.Should().StartWith("https://github.com/");
            target.License.Should().NotBeNullOrWhiteSpace(
                "'{0}' is third-party code; its license must be recorded", target.Name);
        }
    }

    [Fact]
    public void Every_target_declares_how_readiness_is_observed()
    {
        foreach (var target in Versions.Targets)
        {
            target.Endpoints.ReadinessUrl.Should().NotBeNullOrWhiteSpace(
                "'{0}' must declare a readiness endpoint so the harness never guesses with a sleep", target.Name);
        }
    }

    [Fact]
    public void Pinned_content_sources_are_referenced_rather_than_vendored()
    {
        foreach (var source in Versions.ContentSources)
        {
            source.Vendored.Should().BeFalse(
                "'{0}' must be fetched at its pin, not copied into this repository", source.Name);
            source.Pin.IsReproducible.Should().BeTrue();
        }
    }

    [Fact]
    public void The_burden_reduction_payer_is_pinned_to_the_digest_the_smoke_scenario_was_proven_against()
    {
        var payer = Versions.Target("br-payer");

        payer.Pin.Digest.Should().Be(
            "sha256:6074aebc39929a00cf93c1efa28c227eb46aab2418afa208eb293133cb150d8c",
            "changing this pin is a deliberate, reviewable upgrade — see docs/interop/davinci.md");
        payer.Pin.SourceCommit.Should().Be("09d794e202717b4f6c86823626d05eb8667f4010");
        payer.ImplementationGuides["PAS"].Should().Be("2.2.1");
    }

    [Fact]
    public void Unknown_target_keys_fail_loudly()
    {
        var act = () => Versions.Target("no-such-target");

        act.Should().Throw<KeyNotFoundException>().WithMessage("*br-payer*");
    }
}
