using System.Text.RegularExpressions;
using FluentAssertions;

namespace DaVinciInterop.Tests.Unit;

/// <summary>
/// <see cref="ChoUnderTest"/> states where CHO is while a conformance kit judges
/// it. Every value there mirrors a default in the Compose file, and a drift
/// between the two does not fail loudly: the harness would seed one stack and
/// point the kit at another, and the kit would report CHO failing everything.
/// </summary>
[Trait("Category", "DaVinciInteropUnit")]
public sealed class ChoUnderTestAddressTests
{
    private static readonly string Compose = File.ReadAllText(InteropPaths.ComposeFile);

    [Fact]
    public void Every_host_port_matches_the_compose_default_it_mirrors()
    {
        foreach (var (variable, expected) in ChoUnderTest.PortDefaults)
        {
            var match = Regex.Match(Compose, $@"127\.0\.0\.1:\$\{{{Regex.Escape(variable)}:-(\d+)\}}:");

            match.Success.Should().BeTrue(
                "{0} must be published by interop/docker-compose.interop.yml for the harness to reach it",
                variable);

            int.Parse(match.Groups[1].Value).Should().Be(expected,
                "ChoUnderTest and the compose file must agree on {0}", variable);
        }
    }

    [Fact]
    public void The_address_the_kit_is_given_is_a_compose_service_not_loopback()
    {
        // The mistake this guards: a kit runs inside the Compose network, so a
        // 127.0.0.1 URL points it at its own container. Nothing answers, and
        // Inferno reports that identically to CHO failing every test.
        ChoUnderTest.ContainerFhirBaseUrl.Should().NotContain("127.0.0.1");
        ChoUnderTest.ContainerFhirBaseUrl.Should().NotContain("localhost");
        ChoUnderTest.ContainerFhirBaseUrl.Should().StartWith("http://cho-fhir-service:");

        Compose.Should().Contain("cho-fhir-service:",
            "the address handed to the kit must name a service the compose file defines");
    }

    [Fact]
    public void The_addresses_the_harness_uses_are_loopback()
    {
        ChoUnderTest.HostFhirBaseUrl.Should().StartWith("http://127.0.0.1:");

        ChoUnderTest.SeedServiceUrls.Values.Should().OnlyContain(
            url => url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal),
            "the seeder runs on the host and reaches CHO through the published loopback ports");
    }

    [Fact]
    public void The_profile_named_is_the_one_the_compose_file_defines()
    {
        Compose.Should().Contain($"profiles: [{ChoUnderTest.Profile}]");
    }
}
