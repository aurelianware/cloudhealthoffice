using CloudHealthOffice.ManagedFhirStore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CloudHealthOffice.ManagedFhirStore.Tests;

public class ManagedFhirStoreRegistrationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("None")]
    public void Publishing_is_off_unless_a_provider_is_named(string? provider)
    {
        var services = new ServiceCollection();

        var registered = services.AddChoManagedFhirStore(Config(("ManagedFhirStore:Provider", provider)), "Production");

        Assert.False(registered);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IManagedFhirStore));
    }

    [Fact]
    public void Gcp_registers_the_healthcare_api_store()
    {
        var services = new ServiceCollection().AddLogging();

        var registered = services.AddChoManagedFhirStore(Config(
            ("ManagedFhirStore:Provider", "GcpHealthcare"),
            ("ManagedFhirStore:Gcp:ProjectId", "demo"),
            ("ManagedFhirStore:Gcp:Location", "us-central1"),
            ("ManagedFhirStore:Gcp:DatasetId", "cho"),
            ("ManagedFhirStore:Gcp:FhirStoreId", "cho-fhir-r4")), "Production");

        Assert.True(registered);
        var store = services.BuildServiceProvider().GetRequiredService<IManagedFhirStore>();
        Assert.Equal("gcp-healthcare", store.ProviderName);
    }

    [Fact]
    public void Gcp_with_missing_coordinates_fails_at_registration()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddChoManagedFhirStore(Config(
            ("ManagedFhirStore:Provider", "GcpHealthcare"),
            ("ManagedFhirStore:Gcp:ProjectId", "demo")), "Production"));

        Assert.Contains("DatasetId", ex.Message);
    }

    [Theory]
    [InlineData("AzureHealthDataServices")]
    [InlineData("AwsHealthLake")]
    [InlineData("1")]
    public void An_unimplemented_or_unknown_provider_is_a_startup_error(string provider)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ManagedFhirStoreServiceCollectionExtensions.ResolveProvider(Config(("ManagedFhirStore:Provider", provider)), "Production"));

        Assert.Contains("planned, not implemented", ex.Message);
    }

    [Fact]
    public void In_memory_is_refused_outside_development()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ManagedFhirStoreServiceCollectionExtensions.ResolveProvider(Config(("ManagedFhirStore:Provider", "InMemory")), "Production"));

        Assert.Equal(ManagedFhirStoreProvider.InMemory,
            ManagedFhirStoreServiceCollectionExtensions.ResolveProvider(Config(("ManagedFhirStore:Provider", "InMemory")), "Development"));
    }

    [Fact]
    public async Task In_memory_upserts_replace_and_count_versions()
    {
        var store = new InMemoryManagedFhirStore();
        const string v1 = """{"resourceType":"Patient","id":"m-1","gender":"female"}""";
        const string v2 = """{"resourceType":"Patient","id":"m-1","gender":"unknown"}""";

        var first = await store.UpsertAsync("Patient", "m-1", v1);
        var second = await store.UpsertAsync("Patient", "m-1", v2);

        Assert.Equal(201, first.StatusCode);
        Assert.Equal(200, second.StatusCode);
        Assert.Equal("2", second.VersionId);
        Assert.Equal(v2, await store.ReadAsync("Patient", "m-1"));
        Assert.Equal(1, store.Count);
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
}
