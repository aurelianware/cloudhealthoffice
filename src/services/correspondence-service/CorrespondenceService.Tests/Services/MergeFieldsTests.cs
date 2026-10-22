using System.Text.Json.Nodes;
using CorrespondenceService.Services;

namespace CorrespondenceService.Tests.Services;

public class MergeFieldsTests
{
    private static string Render(string text, JsonObject data, out SortedSet<string> missing)
    {
        missing = new SortedSet<string>();
        return MergeFields.Render(text, MergeFields.FromJson(data), missing);
    }

    [Fact]
    public void Fills_nested_fields_without_html_encoding()
    {
        var text = Render("Dear {{ member.name }}, plan {{ plan.name }}.",
            new JsonObject { ["member"] = new JsonObject { ["name"] = "O'Brien & Sons <Jr>" }, ["plan"] = new JsonObject { ["name"] = "Gold" } },
            out var missing);
        text.Should().Be("Dear O'Brien & Sons <Jr>, plan Gold.");
        missing.Should().BeEmpty();
    }

    [Fact]
    public void Reports_every_missing_field()
    {
        Render("{{ member.name }} {{ appeal.id }} {{ appeal.decision }}", new JsonObject { ["member"] = new JsonObject { ["name"] = "A" } }, out var missing);
        missing.Should().BeEquivalentTo("appeal.id", "appeal.decision");
    }

    [Fact]
    public void A_field_with_a_default_is_not_missing()
    {
        var text = Render("{{ member.phone | default: 'the number on your ID card' }}", new JsonObject(), out var missing);
        text.Should().Be("the number on your ID card");
        missing.Should().BeEmpty();
    }

    [Fact]
    public void Loop_and_conditional_fields_work()
    {
        var data = new JsonObject
        {
            ["services"] = new JsonArray(new JsonObject { ["code"] = "97110" }, new JsonObject { ["code"] = "97140" }),
            ["upheld"] = true,
            ["amount"] = 12.5,
        };
        var text = Render("{% for s in services %}{{ s.code }};{% endfor %}{% if upheld %}upheld{% endif %} {{ amount }}", data, out var missing);
        text.Should().Be("97110;97140;upheld 12.5");
        missing.Should().BeEmpty();
    }

    [Fact]
    public void Invalid_liquid_does_not_parse()
    {
        MergeFields.TryParse("{% if x %}no end", out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
    }
}
