using DaVinciInterop.Tests.Harness;
using FluentAssertions;

namespace DaVinciInterop.Tests.Unit;

/// <summary>The parts of the harness's SMART patient launch that need no running stack.</summary>
public sealed class InfernoPatientAppTests
{
    private const string Redirect = "https://inferno-carin-bb.invalid/callback";

    [Fact]
    public void The_code_challenge_is_rfc_7636s_s256()
        // Expected value from `printf %s <verifier> | openssl dgst -sha256 -binary | base64`,
        // made URL-safe and unpadded (RFC 7636 §4.2).
        => InfernoPatientApp.CodeChallenge("dBjftJeZ4CVP-mJ92K9CyY0Vb8Qr8h-kqVc4lK5Mf9s")
            .Should().Be("hZuErEl1_nA2h25e6tuDBj802y5q_sYL7MFqw6cGROQ");

    [Fact]
    public void The_consent_page_is_read_as_the_fields_a_browser_would_post()
    {
        const string page = """
            <form method="post" action="/connect/authorize">
            <input type="hidden" name="client_id" value="inferno-carin-bb"><input type="hidden" name="scope" value="launch/patient patient/*.read"><input type="hidden" name="redirect_uri" value="https://a.invalid/cb?x=1&amp;y=2"><input type="hidden" name="consent_token" value="tok">
            <button name="consent_decision" value="approve">Allow</button>
            """;

        InfernoPatientApp.ConsentFields(page).Should().Equal(
            new KeyValuePair<string, string>("client_id", "inferno-carin-bb"),
            new KeyValuePair<string, string>("scope", "launch/patient patient/*.read"),
            new KeyValuePair<string, string>("redirect_uri", "https://a.invalid/cb?x=1&y=2"),
            new KeyValuePair<string, string>("consent_token", "tok"));
    }

    [Fact]
    public void The_code_is_read_off_the_redirect_when_the_state_comes_back()
        => InfernoPatientApp.CodeFromRedirect(new Uri(Redirect + "?code=abc&state=s1"), Redirect, "s1")
            .Should().Be("abc");

    [Theory]
    [InlineData(Redirect + "?code=abc&state=other")]
    [InlineData(Redirect + "?error=access_denied&state=s1")]
    [InlineData("https://elsewhere.invalid/callback?code=abc&state=s1")]
    [InlineData(Redirect + "?state=s1")]
    public void A_redirect_that_is_not_a_clean_answer_to_this_request_stops_the_run(string location)
    {
        var act = () => InfernoPatientApp.CodeFromRedirect(new Uri(location), Redirect, "s1");
        act.Should().Throw<InteropEnvironmentException>();
    }
}
