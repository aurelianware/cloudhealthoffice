using FluentAssertions;

namespace DaVinciInterop.Tests.Unit;

/// <summary>
/// Redaction is the guardrail that lets interop artifacts be reviewed and, once
/// sanitized, published. These tests hold that guardrail in place.
/// </summary>
[Trait("Category", "DaVinciInteropUnit")]
public sealed class RedactionTests
{
    [Theory]
    [InlineData("Authorization")]
    [InlineData("authorization")]
    [InlineData("Proxy-Authorization")]
    [InlineData("Cookie")]
    [InlineData("X-Api-Key")]
    public void Credential_headers_never_reach_an_artifact(string header)
    {
        Redaction.HeaderValue(header, "Bearer super-secret-value").Should().Be(Redaction.Placeholder);
    }

    [Fact]
    public void Ordinary_headers_are_preserved_so_the_exchange_stays_legible()
    {
        Redaction.HeaderValue("Accept", "application/fhir+json").Should().Be("application/fhir+json");
    }

    [Fact]
    public void Token_bearing_json_members_are_redacted_but_still_named()
    {
        var body = """{"access_token":"abc.def.ghi","token_type":"Bearer","client_secret":"hunter2"}""";

        var redacted = Redaction.Body(body);

        redacted.Should().NotContain("abc.def.ghi");
        redacted.Should().NotContain("hunter2");
        redacted.Should().Contain("access_token");
        redacted.Should().Contain("client_secret");
    }

    [Fact]
    public void Bearer_credentials_echoed_inside_a_body_are_redacted()
    {
        Redaction.Body("Request rejected: Authorization: Bearer eyJhbGciOi.payloadpart.signature")
            .Should().NotContain("eyJhbGciOi.payloadpart.signature");
    }

    [Fact]
    public void Private_key_blocks_are_redacted_whole()
    {
        var body = "-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKC\n-----END RSA PRIVATE KEY-----";

        Redaction.Body(body).Should().Be(Redaction.Placeholder);
    }

    [Fact]
    public void Credentials_in_a_url_are_stripped_but_the_endpoint_stays_reproducible()
    {
        var redacted = Redaction.Url("https://user:pass@payer.example/fhir/Claim/$submit?access_token=abc&_format=json");

        redacted.Should().NotContain("pass");
        redacted.Should().NotContain("abc");
        redacted.Should().Contain("payer.example");
        redacted.Should().Contain("_format=json");
    }

    [Fact]
    public void A_plain_url_is_left_alone()
    {
        const string url = "http://127.0.0.1:18081/fhir/metadata";

        Redaction.Url(url).Should().Be(url);
    }
}
