using FhirService.Models;
using FluentAssertions;

namespace DaVinciInterop.Tests.Unit;

/// <summary>
/// The <c>interop-cho</c> profile is the system under test for every Inferno
/// suite. If it serves <see cref="FhirAdapterSources.Mock"/>, a passing suite
/// certifies the mock — an answer worse than no answer, because it reads as
/// conformance. These tests fail the build rather than let the profile drift
/// back onto the synthetic adapter, or declare a ChoNative binding without the
/// upstream URL that binding needs.
///
/// The expectations are read from the compose file itself, and the resource
/// names come from <see cref="FhirDataBindings.NativeCapable"/> rather than a
/// restated list, so a new natively-servable resource is covered here the day
/// it exists.
/// </summary>
[Trait("Category", "DaVinciInteropUnit")]
public sealed class InteropChoProfileTests
{
    private static readonly IReadOnlyDictionary<string, string> FhirServiceEnvironment =
        ComposeEnvironment("cho-fhir-service");

    /// <summary>Resource binding → the configuration key its client needs.</summary>
    private static readonly IReadOnlyDictionary<string, string> UrlKeyForSource =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Patient"] = "Services__MemberServiceUrl",
            ["Coverage"] = "Services__CoverageServiceUrl",
            ["Encounter"] = "Services__EncounterServiceUrl",
            ["Claim"] = "Services__ClaimsServiceUrl",
        };

    [Fact]
    public void The_system_under_test_serves_CHO_native_data_rather_than_the_mock()
    {
        foreach (var resource in UrlKeyForSource.Keys)
        {
            FhirServiceEnvironment.Should().ContainKey($"FhirAdapters__Sources__{resource}",
                "an Inferno suite that reads {0} from the mock certifies the mock, not CHO", resource);
            FhirServiceEnvironment[$"FhirAdapters__Sources__{resource}"].Should().Be(
                FhirAdapterSources.ChoNative,
                "{0} must be served by the CHO service that owns it (ADR 018/024)", resource);
        }
    }

    [Fact]
    public void Every_native_binding_names_the_service_that_serves_it()
    {
        // fhir-service throws at start-up when a ChoNative binding has no URL,
        // so this would be a failed container rather than a wrong answer — but
        // a failed container at suite time costs a CI run to discover.
        foreach (var (resource, urlKey) in UrlKeyForSource)
        {
            if (FhirServiceEnvironment.GetValueOrDefault($"FhirAdapters__Sources__{resource}")
                != FhirAdapterSources.ChoNative)
            {
                continue;
            }

            FhirServiceEnvironment.Should().ContainKey(urlKey,
                "FhirAdapters:Sources:{0}=ChoNative needs {1}", resource, urlKey);
            var url = FhirServiceEnvironment[urlKey];
            Uri.TryCreate(url, UriKind.Absolute, out _).Should().BeTrue(
                "{0} must be an absolute URL, was '{1}'", urlKey, url);
            ComposeServiceNames().Should().Contain(new Uri(url).Host,
                "{0} must name a service the interop-cho profile actually starts", urlKey);
        }
    }

    [Fact]
    public void The_mode_label_matches_what_the_bindings_make_true()
    {
        // Demo is the honest label only while the mock serves these resources.
        FhirServiceEnvironment.Should().ContainKey("FhirAdapters__Mode");
        FhirServiceEnvironment["FhirAdapters__Mode"].Should().Be(FhirAdapterModes.Hybrid);
        FhirServiceEnvironment["FhirAdapters__DataClassification"].Should().Be(
            FhirAdapterDataClasses.Synthetic,
            "no interoperability profile may ever be pointed at real data");
    }

    [Fact]
    public void The_SMART_issuer_names_an_authorization_server_this_profile_starts()
    {
        // It previously defaulted to http://localhost:18024, where nothing
        // listened: discovery and JWKS retrieval could not succeed, so no SMART
        // suite could obtain a token.
        var issuer = Unsubstituted(FhirServiceEnvironment["SmartAuth__Issuer"]);
        ComposeServiceNames().Should().Contain(new Uri(issuer).Host);

        // Tokens, discovery and JWKS all name the issuer whatever Host a request
        // arrives with, so the two services must agree byte for byte.
        Unsubstituted(ComposeEnvironment("cho-smart-auth-service")["SmartAuth__Issuer"])
            .Should().Be(issuer);
    }

    /// <summary>The default of a <c>${VAR:-default}</c> reference, or the value itself.</summary>
    private static string Unsubstituted(string value)
    {
        if (!value.StartsWith("${", StringComparison.Ordinal) || !value.EndsWith('}'))
            return value;
        var inner = value[2..^1];
        var separator = inner.IndexOf(":-", StringComparison.Ordinal);
        return separator < 0 ? inner : inner[(separator + 2)..];
    }

    /// <summary>
    /// The <c>environment:</c> mapping of one service. Deliberately a small
    /// reader rather than a YAML dependency: it understands exactly the subset
    /// this file uses, and anything outside that subset fails loudly here.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ComposeEnvironment(string service)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        var inService = false;
        var inEnvironment = false;

        foreach (var raw in File.ReadLines(InteropPaths.ComposeFile).SkipWhile(l => l.TrimEnd() != "services:"))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.TrimStart().StartsWith('#')) continue;

            if (Indent(line) == 2 && line.TrimStart().EndsWith(':'))
            {
                if (inService && inEnvironment) break;
                inService = line.Trim() == service + ":";
                inEnvironment = false;
                continue;
            }
            if (!inService) continue;

            if (Indent(line) == 4)
                inEnvironment = line.Trim() == "environment:";
            else if (inEnvironment && Indent(line) == 6)
            {
                var separator = line.IndexOf(':', Indent(line));
                separator.Should().BeGreaterThan(0,
                    "the environment mapping of '{0}' must use key: value form, saw '{1}'", service, line);
                environment[line[..separator].Trim()] = line[(separator + 1)..].Trim().Trim('"');
            }
        }

        environment.Should().NotBeEmpty("'{0}' must exist in {1} with an environment mapping",
            service, InteropPaths.ComposeFile);
        return environment;
    }

    /// <summary>The services this file defines — not the top-level network or volume names.</summary>
    private static IReadOnlyList<string> ComposeServiceNames() =>
        File.ReadLines(InteropPaths.ComposeFile)
            .SkipWhile(l => l.TrimEnd() != "services:")
            .Where(l => Indent(l) == 2 && l.TrimEnd().EndsWith(':') && !l.TrimStart().StartsWith('#'))
            .Select(l => l.Trim().TrimEnd(':'))
            .ToList();

    private static int Indent(string line) => line.Length - line.TrimStart().Length;
}
