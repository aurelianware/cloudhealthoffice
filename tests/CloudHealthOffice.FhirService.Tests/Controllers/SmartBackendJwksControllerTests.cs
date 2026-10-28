using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CloudHealthOffice.FhirService.Tests.Controllers;

/// <summary>
/// <c>GET /fhir/r4/.well-known/smart-backend-jwks.json</c>: the public keys
/// CHO signs its outbound client assertions with (G-42), served without a
/// token so a peer can use it as CHO's <c>jwks_uri</c>.
/// </summary>
public class SmartBackendJwksControllerTests
{
    private const string Path = "/fhir/r4/.well-known/smart-backend-jwks.json";

    [Fact]
    public async Task NoSigningKey_Is404()
    {
        await using var factory = new FhirTestWebAppFactory();
        var response = await factory.CreateClient().GetAsync(Path);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ConfiguredKey_IsPublishedWithoutAToken_PublicPartsOnly()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        await using var factory = new FhirTestWebAppFactory().WithWebHostBuilder(b =>
            b.UseSetting("SmartBackendClients:SigningKeys:0:PrivateKeyPem", ec.ExportECPrivateKeyPem()));

        var response = await factory.CreateClient().GetAsync(Path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.MaxAge.Should().Be(TimeSpan.FromMinutes(5));
        var jwk = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["keys"]![0]!.AsObject();
        jwk["kty"]!.GetValue<string>().Should().Be("EC");
        jwk["alg"]!.GetValue<string>().Should().Be("ES384");
        jwk.ContainsKey("d").Should().BeFalse();
    }
}
