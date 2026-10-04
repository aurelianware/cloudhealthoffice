using CloudHealthOffice.Testing.Mongo;
using MemberDocumentService.Models;
using MemberDocumentService.Repositories;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MemberDocumentService.Tests.Repositories;

/// <summary>
/// Saves replace only the version that was read: a legal-hold change and
/// another write that both started from the same read cannot silently
/// overwrite each other. Against a real mongod.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class MemberDocumentConcurrencyTests : IAsyncLifetime
{
    private const string Tenant = "tenant-docs";

    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;
    private MemberDocumentRepositoryMongo _repository = null!;

    public MemberDocumentConcurrencyTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("member_documents_occ");
        _repository = new MemberDocumentRepositoryMongo(_database);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);

    private static MemberDocument Document(string id) => new()
    {
        Id = id,
        TenantId = Tenant,
        MemberId = "mem-1",
        Category = "EOB",
        BlobPath = $"tenants/{Tenant}/members/mem-1/{id}.pdf",
        ContentType = "application/pdf",
        PendingUploadBlobPath = $"tenants/{Tenant}/members/mem-1/{id}.pdf.upload",
    };

    [Fact]
    public async Task LegalHoldAndAnotherWrite_FromTheSameRead_TheLaterSaveIsRefused()
    {
        await _repository.CreateAsync(Document("doc-1"));

        var holdRequest = (await _repository.GetByIdAsync(Tenant, "doc-1"))!;
        var finalizeRequest = (await _repository.GetByIdAsync(Tenant, "doc-1"))!;

        holdRequest.LegalHold = true;
        holdRequest.LegalHoldSetBy = "compliance-1";
        await _repository.UpdateAsync(holdRequest);

        finalizeRequest.PendingUploadBlobPath = null;
        finalizeRequest.SizeBytes = 1234;
        await FluentActions.Awaiting(() => _repository.UpdateAsync(finalizeRequest))
            .Should().ThrowAsync<MemberDocumentConcurrencyException>();

        // Before: the second save replaced the whole record and dropped the hold.
        var stored = (await _repository.GetByIdAsync(Tenant, "doc-1"))!;
        stored.LegalHold.Should().BeTrue();
        stored.LegalHoldSetBy.Should().Be("compliance-1");
        stored.PendingUploadBlobPath.Should().NotBeNull();
        stored.Version.Should().Be(1);
    }

    [Fact]
    public async Task SequentialSaves_OfFreshReads_Succeed_AndCountVersions()
    {
        await _repository.CreateAsync(Document("doc-2"));

        for (var i = 1; i <= 3; i++)
        {
            var doc = (await _repository.GetByIdAsync(Tenant, "doc-2"))!;
            doc.SizeBytes = i;
            var saved = await _repository.UpdateAsync(doc);
            saved.Version.Should().Be(i);
        }

        (await _repository.GetByIdAsync(Tenant, "doc-2"))!.SizeBytes.Should().Be(3);
    }

    [Fact]
    public async Task DocumentSavedBeforeVersioning_IsVersion0_AndSaves()
    {
        var legacy = Document("doc-legacy").ToBsonDocument();
        legacy.Remove("Version");
        await _database.GetCollection<BsonDocument>("MemberDocuments").InsertOneAsync(legacy);

        var doc = (await _repository.GetByIdAsync(Tenant, "doc-legacy"))!;
        doc.Version.Should().Be(0);
        doc.LegalHold = true;
        await _repository.UpdateAsync(doc);

        var stale = Document("doc-legacy");
        stale.Version = 0;
        await FluentActions.Awaiting(() => _repository.UpdateAsync(stale))
            .Should().ThrowAsync<MemberDocumentConcurrencyException>();
        (await _repository.GetByIdAsync(Tenant, "doc-legacy"))!.LegalHold.Should().BeTrue();
    }
}
