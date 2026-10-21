using Xunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.DocumentStore.Tests;

/// <summary>
/// AddChoDocumentStore picks the provider from configuration and fails
/// closed: a deployment with no usable store does not start, and documents
/// never silently live in memory outside Development.
/// </summary>
public class DocumentStoreRegistrationTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();

    private static IDocumentStore Create(IConfiguration config, string environment = "Production") =>
        DocumentStoreServiceCollectionExtensions.Create(config, environment, NullLoggerFactory.Instance);

    [Fact]
    public void NothingConfigured_Fails()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Create(Config()));
        Assert.Contains("DocumentStore:Provider", ex.Message);
    }

    [Fact]
    public void NoProvider_WithTheOlderAzureSetting_IsAzure()
    {
        // Deployments configured before DocumentStore:Provider existed keep working.
        var config = Config(("BlobStorage:ConnectionString", "UseDevelopmentStorage=true"));

        Assert.Equal(DocumentStoreProvider.Azure,
            DocumentStoreServiceCollectionExtensions.ResolveProvider(config, "Production"));
        Assert.IsType<AzureBlobDocumentStore>(Create(config));
    }

    [Fact]
    public void ExplicitProvider_WinsOverTheOlderAzureSetting()
    {
        var config = Config(
            ("BlobStorage:ConnectionString", "UseDevelopmentStorage=true"),
            ("DocumentStore:Provider", "S3"),
            ("DocumentStore:S3:Bucket", "docs"),
            ("DocumentStore:S3:Region", "us-west-2"));

        Assert.IsType<S3DocumentStore>(Create(config));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Testing")]
    [InlineData("Staging")]
    public void InMemory_IsRefusedOutsideDevelopment(string environment)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Create(Config(("DocumentStore:Provider", "InMemory")), environment));
        Assert.Contains("Development", ex.Message);
    }

    [Fact]
    public void InMemory_IsAllowedInDevelopment() =>
        Assert.IsType<InMemoryDocumentStore>(Create(Config(("DocumentStore:Provider", "inmemory")), "Development"));

    [Theory]
    [InlineData("Gcs", "DocumentStore:Gcs:Bucket")]
    [InlineData("S3", "DocumentStore:S3:Bucket")]
    [InlineData("Azure", "connection string")]
    public void AProviderWithoutItsSettings_Fails(string provider, string missing)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Create(Config(("DocumentStore:Provider", provider))));
        Assert.Contains(missing, ex.Message);
    }

    [Fact]
    public void AnUnknownProvider_Fails()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Create(Config(("DocumentStore:Provider", "Dropbox"))));
        Assert.Contains("Dropbox", ex.Message);
    }

    [Fact]
    public void Gcs_WithAnEmulatorEndpoint_NeedsNoCredentials()
    {
        var store = Create(Config(
            ("DocumentStore:Provider", "Gcs"),
            ("DocumentStore:Gcs:Bucket", "docs"),
            ("DocumentStore:Gcs:Endpoint", "http://127.0.0.1:4443")));

        Assert.IsType<GcsDocumentStore>(store);
        Assert.Equal(new Uri("gs://docs/member-documents/a/b.pdf"), store.GetUri("member-documents", "a/b.pdf"));
    }

    [Fact]
    public void AddChoDocumentStore_ResolvesFromTheHostsFinalConfiguration()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Config(("DocumentStore:Provider", "InMemory")));
        services.AddChoDocumentStore("Development");

        using var provider = services.BuildServiceProvider();

        Assert.IsType<InMemoryDocumentStore>(provider.GetRequiredService<IDocumentStore>());
    }
}

public class DocumentKeysTests
{
    [Fact]
    public void ObjectName_PrefixesTheContainer() =>
        Assert.Equal("member-documents/tenants/t1/doc.pdf", DocumentKeys.ObjectName("member-documents", "tenants/t1/doc.pdf"));

    [Theory]
    [InlineData("", "a.txt")]
    [InlineData("a/b", "a.txt")]
    [InlineData("..", "a.txt")]
    [InlineData("c", "")]
    [InlineData("c", "/abs.txt")]
    [InlineData("c", "../other/a.txt")]
    [InlineData("c", "x/./a.txt")]
    public void ObjectName_RejectsNamesThatCouldLeaveTheContainer(string container, string name) =>
        Assert.Throws<ArgumentException>(() => DocumentKeys.ObjectName(container, name));
}
