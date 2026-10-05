using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CloudHealthOffice.SmartAuth.Tests;

/// <summary>
/// smart-auth-service signs as the issuer in SmartAuth:Issuer, whatever Host
/// or X-Forwarded-* a request carries. That value is the one fhir-service
/// trusts (fhir-service appsettings SmartAuth:Issuer, same k8s host).
/// </summary>
[Collection(SmartAuthCollection.Name)]
public class SmartIssuerTests
{
    private const string ConfiguredIssuer = "https://auth.cloudhealthoffice.com/";

    private readonly SmartAuthTestFixture _fixture;
    private readonly SmartAuthDriver _smart;

    public SmartIssuerTests(SmartAuthTestFixture fixture)
    {
        _fixture = fixture;
        _smart = new SmartAuthDriver(fixture.Factory);
    }

    private static void Spoof(HttpRequestMessage request)
    {
        request.Headers.Host = "evil.example";
        request.Headers.Add("X-Forwarded-Host", "evil.example");
        request.Headers.Add("X-Forwarded-Proto", "http");
        request.Headers.Add("X-Forwarded-Prefix", "/evil");
    }

    [Fact]
    public async Task Discovery_WithASpoofedHost_AdvertisesTheConfiguredIssuer()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/.well-known/openid-configuration");
        Spoof(request);

        var resp = await _smart.Anonymous().SendAsync(request);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;

        doc.GetProperty("issuer").GetString().Should().Be(ConfiguredIssuer);
        doc.GetProperty("jwks_uri").GetString().Should().StartWith(ConfiguredIssuer);
        doc.GetProperty("token_endpoint").GetString().Should().StartWith(ConfiguredIssuer);
        doc.GetProperty("authorization_endpoint").GetString().Should().StartWith(ConfiguredIssuer);
        doc.ToString().Should().NotContain("evil");
    }

    [Fact]
    public async Task Tokens_WithASpoofedHost_CarryTheConfiguredIssuer()
    {
        var (status, body) = await _smart.ClientCredentialsAsync(
            "cho-payer-system", "system-secret-change-in-prod", "system/*.read", Spoof);
        status.Should().Be(HttpStatusCode.OK, body.ToString());
        SmartAuthDriver.Read(body.GetProperty("access_token").GetString()!).Issuer.Should().Be(ConfiguredIssuer);

        var browser = await _smart.SignInAsync("demo-member");
        var outcome = await SmartAuthDriver.AuthorizeAsync(browser, "smart-patient-app",
            "openid launch/patient patient/*.read", tamper: Spoof, redirectUri: "http://localhost:4200/callback");
        var (codeStatus, codeBody) = await _smart.ExchangeCodeAsync("smart-patient-app", outcome, tamper: Spoof);
        codeStatus.Should().Be(HttpStatusCode.OK, codeBody.ToString());
        SmartAuthDriver.Read(codeBody.GetProperty("access_token").GetString()!).Issuer.Should().Be(ConfiguredIssuer);
        SmartAuthDriver.Read(codeBody.GetProperty("id_token").GetString()!).Issuer.Should().Be(ConfiguredIssuer);
    }

    [Fact]
    public void ConfiguredIssuer_MatchesWhatFhirServiceTrusts()
    {
        var smartAuth = _fixture.Factory.Services.GetRequiredService<IConfiguration>()["SmartAuth:Issuer"];
        var fhirAppSettings = Path.Combine(RepoRoot(), "src/services/fhir-service/appsettings.json");
        var fhirIssuer = JsonDocument.Parse(File.ReadAllText(fhirAppSettings))
            .RootElement.GetProperty("SmartAuth").GetProperty("Issuer").GetString();

        new Uri(smartAuth!).Should().Be(new Uri(fhirIssuer!));
        new Uri(smartAuth!).Should().Be(new Uri(ConfiguredIssuer));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "services", "fhir-service")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
