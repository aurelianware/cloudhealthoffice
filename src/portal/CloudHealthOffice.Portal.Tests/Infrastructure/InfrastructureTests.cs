using CloudHealthOffice.Portal.Infrastructure;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Xml.Linq;

namespace CloudHealthOffice.Portal.Tests.Infrastructure;

public class InfrastructureTests
{
    // ── DataProtectionKeyDocument ────────────────────────────────────────────

    [Fact]
    public void DataProtectionKeyDocument_AllProperties_RoundTrip()
    {
        var id = ObjectId.GenerateNewId();
        var sut = new DataProtectionKeyDocument
        {
            Id = id,
            FriendlyName = "key-2026-01-01",
            Xml = "<key id=\"abc\" version=\"1\" />"
        };

        sut.Id.Should().Be(id);
        sut.FriendlyName.Should().Be("key-2026-01-01");
        sut.Xml.Should().Be("<key id=\"abc\" version=\"1\" />");
    }

    [Fact]
    public void DataProtectionKeyDocument_DefaultValues_AreEmpty()
    {
        var sut = new DataProtectionKeyDocument();
        sut.FriendlyName.Should().BeEmpty();
        sut.Xml.Should().BeEmpty();
    }

    // ── MongoDbXmlRepository ─────────────────────────────────────────────────
    // Note: GetAllElements and StoreElement rely on synchronous MongoDB driver
    // methods that route through FindSync / InsertOne on the IMongoCollection
    // interface. Mocking FindSync directly lets us exercise error-handling paths.

    [Fact]
    public void MongoDbXmlRepository_GetAllElements_WhenFindSyncThrows_ReturnsEmptyCollection()
    {
        var mongoClient = new Mock<IMongoClient>();
        var database = new Mock<IMongoDatabase>();
        var collection = new Mock<IMongoCollection<DataProtectionKeyDocument>>();
        var logger = new Mock<ILogger<MongoDbXmlRepository>>();

        mongoClient.Setup(c => c.GetDatabase(It.IsAny<string>(), null)).Returns(database.Object);
        database.Setup(d => d.GetCollection<DataProtectionKeyDocument>(
            It.IsAny<string>(), null)).Returns(collection.Object);

        // FindSync is the interface method called by the Find extension method
        collection.Setup(c => c.FindSync(
            It.IsAny<FilterDefinition<DataProtectionKeyDocument>>(),
            It.IsAny<FindOptions<DataProtectionKeyDocument, DataProtectionKeyDocument>>(),
            It.IsAny<CancellationToken>()))
            .Throws(new Exception("MongoDB connection failed"));

        var sut = new MongoDbXmlRepository(mongoClient.Object, logger.Object);

        var result = sut.GetAllElements();

        result.Should().BeEmpty();
    }

    [Fact]
    public void MongoDbXmlRepository_StoreElement_WhenInsertOneThrows_RethrowsException()
    {
        var mongoClient = new Mock<IMongoClient>();
        var database = new Mock<IMongoDatabase>();
        var collection = new Mock<IMongoCollection<DataProtectionKeyDocument>>();
        var logger = new Mock<ILogger<MongoDbXmlRepository>>();

        mongoClient.Setup(c => c.GetDatabase(It.IsAny<string>(), null)).Returns(database.Object);
        database.Setup(d => d.GetCollection<DataProtectionKeyDocument>(
            It.IsAny<string>(), null)).Returns(collection.Object);

        collection.Setup(c => c.InsertOne(
            It.IsAny<DataProtectionKeyDocument>(),
            It.IsAny<InsertOneOptions>(),
            It.IsAny<CancellationToken>()))
            .Throws(new Exception("Insert failed"));

        var sut = new MongoDbXmlRepository(mongoClient.Object, logger.Object);
        var element = XElement.Parse("<key id=\"test\" />");

        var act = () => sut.StoreElement(element, "test-key");

        act.Should().Throw<Exception>().WithMessage("Insert failed");
    }

    [Fact]
    public void MongoDbXmlRepository_GetAllElements_WhenCollectionHasValidDocs_ReturnsParsedElements()
    {
        var mongoClient = new Mock<IMongoClient>();
        var database = new Mock<IMongoDatabase>();
        var collection = new Mock<IMongoCollection<DataProtectionKeyDocument>>();
        var logger = new Mock<ILogger<MongoDbXmlRepository>>();

        mongoClient.Setup(c => c.GetDatabase(It.IsAny<string>(), null)).Returns(database.Object);
        database.Setup(d => d.GetCollection<DataProtectionKeyDocument>(
            It.IsAny<string>(), null)).Returns(collection.Object);

        var docs = new List<DataProtectionKeyDocument>
        {
            new() { FriendlyName = "key-1", Xml = "<key id=\"1\" />" },
            new() { FriendlyName = "key-2", Xml = "<key id=\"2\" />" }
        };

        var cursor = new Mock<IAsyncCursor<DataProtectionKeyDocument>>();
        var called = false;
        cursor.Setup(c => c.MoveNext(It.IsAny<CancellationToken>()))
            .Returns(() => { if (called) return false; called = true; return true; });
        cursor.Setup(c => c.Current).Returns(docs);
        cursor.Setup(c => c.Dispose());

        collection.Setup(c => c.FindSync(
            It.IsAny<FilterDefinition<DataProtectionKeyDocument>>(),
            It.IsAny<FindOptions<DataProtectionKeyDocument, DataProtectionKeyDocument>>(),
            It.IsAny<CancellationToken>()))
            .Returns(cursor.Object);

        var sut = new MongoDbXmlRepository(mongoClient.Object, logger.Object);

        var result = sut.GetAllElements();

        result.Should().HaveCount(2);
        result.First().Name.LocalName.Should().Be("key");
    }

    [Fact]
    public void MongoDbXmlRepository_GetAllElements_WhenDocHasInvalidXml_SkipsInvalidDoc()
    {
        var mongoClient = new Mock<IMongoClient>();
        var database = new Mock<IMongoDatabase>();
        var collection = new Mock<IMongoCollection<DataProtectionKeyDocument>>();
        var logger = new Mock<ILogger<MongoDbXmlRepository>>();

        mongoClient.Setup(c => c.GetDatabase(It.IsAny<string>(), null)).Returns(database.Object);
        database.Setup(d => d.GetCollection<DataProtectionKeyDocument>(
            It.IsAny<string>(), null)).Returns(collection.Object);

        var docs = new List<DataProtectionKeyDocument>
        {
            new() { FriendlyName = "good-key", Xml = "<key id=\"1\" />" },
            new() { FriendlyName = "bad-key", Xml = "<<invalid xml>>" }
        };

        var cursor = new Mock<IAsyncCursor<DataProtectionKeyDocument>>();
        var called = false;
        cursor.Setup(c => c.MoveNext(It.IsAny<CancellationToken>()))
            .Returns(() => { if (called) return false; called = true; return true; });
        cursor.Setup(c => c.Current).Returns(docs);
        cursor.Setup(c => c.Dispose());

        collection.Setup(c => c.FindSync(
            It.IsAny<FilterDefinition<DataProtectionKeyDocument>>(),
            It.IsAny<FindOptions<DataProtectionKeyDocument, DataProtectionKeyDocument>>(),
            It.IsAny<CancellationToken>()))
            .Returns(cursor.Object);

        var sut = new MongoDbXmlRepository(mongoClient.Object, logger.Object);

        var result = sut.GetAllElements();

        // Only the valid XML doc should be returned
        result.Should().ContainSingle()
            .Which.Name.LocalName.Should().Be("key");
    }

    [Fact]
    public void MongoDbXmlRepository_StoreElement_WhenSuccessful_InsertsDocumentWithCorrectFields()
    {
        var mongoClient = new Mock<IMongoClient>();
        var database = new Mock<IMongoDatabase>();
        var collection = new Mock<IMongoCollection<DataProtectionKeyDocument>>();

        mongoClient.Setup(c => c.GetDatabase(It.IsAny<string>(), null)).Returns(database.Object);
        database.Setup(d => d.GetCollection<DataProtectionKeyDocument>(
            It.IsAny<string>(), null)).Returns(collection.Object);

        collection.Setup(c => c.InsertOne(
            It.IsAny<DataProtectionKeyDocument>(),
            It.IsAny<InsertOneOptions>(),
            It.IsAny<CancellationToken>()));

        var sut = new MongoDbXmlRepository(mongoClient.Object);
        var element = XElement.Parse("<key id=\"fresh\" version=\"1\" />");

        sut.StoreElement(element, "fresh-key");

        collection.Verify(c => c.InsertOne(
            It.Is<DataProtectionKeyDocument>(d => d.FriendlyName == "fresh-key"),
            It.IsAny<InsertOneOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}

public class DiagnosticCircuitHandlerTests
{
    // DiagnosticCircuitHandler wraps circuit lifecycle events with Debug logging.
    // Circuit (Microsoft.AspNetCore.Components.Server.Circuits) is a sealed class
    // with an internal constructor, so it cannot be created or mocked in external
    // test projects using the standard Moq approach.
    // The constructor and all four override methods are therefore covered indirectly
    // through bUnit integration tests (where the full Blazor Server pipeline is
    // available). Standalone unit tests for these methods are not possible without
    // internal-visibility access to the Circuit type.
}
