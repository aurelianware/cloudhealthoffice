using FluentAssertions;

namespace DaVinciInterop.Tests.Unit;

/// <summary>
/// Response validation is what separates "the endpoint answered" from "the
/// exchange was standards-conformant". A malformed response must be reported, not
/// silently accepted because it happened to arrive with HTTP 200.
/// </summary>
[Trait("Category", "DaVinciInteropUnit")]
public sealed class CdsHooksResponseTests
{
    [Fact]
    public void A_response_with_cards_and_system_actions_parses()
    {
        var response = CdsHooksResponse.Parse("""
        {
          "cards": [{ "summary": "Prior authorization required", "indicator": "warning",
                      "source": { "label": "Payer", "url": "https://payer.example" } }],
          "systemActions": [{ "type": "update", "resource": { "resourceType": "DeviceRequest" } }]
        }
        """);

        response.Should().NotBeNull();
        response!.Cards.Should().ContainSingle();
        response.SystemActions.Should().ContainSingle();
        response.SystemActions![0].ResourceType.Should().Be("DeviceRequest");
        response.ProtocolViolations().Should().BeEmpty();
    }

    [Fact]
    public void An_empty_cards_array_is_valid_and_is_not_the_same_as_a_missing_one()
    {
        var response = CdsHooksResponse.Parse("""{"cards":[]}""");

        response!.HasCardsMember.Should().BeTrue();
        response.ProtocolViolations().Should().BeEmpty(
            "a payer with nothing to say still returns cards:[]");
    }

    [Fact]
    public void A_response_without_a_cards_member_is_a_protocol_violation()
    {
        var response = CdsHooksResponse.Parse("""{"systemActions":[]}""");

        response!.HasCardsMember.Should().BeFalse();
        response.ProtocolViolations().Should().ContainSingle()
            .Which.Should().Contain("no 'cards' member");
    }

    [Fact]
    public void Unparseable_json_yields_null_rather_than_an_exception()
    {
        CdsHooksResponse.Parse("this is not json").Should().BeNull();
    }

    [Theory]
    [InlineData("""{"cards":[{"indicator":"info","source":{"label":"P"}}]}""", "summary is required")]
    [InlineData("""{"cards":[{"summary":"s","source":{"label":"P"}}]}""", "indicator is required")]
    [InlineData("""{"cards":[{"summary":"s","indicator":"info"}]}""", "source is required")]
    [InlineData("""{"cards":[{"summary":"s","indicator":"info","source":{}}]}""", "source.label is required")]
    [InlineData("""{"cards":[{"summary":"s","indicator":"urgent","source":{"label":"P"}}]}""", "not one of info|warning|critical")]
    public void Malformed_cards_are_reported_with_what_is_wrong(string json, string expected)
    {
        CdsHooksResponse.Parse(json)!.ProtocolViolations()
            .Should().ContainSingle().Which.Should().Contain(expected);
    }

    [Fact]
    public void A_card_summary_beyond_the_specified_limit_is_reported()
    {
        var json = "{\"cards\":[{\"summary\":\"" + new string('x', 141)
                   + "\",\"indicator\":\"info\",\"source\":{\"label\":\"P\"}}]}";

        CdsHooksResponse.Parse(json)!.ProtocolViolations()
            .Should().ContainSingle().Which.Should().Contain("140-character limit");
    }

    [Fact]
    public void Violations_name_the_offending_card_position()
    {
        var response = CdsHooksResponse.Parse("""
        {"cards":[
          {"summary":"ok","indicator":"info","source":{"label":"P"}},
          {"summary":"bad","indicator":"nope","source":{"label":"P"}}
        ]}
        """);

        response!.ProtocolViolations().Should().ContainSingle().Which.Should().StartWith("cards[1]:");
    }

    [Fact]
    public void A_request_serializes_to_the_cds_hooks_wire_shape()
    {
        var request = SyntheticInteropData.CrdOrderRequest(
            "order-sign", "L8000", "http://host.docker.internal:1234/fhir", "abc-123");

        var json = request.ToJson();

        json.Should().Contain("\"hook\": \"order-sign\"");
        json.Should().Contain("\"hookInstance\": \"abc-123\"");
        json.Should().Contain("\"draftOrders\"");
        json.Should().Contain("\"prefetch\"");
        json.Should().Contain("L8000");
    }
}
