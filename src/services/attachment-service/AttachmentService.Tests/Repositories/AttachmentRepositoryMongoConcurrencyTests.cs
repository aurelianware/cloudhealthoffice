using System.Net;
using AttachmentService.Models;
using AttachmentService.Repositories;
using AttachmentService.Services;
using AttachmentService.Tests.Security;
using AttachmentService.Tests.Support;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace AttachmentService.Tests.Repositories;

/// <summary>
/// Optimistic concurrency on the MongoDB attachment repository (EphemeralMongo).
/// <c>UpdateAsync</c> replaced the attachment unconditionally, so of two
/// writes based on the same read (two acknowledgment requests, say) the later
/// silently erased the earlier. A replace is now conditional on the
/// <see cref="Attachment.Revision"/> it read.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class AttachmentRepositoryMongoConcurrencyTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "tenant-concurrency";
    private IMongoDatabase _db = null!;

    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("attachment_concurrency");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private AttachmentRepositoryMongo Repo() => new(
        _db, new ConfigurationBuilder().Build(), NullLogger<AttachmentRepositoryMongo>.Instance);

    private async Task SeedAsync(string id) => await Repo().CreateAsync(new Attachment
    {
        Id = id,
        TenantId = Tenant,
        ClaimId = "CLM-1",
        PayerId = "PAYER-1",
        DocumentType = "Medical Records",
        DocumentFormat = "PDF",
    });

    private async Task<Attachment> ReadAsync(string id) => (await Repo().GetByIdAsync(id, Tenant))!;

    private static async Task<Exception?> Try(Func<Task> write)
    {
        try
        {
            await write();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    [Fact]
    public async Task TwoConcurrentUpdates_FromTheSameRead_ExactlyOneWins_TheOtherConflicts()
    {
        await SeedAsync("A1");
        var a = await ReadAsync("A1");
        var b = await ReadAsync("A1");
        a.Generated999 = "ISA*999";
        b.Status = "Linked";

        var outcomes = await Task.WhenAll(
            Try(() => Repo().UpdateAsync(a)),
            Try(() => Repo().UpdateAsync(b)));

        outcomes.Count(o => o is null).Should().Be(1, "exactly one write wins");
        outcomes.Count(o => o is AttachmentConcurrencyException).Should().Be(1, "the other gets a conflict, not a silent overwrite");
        outcomes.OfType<AttachmentConcurrencyException>().Single()
            .Should().BeAssignableTo<CloudHealthOffice.Infrastructure.Data.ConcurrencyConflictException>("it is answered 409");
        (await ReadAsync("A1")).Revision.Should().Be(1);
    }

    [Fact]
    public async Task AStaleReplace_Conflicts_AndTheEarlierWriteSurvives_UntilReread()
    {
        await SeedAsync("A1");
        var stale = await ReadAsync("A1");
        var fresh = await ReadAsync("A1");
        fresh.AcknowledgmentSent = true;
        fresh.AcknowledgmentSentDate = DateTime.UtcNow;
        await Repo().UpdateAsync(fresh);
        stale.Notes = "reviewed";

        await ((Func<Task>)(() => Repo().UpdateAsync(stale))).Should().ThrowAsync<AttachmentConcurrencyException>();
        stale.Revision.Should().Be(0, "the in-memory revision is restored on a conflict");
        (await ReadAsync("A1")).AcknowledgmentSent.Should().BeTrue();

        var reread = await ReadAsync("A1");
        reread.Notes = "reviewed";
        await Repo().UpdateAsync(reread);

        var stored = await ReadAsync("A1");
        stored.AcknowledgmentSent.Should().BeTrue();
        stored.Notes.Should().Be("reviewed");
        stored.Revision.Should().Be(2);
    }

    [Fact]
    public async Task AnAttachmentWrittenBeforeTheRevisionExisted_UpdatesOnce_ThenCarriesARevision()
    {
        await SeedAsync("A1");
        await _db.GetCollection<BsonDocument>("Attachments").UpdateOneAsync(
            new BsonDocument("_id", "A1"), new BsonDocument("$unset", new BsonDocument("Revision", "")));
        var legacy = await ReadAsync("A1");
        legacy.Revision.Should().Be(0);
        legacy.Notes = "legacy";

        await Repo().UpdateAsync(legacy);

        (await ReadAsync("A1")).Revision.Should().Be(1);
    }

    [Fact]
    public async Task UpdatingAMissingAttachment_IsStillNotFound_NotAConflict()
    {
        var ghost = new Attachment { Id = "NOPE", TenantId = Tenant };

        var thrown = await ((Func<Task>)(() => Repo().UpdateAsync(ghost))).Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Should().NotBeAssignableTo<CloudHealthOffice.Infrastructure.Data.ConcurrencyConflictException>();
        (await Repo().GetByIdAsync("NOPE", Tenant)).Should().BeNull();
    }
}

/// <summary>A conflicting acknowledgment save is answered 409, not swallowed into a 500.</summary>
[Collection(nameof(AttachmentServiceCollection))]
public sealed class AttachmentAcknowledgmentConflictTests
{
    private const string Tenant = "tenant-a";
    private readonly AttachmentServiceFactory _factory;

    public AttachmentAcknowledgmentConflictTests(AttachmentServiceFactory factory)
    {
        _factory = factory;
        _factory.Reset();
    }

    [Fact]
    public async Task Acknowledgment_WhenTheAttachmentChangedSinceItWasRead_Is409()
    {
        var attachment = _factory.Attachments.Seed(Tenant);
        _factory.Attachments.ThrowOnUpdate = new AttachmentConcurrencyException(attachment.Id, 0);
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler("user-1"));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);

        var response = await client.PostAsync($"/api/attachments/{attachment.Id}/acknowledgment", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}
