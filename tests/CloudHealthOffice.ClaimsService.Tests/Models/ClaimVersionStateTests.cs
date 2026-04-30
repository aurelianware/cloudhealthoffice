using ClaimsService.Models;
using FluentAssertions;
using System.Text.Json;

namespace CloudHealthOffice.ClaimsService.Tests.Models;

/// <summary>
/// PR #705 enum convention: <c>Unknown=0</c> as default, JSON wire format
/// is the string name, not the integer. These tests pin the enum's wire
/// shape so a careless renumbering or a forgotten <c>JsonStringEnumConverter</c>
/// is caught at test time.
/// </summary>
public class ClaimVersionStateTests
{
    [Fact]
    public void Default_value_is_Unknown()
    {
        default(ClaimVersionState).Should().Be(ClaimVersionState.Unknown);
        ((int)default(ClaimVersionState)).Should().Be(0);
    }

    [Theory]
    [InlineData(ClaimVersionState.Unknown, 0)]
    [InlineData(ClaimVersionState.Draft, 1)]
    [InlineData(ClaimVersionState.Submitted, 2)]
    [InlineData(ClaimVersionState.Adjudicated, 3)]
    [InlineData(ClaimVersionState.Paid, 4)]
    [InlineData(ClaimVersionState.Denied, 5)]
    [InlineData(ClaimVersionState.Adjusted, 6)]
    [InlineData(ClaimVersionState.Voided, 7)]
    public void Underlying_integer_values_are_pinned(ClaimVersionState state, int expected)
    {
        ((int)state).Should().Be(expected);
    }

    [Fact]
    public void Serializes_as_string_via_JsonStringEnumConverter()
    {
        // The wire format is the enum name, not the int. Persistence
        // layers and Kafka payloads depend on this; flipping the
        // converter would be a silent breaking change.
        var json = JsonSerializer.Serialize(ClaimVersionState.Adjudicated);
        json.Should().Be("\"Adjudicated\"");
    }

    [Fact]
    public void Deserializes_from_string_name()
    {
        var state = JsonSerializer.Deserialize<ClaimVersionState>("\"Paid\"");
        state.Should().Be(ClaimVersionState.Paid);
    }

    [Fact]
    public void Missing_field_in_payload_deserializes_to_Unknown()
    {
        // Legacy documents missing VersionState entirely deserialize to the
        // default (Unknown), which the repository hydration step then maps
        // onto a real state. This test guards the hydration entry point.
        var doc = JsonSerializer.Deserialize<Holder>("{}");
        doc.Should().NotBeNull();
        doc!.VersionState.Should().Be(ClaimVersionState.Unknown);
    }

    private sealed class Holder
    {
        public ClaimVersionState VersionState { get; set; }
    }
}
