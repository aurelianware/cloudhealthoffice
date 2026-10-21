using CloudHealthOffice.Testing.Mongo;
using DocumentService.Models;
using DocumentService.Repositories;
using MongoDB.Driver;

namespace DocumentService.Tests.Repositories;

/// <summary>The catalog and the access audit against a real mongod.</summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class DocumentRepositoryMongoTests : IAsyncLifetime
{
    private const string Tenant = "tenant-docs";

    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;
    private DocumentRepositoryMongo _repository = null!;

    public DocumentRepositoryMongoTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("document_catalog");
        _repository = new DocumentRepositoryMongo(_database);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);

    private static CatalogDocument Doc(string id, string tenant = Tenant, string? sha = null, bool pending = false, params DocumentLink[] links) => new()
    {
        Id = id,
        TenantId = tenant,
        Type = "MedicalRecord",
        ContentType = "application/pdf",
        Sha256 = sha,
        Links = links.Length > 0 ? links.ToList() : [Claim("CLM-1")],
        StorageContainer = "documents",
        StoragePath = $"tenants/{tenant}/documents/{id}.pdf",
        PendingUploadPath = pending ? $"tenants/{tenant}/documents/{id}.pdf.upload" : null,
        CreatedAt = DateTime.UtcNow,
        ReceivedAt = DateTime.UtcNow,
    };

    private static DocumentLink Claim(string id) => new() { Kind = DocumentLinkKind.Claim, Id = id };

    [Fact]
    public async Task ListBySubject_MatchesAnyLink_WithinTheTenantOnly()
    {
        await _repository.CreateAsync(Doc("a", links: [Claim("CLM-1"), new DocumentLink { Kind = DocumentLinkKind.Member, Id = "M-1" }]));
        await _repository.CreateAsync(Doc("b", links: [Claim("CLM-2")]));
        await _repository.CreateAsync(Doc("c", tenant: "other", links: [Claim("CLM-1")]));
        // Same id, different kind: not a match.
        await _repository.CreateAsync(Doc("d", links: [new DocumentLink { Kind = DocumentLinkKind.Appeal, Id = "CLM-1" }]));

        var onClaim = await _repository.ListBySubjectAsync(Tenant, Claim("CLM-1"));
        var onMember = await _repository.ListBySubjectAsync(Tenant, new DocumentLink { Kind = DocumentLinkKind.Member, Id = "M-1" });

        Assert.Equal(["a"], onClaim.Select(d => d.Id));
        Assert.Equal(["a"], onMember.Select(d => d.Id));
    }

    [Fact]
    public async Task Links_RoundTripWithTheirKindAsAString()
    {
        await _repository.CreateAsync(Doc("a", links: [Claim("CLM-1")]));

        var raw = await _database.GetCollection<MongoDB.Bson.BsonDocument>(DocumentRepositoryMongo.CollectionName)
            .Find(FilterDefinition<MongoDB.Bson.BsonDocument>.Empty).FirstAsync();

        Assert.Equal("Claim", raw["Links"][0]["Kind"].AsString);
        Assert.Equal(Claim("CLM-1"), (await _repository.GetAsync(Tenant, "a"))!.Links.Single());
    }

    [Fact]
    public async Task FindByHash_IgnoresPendingUploads_AndOtherTenants()
    {
        await _repository.CreateAsync(Doc("pending", sha: "abc", pending: true));
        await _repository.CreateAsync(Doc("elsewhere", tenant: "other", sha: "abc"));
        Assert.Null(await _repository.FindByHashAsync(Tenant, "abc"));

        await _repository.CreateAsync(Doc("final", sha: "abc"));
        Assert.Equal("final", (await _repository.FindByHashAsync(Tenant, "abc"))!.Id);
    }

    [Fact]
    public async Task Update_FromAStaleRead_IsRefused()
    {
        await _repository.CreateAsync(Doc("a"));
        var first = (await _repository.GetAsync(Tenant, "a"))!;
        var second = (await _repository.GetAsync(Tenant, "a"))!;

        first.Links.Add(Claim("CLM-2"));
        await _repository.UpdateAsync(first);
        second.Retention.LegalHold.Set = true;

        await Assert.ThrowsAsync<DocumentConcurrencyException>(() => _repository.UpdateAsync(second));
        var stored = (await _repository.GetAsync(Tenant, "a"))!;
        Assert.Equal(2, stored.Links.Count);
        Assert.False(stored.Retention.LegalHold.Set);
    }

    [Fact]
    public async Task DeletePending_NeverDeletesAFinalizedDocument()
    {
        await _repository.CreateAsync(Doc("final"));
        await _repository.CreateAsync(Doc("pending", pending: true));

        await _repository.DeletePendingAsync(Tenant, "final");
        await _repository.DeletePendingAsync(Tenant, "pending");

        Assert.NotNull(await _repository.GetAsync(Tenant, "final"));
        Assert.Null(await _repository.GetAsync(Tenant, "pending"));
    }

    [Fact]
    public async Task AccessAudit_ReturnsADocumentsEntries_NewestFirst()
    {
        var audit = new DocumentAccessAuditMongo(_database);
        var t0 = DateTime.UtcNow;
        await audit.RecordAsync(new DocumentAccessEvent { TenantId = Tenant, DocumentId = "a", Actor = "u1", At = t0, Action = DocumentAccessAction.View, Allowed = true });
        await audit.RecordAsync(new DocumentAccessEvent { TenantId = Tenant, DocumentId = "a", Actor = "u2", At = t0.AddSeconds(1), Action = DocumentAccessAction.Download, Allowed = false, Detail = "denied" });
        await audit.RecordAsync(new DocumentAccessEvent { TenantId = "other", DocumentId = "a", Actor = "u3", At = t0, Action = DocumentAccessAction.View, Allowed = true });

        var entries = await audit.ForDocumentAsync(Tenant, "a", 10);

        Assert.Equal(["u2", "u1"], entries.Select(e => e.Actor));
    }
}
