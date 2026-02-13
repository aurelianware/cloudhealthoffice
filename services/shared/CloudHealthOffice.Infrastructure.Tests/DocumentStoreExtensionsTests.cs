using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Infrastructure;

namespace CloudHealthOffice.Infrastructure.Tests;

public class DocumentStoreExtensionsTests
{
    [Fact]
    public void AddDocumentStore_WithAzureProvider_RegistersCosmosDocumentStore()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        Environment.SetEnvironmentVariable("CloudProvider", "Azure");
        Environment.SetEnvironmentVariable("CosmosDb__ConnectionString", "AccountEndpoint=https://test.documents.azure.com;AccountKey=test");
        Environment.SetEnvironmentVariable("CosmosDb__DatabaseName", "test-db");
        Environment.SetEnvironmentVariable("CosmosDb__ContainerName", "test-container");

        // Act
        services.AddDocumentStore<TestDocument>();
        var serviceProvider = services.BuildServiceProvider();

        // Assert
        var documentStore = serviceProvider.GetService<IDocumentStore<TestDocument>>();
        documentStore.Should().NotBeNull();
        documentStore.Should().BeOfType<CosmosDocumentStore<TestDocument>>();
    }

    [Fact]
    public void AddDocumentStore_WithDigitalOceanProvider_RegistersMongoDocumentStore()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        Environment.SetEnvironmentVariable("CloudProvider", "DigitalOcean");
        Environment.SetEnvironmentVariable("MongoDB__ConnectionString", "mongodb://localhost:27017");
        Environment.SetEnvironmentVariable("MongoDB__DatabaseName", "test-db");
        Environment.SetEnvironmentVariable("MongoDB__CollectionName", "test-collection");

        // Act
        services.AddDocumentStore<TestDocument>();
        var serviceProvider = services.BuildServiceProvider();

        // Assert
        var documentStore = serviceProvider.GetService<IDocumentStore<TestDocument>>();
        documentStore.Should().NotBeNull();
        documentStore.Should().BeOfType<MongoDocumentStore<TestDocument>>();
    }

    [Fact]
    public void AddDocumentStore_WithNoProvider_DefaultsToAzure()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        Environment.SetEnvironmentVariable("CloudProvider", null);
        Environment.SetEnvironmentVariable("CosmosDb__ConnectionString", "AccountEndpoint=https://test.documents.azure.com;AccountKey=test");
        Environment.SetEnvironmentVariable("CosmosDb__DatabaseName", "test-db");
        Environment.SetEnvironmentVariable("CosmosDb__ContainerName", "test-container");

        // Act
        services.AddDocumentStore<TestDocument>();
        var serviceProvider = services.BuildServiceProvider();

        // Assert
        var documentStore = serviceProvider.GetService<IDocumentStore<TestDocument>>();
        documentStore.Should().NotBeNull();
        documentStore.Should().BeOfType<CosmosDocumentStore<TestDocument>>();
    }

    [Fact]
    public void AddDocumentStore_WithInvalidProvider_ThrowsException()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        Environment.SetEnvironmentVariable("CloudProvider", "AWS"); // Not supported

        // Act & Assert
        Assert.Throws<NotSupportedException>(() => services.AddDocumentStore<TestDocument>());
    }

    [Theory]
    [InlineData("azure")]
    [InlineData("AZURE")]
    [InlineData("Azure")]
    public void AddDocumentStore_CloudProviderIsCaseInsensitive_Azure(string provider)
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        Environment.SetEnvironmentVariable("CloudProvider", provider);
        Environment.SetEnvironmentVariable("CosmosDb__ConnectionString", "AccountEndpoint=https://test.documents.azure.com;AccountKey=test");
        Environment.SetEnvironmentVariable("CosmosDb__DatabaseName", "test-db");
        Environment.SetEnvironmentVariable("CosmosDb__ContainerName", "test-container");

        // Act
        services.AddDocumentStore<TestDocument>();
        var serviceProvider = services.BuildServiceProvider();

        // Assert
        var documentStore = serviceProvider.GetService<IDocumentStore<TestDocument>>();
        documentStore.Should().BeOfType<CosmosDocumentStore<TestDocument>>();
    }

    [Theory]
    [InlineData("digitalocean")]
    [InlineData("DIGITALOCEAN")]
    [InlineData("DigitalOcean")]
    public void AddDocumentStore_CloudProviderIsCaseInsensitive_DigitalOcean(string provider)
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        Environment.SetEnvironmentVariable("CloudProvider", provider);
        Environment.SetEnvironmentVariable("MongoDB__ConnectionString", "mongodb://localhost:27017");
        Environment.SetEnvironmentVariable("MongoDB__DatabaseName", "test-db");
        Environment.SetEnvironmentVariable("MongoDB__CollectionName", "test-collection");

        // Act
        services.AddDocumentStore<TestDocument>();
        var serviceProvider = services.BuildServiceProvider();

        // Assert
        var documentStore = serviceProvider.GetService<IDocumentStore<TestDocument>>();
        documentStore.Should().BeOfType<MongoDocumentStore<TestDocument>>();
    }
}
