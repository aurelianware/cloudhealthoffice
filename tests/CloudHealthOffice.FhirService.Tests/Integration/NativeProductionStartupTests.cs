using System.Security.Cryptography;
using CloudHealthOffice.Testing.FhirService;
using FhirService.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CloudHealthOffice.FhirService.Tests.Integration;

/// <summary>Production composition with test-owned database stores; no external deployment proof.</summary>
public class NativeProductionStartupTests
{
    [Fact]
    public void ProductionComposition_StartsWithNativeSourcesAndSyntheticDataDisabled()
    {
        using var rsa = RSA.Create(2048);
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseUnreachableMongoWithInMemoryStores();
            var settings = new Dictionary<string, string?>
            {
                ["SyntheticData:Allowed"] = "false",
                ["ChoAuth:Audience"] = "cho-api",
                ["ChoAuth:Issuers:0:Issuer"] = "cho-token-service",
                ["ChoAuth:Issuers:0:PublicKeyPem"] = rsa.ExportSubjectPublicKeyInfoPem(),
                ["SmartAuth:Mode"] = "ExternalIssuer",
                ["SmartAuth:TrustedIssuers:0:Issuer"] = "https://auth.example.test",
                ["SmartAuth:TrustedIssuers:0:Audiences:0"] = "fhir-api",
                ["SmartAuth:TrustedIssuers:0:IssuesChoClientRegistrations"] = "true",
                ["Appeals:UseMockAdapter"] = "false",
                ["Services:MemberServiceUrl"] = "http://127.0.0.1:1/",
                ["Services:CoverageServiceUrl"] = "http://127.0.0.1:1/",
                ["Services:EncounterServiceUrl"] = "http://127.0.0.1:1/",
                ["Services:ClaimsServiceUrl"] = "http://127.0.0.1:1/",
            };
            foreach (var resource in new[] { "Patient", "Coverage", "Encounter", "Claim", "PayerToPayer" })
                settings["FhirAdapters:Sources:" + resource] = "ChoNative";
            foreach (var (key, value) in settings) builder.UseSetting(key, value);
        });

        using var client = factory.CreateClient();
        factory.Services.GetRequiredService<IHostEnvironment>().EnvironmentName.Should().Be("Production");
        factory.Services.GetService<IPatientAccessDataProvider>().Should().BeNull();
        factory.Services.GetService<IChoMemberDirectory>().Should().BeNull();
    }
}
