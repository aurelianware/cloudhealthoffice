using FhirService.Services.Consent;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace CloudHealthOffice.FhirService.Tests.Consent;

/// <summary>
/// With no consent registry URL, production must not fall back to an empty
/// catalog: Provider Access would read it as "nobody opted out" (ADR 021).
/// </summary>
public class ConsentSourceSelectionTests
{
    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "fhir-service";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Outside_development_with_no_catalog_the_unavailable_source_is_used(string environment)
        => ConsentSourceSelection.UseConfiguredCatalog(new Env(environment), Config()).Should().BeFalse();

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void Development_and_testing_use_the_catalog(string environment)
        => ConsentSourceSelection.UseConfiguredCatalog(new Env(environment), Config()).Should().BeTrue();

    [Fact]
    public void A_deliberately_configured_catalog_is_used_anywhere()
        => ConsentSourceSelection.UseConfiguredCatalog(new Env("Production"),
                Config(("Cms0057:PayerToPayerConsent:ConsentsByTenant:t:0:MemberId", "m")))
            .Should().BeTrue();

    [Fact]
    public async Task The_unavailable_source_never_answers_empty()
    {
        var source = new UnavailableConsentSource(Microsoft.Extensions.Logging.Abstractions.NullLogger<UnavailableConsentSource>.Instance);

        await ((Func<Task>)(() => source.GetConsentsAsync("t", "m"))).Should().ThrowAsync<ConsentRegistryUnavailableException>();
    }
}
