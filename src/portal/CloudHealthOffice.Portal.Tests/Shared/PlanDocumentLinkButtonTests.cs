using Bunit;
using CloudHealthOffice.Portal.Services;
using CloudHealthOffice.Portal.Shared;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Shared;

/// <summary>
/// Plan document links come from benefit-plan-service data; the member
/// dialog must only ever render an https or relative href.
/// </summary>
public class PlanDocumentLinkButtonTests : TestContext
{
    public PlanDocumentLinkButtonTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<PlanDocumentLinkButton> Render(string? location) =>
        RenderComponent<PlanDocumentLinkButton>(p => p.Add(x => x.Document, new PlanDocumentLink
        {
            DocType = "SBC",
            DisplayName = "Summary of Benefits",
            Location = location!,
        }));

    [Theory]
    [InlineData("javascript:alert(document.cookie)")]
    [InlineData(" javascript:alert(1)")]
    [InlineData("JAVASCRIPT:alert(1)")]
    [InlineData("java\tscript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("http://docs.payer.example/sbc.pdf")]
    [InlineData("https://user:pw@docs.payer.example/sbc.pdf")]
    [InlineData("//evil.example/sbc.pdf")]
    [InlineData("/\\evil.example/sbc.pdf")]
    [InlineData(null)]
    [InlineData("")]
    public void Does_not_render_unsafe_href(string? location)
    {
        var cut = Render(location);

        cut.FindAll("a").Should().BeEmpty();
        cut.Markup.Should().NotContain("href=");
        cut.Markup.Should().Contain("Summary of Benefits");
        cut.Markup.Should().Contain("unavailable");
    }

    [Theory]
    [InlineData("https://docs.payer.example/sbc.pdf")]
    [InlineData("documentreference/abc-123")]
    [InlineData("/documents/sbc.pdf")]
    public void Renders_https_or_relative_href_in_new_tab_with_noopener(string location)
    {
        var cut = Render(location);

        var a = cut.Find("a");
        a.GetAttribute("href").Should().Be(location);
        a.GetAttribute("target").Should().Be("_blank");
        a.GetAttribute("rel").Should().Be("noopener noreferrer");
    }
}
