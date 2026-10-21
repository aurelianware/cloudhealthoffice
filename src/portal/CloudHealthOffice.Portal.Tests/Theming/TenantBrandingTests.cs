using CloudHealthOffice.Portal.Theming;
using Microsoft.Extensions.Configuration;

namespace CloudHealthOffice.Portal.Tests.Theming;

public class TenantBrandingTests
{
    [Theory]
    [InlineData("#FFFFFF", "#000000", 21.0)]
    [InlineData("#0E7490", "#FFFFFF", 5.36)]
    [InlineData("#777777", "#FFFFFF", 4.48)]
    public void Contrast_ratio_matches_WCAG(string a, string b, double expected)
        => ColorContrast.Ratio(a, b).Should().BeApproximately(expected, 0.02);

    [Theory]
    [InlineData("#00539B")]   // a typical plan navy
    [InlineData("#0e7490")]
    public void Readable_brand_color_is_accepted(string accent)
        => TenantBrandingStyle.ValidAccent(accent).Should().Be(accent.ToUpperInvariant());

    [Theory]
    [InlineData("#00FFFF")]   // Sentinel cyan: unreadable on white
    [InlineData("#FFCC00")]   // brand yellow
    [InlineData("red")]
    [InlineData("#123")]
    [InlineData("#0E7490;--cho-error:#fff")]
    [InlineData(null)]
    public void Unreadable_or_malformed_brand_color_is_ignored(string? accent)
    {
        TenantBrandingStyle.ValidAccent(accent).Should().BeNull();
        TenantBrandingStyle.AccentCss(accent).Should().BeNull();
    }

    [Fact]
    public void Accent_css_overrides_only_the_accent_tokens_for_enterprise()
    {
        var css = TenantBrandingStyle.AccentCss("#00539B");

        css.Should().StartWith("html:root[data-cho-theme=\"enterprise\"]{");
        css.Should().Contain("--cho-accent:#00539B;");
        css.Should().Contain("--cho-accent-hover:#");
        css.Should().NotContain("--cho-error").And.NotContain("--cho-success");
    }

    [Theory]
    [InlineData("/branding/acme.svg", "/branding/acme.svg")]
    [InlineData("https://cdn.example.com/logo.png", "https://cdn.example.com/logo.png")]
    [InlineData("//evil.example.com/x.png", null)]
    [InlineData("http://cdn.example.com/logo.png", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("", null)]
    public void Logo_url_must_be_site_relative_or_https(string url, string? expected)
        => TenantBrandingStyle.ValidLogoUrl(url).Should().Be(expected);

    [Fact]
    public void Configuration_provider_reads_branding_by_tenant_id()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TenantBranding:acme-health:DisplayName"] = "Acme Health Plan",
            ["TenantBranding:acme-health:LogoUrl"] = "/branding/acme.svg",
            ["TenantBranding:acme-health:AccentColor"] = "#00539B",
        }).Build();
        var provider = new ConfigurationTenantBrandingProvider(config);

        provider.GetBranding("acme-health").Should().BeEquivalentTo(new TenantBranding
        {
            DisplayName = "Acme Health Plan",
            LogoUrl = "/branding/acme.svg",
            AccentColor = "#00539B",
        });
        provider.GetBranding("other").Should().BeNull();
        provider.GetBranding(null).Should().BeNull();
    }

    [Theory]
    [InlineData(null, null, "enterprise")]
    [InlineData("sentinel", null, "sentinel")]
    [InlineData(" SENTINEL ", null, "sentinel")]
    [InlineData("neon-pink", "sentinel", "sentinel")]
    [InlineData("neon-pink", "bogus", "enterprise")]
    public void Theme_name_normalizes_to_a_known_theme(string? value, string? fallback, string expected)
        => ChoThemeNames.Normalize(value, fallback).Should().Be(expected);
}
