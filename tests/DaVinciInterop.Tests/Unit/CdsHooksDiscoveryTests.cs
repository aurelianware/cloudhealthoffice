using System.Text.Json;
using FluentAssertions;

namespace DaVinciInterop.Tests.Unit;

/// <summary>
/// Discovery parsing and service selection decide which external endpoint a
/// scenario talks to. Getting it wrong silently — picking a different service
/// after an upstream reorder, or accepting a document that is not a discovery
/// response — would make an interop result meaningless rather than failed.
/// </summary>
[Trait("Category", "DaVinciInteropUnit")]
public sealed class CdsHooksDiscoveryTests
{
    private const string PayerDiscovery = """
    {
      "services": [
        { "hook": "order-select", "id": "order-select-crd", "title": "CRD Order Select Hook",
          "prefetch": { "patient": "Patient/{{context.patientId}}", "coverage": "Coverage?patient={{context.patientId}}" },
          "extension": { "davinci-crd.version": ["2.2"] } },
        { "hook": "order-sign", "id": "order-sign-crd", "title": "CRD Order Sign Hook",
          "prefetch": { "coverage": "Coverage?patient={{context.patientId}}", "patient": "Patient/{{context.patientId}}" },
          "extension": { "davinci-crd.version": ["2.2"] } },
        { "hook": "order-sign", "id": "plain-order-sign", "title": "A non-CRD service on the same hook" }
      ]
    }
    """;

    private static CdsHooksDiscovery Parse(string json) =>
        JsonSerializer.Deserialize<CdsHooksDiscovery>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Fact]
    public void A_discovery_document_parses_into_services_with_their_metadata()
    {
        var discovery = Parse(PayerDiscovery);

        discovery.Services.Should().HaveCount(3);
        var orderSign = discovery.Services.Single(s => s.Id == "order-sign-crd");
        orderSign.Hook.Should().Be("order-sign");
        orderSign.Title.Should().Be("CRD Order Sign Hook");
    }

    [Fact]
    public void The_crd_version_extension_is_extracted_from_discovery()
    {
        var discovery = Parse(PayerDiscovery);

        discovery.Services.Single(s => s.Id == "order-sign-crd").AdvertisedCrdVersions
            .Should().BeEquivalentTo(["2.2"]);
        CdsHooksServiceSelector.AdvertisedCrdVersions(discovery).Should().BeEquivalentTo(["2.2"]);
    }

    [Fact]
    public void A_service_without_the_extension_advertises_no_crd_version()
    {
        Parse(PayerDiscovery).Services.Single(s => s.Id == "plain-order-sign")
            .AdvertisedCrdVersions.Should().BeEmpty();
    }

    [Fact]
    public void Advertised_prefetch_keys_are_extracted_in_a_stable_order()
    {
        var service = Parse(PayerDiscovery).Services.Single(s => s.Id == "order-sign-crd");

        // Ordered rather than insertion-order, so an evidence diff between runs
        // reflects a real change in what the payer advertises.
        CdsHooksServiceSelector.AdvertisedPrefetchKeys(service).Should().Equal("coverage", "patient");
    }

    [Fact]
    public void Selection_matches_on_hook_and_crd_capability_not_list_position()
    {
        var selected = CdsHooksServiceSelector.Select(Parse(PayerDiscovery), "order-sign");

        selected.Id.Should().Be("order-sign-crd",
            "the non-CRD service shares the hook, so requiring the CRD extension is what disambiguates");
    }

    [Fact]
    public void Selection_fails_loudly_when_no_service_offers_the_hook()
    {
        var act = () => CdsHooksServiceSelector.Select(Parse(PayerDiscovery), "encounter-start");

        act.Should().Throw<CdsHooksServiceSelectionException>()
            .WithMessage("*encounter-start*")
            .WithMessage("*order-sign-crd*", "the error names what was advertised so the failure is diagnosable");
    }

    [Fact]
    public void Selection_refuses_to_guess_when_several_crd_services_share_a_hook()
    {
        var ambiguous = Parse("""
        {"services":[
          {"hook":"order-sign","id":"a","extension":{"davinci-crd.version":["2.2"]}},
          {"hook":"order-sign","id":"b","extension":{"davinci-crd.version":["2.2"]}}
        ]}
        """);

        var act = () => CdsHooksServiceSelector.Select(ambiguous, "order-sign");

        act.Should().Throw<CdsHooksServiceSelectionException>().WithMessage("*disambiguate*");
    }

    [Fact]
    public void Selection_refuses_a_service_with_no_id_because_it_cannot_be_invoked()
    {
        var idless = Parse("""{"services":[{"hook":"order-sign","id":"","extension":{"davinci-crd.version":["2.2"]}}]}""");

        var act = () => CdsHooksServiceSelector.Select(idless, "order-sign");

        act.Should().Throw<CdsHooksServiceSelectionException>();
    }

    [Fact]
    public void A_valid_discovery_document_reports_no_violations()
    {
        CdsHooksServiceSelector.DiscoveryViolations(Parse(PayerDiscovery)).Should().BeEmpty();
    }

    [Fact]
    public void An_unparseable_discovery_document_is_reported_rather_than_treated_as_empty()
    {
        CdsHooksServiceSelector.DiscoveryViolations(null)
            .Should().ContainSingle().Which.Should().Contain("could not be parsed");
    }

    [Fact]
    public void A_discovery_document_advertising_nothing_is_a_violation()
    {
        CdsHooksServiceSelector.DiscoveryViolations(Parse("""{"services":[]}"""))
            .Should().ContainSingle().Which.Should().Contain("no services");
    }

    [Fact]
    public void Services_missing_an_id_or_hook_are_reported_with_their_position()
    {
        var malformed = Parse("""{"services":[{"hook":"order-sign","id":""},{"id":"x","hook":""}]}""");

        var violations = CdsHooksServiceSelector.DiscoveryViolations(malformed);

        violations.Should().Contain(v => v.Contains("services[0]") && v.Contains("no id"));
        violations.Should().Contain(v => v.Contains("services[1]") && v.Contains("no hook"));
    }

    [Fact]
    public void Only_crd_capable_services_are_listed_as_crd_services()
    {
        CdsHooksServiceSelector.CrdServices(Parse(PayerDiscovery))
            .Select(s => s.Id).Should().BeEquivalentTo(["order-select-crd", "order-sign-crd"]);
    }
}
