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
    public void Every_image_the_compose_file_starts_is_pinned_by_digest()
    {
        // The pin tests above only reach images interop/versions.json records as
        // targets. The Inferno kit profiles also start two supporting images —
        // the Sidekiq queue and the HL7 validator — and the validator's output
        // IS the conformance verdict, so a floating tag there would let a
        // verdict change with no reviewable diff. Two references are exempt
        // deliberately: the development MongoDB behind the CHO profile, whose
        // version no external result depends on, and the images built here from
        // an upstream checkout, which are pinned by the checkout's own tag.
        var offenders = File.ReadAllLines(InteropPaths.ComposeFile)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("image:", StringComparison.Ordinal))
            .Select(line => line["image:".Length..].Trim())
            .Where(image => !image.StartsWith("mongo:", StringComparison.Ordinal)
                            && !image.StartsWith("cho-interop/", StringComparison.Ordinal))
            .Where(image => !image.Contains("@sha256:", StringComparison.Ordinal))
            .ToList();

        offenders.Should().BeEmpty(
            "every third-party image this file starts must be referenced by digest, with the tag it was " +
            "resolved from recorded in a comment beside it");
    }

    [Fact]
    public void Every_compose_placement_names_a_service_and_profile_that_exist()
    {
        // A manifest that names a service the compose file does not define reads
        // as working orchestration and is not. The pin tests above cover only
        // image-digest targets, so a kit built from a checkout had no guard.
        var compose = File.ReadAllLines(InteropPaths.ComposeFile)
            .SkipWhile(l => l.TrimEnd() != "services:")
            .ToArray();
        var services = compose
            .Where(l => Indent(l) == 2 && l.TrimEnd().EndsWith(':') && !l.TrimStart().StartsWith('#'))
            .Select(l => l.Trim().TrimEnd(':'))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var target in Versions.Targets.Where(t => !string.IsNullOrEmpty(t.Compose?.Service)))
        {
            var placement = target.Compose!;
            services.Should().Contain(placement.Service,
                "interop/versions.json places '{0}' at compose service '{1}'", target.Key, placement.Service);

            foreach (var profile in placement.Profiles)
            {
                ComposeProfilesOf(compose, placement.Service).Should().Contain(profile,
                    "'{0}' claims profile '{1}'", placement.Service, profile);
            }
        }
    }

    /// <summary>The <c>profiles:</c> list of one service, as the inline form this file uses.</summary>
    private static IReadOnlyList<string> ComposeProfilesOf(IReadOnlyList<string> compose, string service)
    {
        var inService = false;
        foreach (var line in compose)
        {
            if (Indent(line) == 2 && line.TrimEnd().EndsWith(':') && !line.TrimStart().StartsWith('#'))
            {
                if (inService) break;
                inService = line.Trim() == service + ":";
                continue;
            }
            if (!inService || Indent(line) != 4 || !line.TrimStart().StartsWith("profiles:", StringComparison.Ordinal))
                continue;

            return line.Split('[', ']')[1].Split(',').Select(p => p.Trim()).ToList();
        }

        return [];
    }

    private static int Indent(string line) => line.Length - line.TrimStart().Length;

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
    public void Every_started_target_declares_how_readiness_is_observed()
    {
        // A target the harness STARTS must say how to tell it is up, or the
        // harness ends up sleeping and hoping. A tool it merely runs as a local
        // command — pinned by release asset, with no compose placement — has
        // nothing to become ready, and demanding a URL for it would mean
        // inventing one.
        foreach (var target in Versions.Targets.Where(t => t.Pin.ParsedKind != PinKind.ReleaseAsset))
        {
            target.Endpoints.ReadinessUrl.Should().NotBeNullOrWhiteSpace(
                "'{0}' must declare a readiness endpoint so the harness never guesses with a sleep", target.Name);
        }
    }

    [Fact]
    public void A_release_asset_is_pinned_by_hash_and_the_runner_enforces_it()
    {
        foreach (var target in Versions.Targets.Where(t => t.Pin.ParsedKind == PinKind.ReleaseAsset))
        {
            target.Pin.Sha256.Should().NotBeNullOrWhiteSpace(
                "'{0}' is a downloadable file, and a release asset can be replaced in place, " +
                "so the tag alone is not a pin", target.Name);
            target.Pin.Reference.Should().Contain(target.Pin.Asset!,
                "the reference must name the asset the hash belongs to");

            // A hash nothing checks is a comment. Some script has to fetch this
            // target and refuse a file that does not match the pin — whether it
            // reads the hash out of this manifest or carries it inline.
            var enforced = Directory
                .EnumerateFiles(Path.Combine(InteropPaths.RepositoryRoot, "scripts"), "*.sh",
                    SearchOption.AllDirectories)
                .Select(File.ReadAllText)
                .Any(script => script.Contains(target.Key, StringComparison.Ordinal)
                               && script.Contains("sha256sum", StringComparison.Ordinal));

            enforced.Should().BeTrue(
                "no script under scripts/ fetches '{0}' and compares it against its pinned hash",
                target.Name);
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
