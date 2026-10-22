using CloudHealthOffice.ClinicalCriteria.Abstractions;
using CloudHealthOffice.ClinicalCriteria.Configuration;
using CloudHealthOffice.PriorAuthRuleEngine.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace CloudHealthOffice.ClinicalCriteria.Tests;

public class RegistrationTests
{
    private static ServiceProvider Build(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IPriorAuthRuleEngine>());
        services.AddClinicalCriteria(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void A_deployment_without_a_licence_registers_only_the_in_repo_provider()
    {
        using var sp = Build();
        using var scope = sp.CreateScope();

        var providers = scope.ServiceProvider.GetServices<IClinicalCriteriaProvider>().ToList();

        providers.Should().ContainSingle()
            .Which.ProviderId.Should().Be(ClinicalCriteriaOptions.LocalRulesProviderId);
    }

    [Fact]
    public void Enabling_a_licensed_provider_registers_it()
    {
        using var sp = Build(
            ("ClinicalCriteria:InterQual:Enabled", "true"),
            ("ClinicalCriteria:InterQual:BaseUrl", "https://example.invalid"),
            ("ClinicalCriteria:InterQual:CredentialSecretName", "interqual-api-key"));
        using var scope = sp.CreateScope();

        var status = scope.ServiceProvider.GetRequiredService<IClinicalCriteriaService>().GetProviderStatus();

        status.Select(s => s.ProviderId).Should().BeEquivalentTo(["cho-local", "interqual"]);
        status.Single(s => s.ProviderId == "interqual").IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void An_enabled_but_half_configured_provider_is_registered_and_reported_unusable()
    {
        using var sp = Build(("ClinicalCriteria:Mcg:Enabled", "true"));
        using var scope = sp.CreateScope();

        var status = scope.ServiceProvider.GetRequiredService<IClinicalCriteriaService>().GetProviderStatus();

        status.Single(s => s.ProviderId == "mcg").IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void A_customers_own_connector_replaces_a_shipped_provider_of_the_same_id()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ClinicalCriteria:InterQual:Enabled"] = "true",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IPriorAuthRuleEngine>());
        services.AddClinicalCriteria(configuration);
        services.AddScoped<IClinicalCriteriaProvider, CustomerInterQualConnector>();

        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();

        var status = scope.ServiceProvider.GetRequiredService<IClinicalCriteriaService>().GetProviderStatus();

        status.Single(s => s.ProviderId == "interqual").IsConfigured.Should().BeTrue(
            "the last registration of a provider id wins");
    }

    private sealed class CustomerInterQualConnector : IClinicalCriteriaProvider
    {
        public string ProviderId => "interqual";
        public string DisplayName => "InterQual (customer connector)";
        public bool IsConfigured => true;

        public Task<Models.CriteriaEvaluation> EvaluateAsync(
            Models.CriteriaRequest request, CancellationToken ct = default) =>
            throw new NotImplementedException();
    }
}
