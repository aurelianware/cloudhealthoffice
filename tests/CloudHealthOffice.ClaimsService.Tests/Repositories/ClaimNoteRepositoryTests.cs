using ClaimsService.Models;
using ClaimsService.Repositories;
using CloudHealthOffice.Infrastructure.Serialization;
using CloudHealthOffice.Testing.Mongo;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace CloudHealthOffice.ClaimsService.Tests.Repositories;

internal static class ClaimNoteRepositoryContract
{
    private static readonly DateTime T0 = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    private static ClaimNote Note(string tenant, string claim, string text, int minutes) => new()
    {
        TenantId = tenant,
        ClaimId = claim,
        ClaimNumber = "CN-" + claim,
        Category = ClaimNoteCategories.Decision,
        Text = text,
        AuthorId = "examiner-1",
        AuthorName = "Pat Examiner",
        CreatedAt = T0.AddMinutes(minutes),
    };

    /// <summary>Notes come back oldest first, for that claim and tenant only, with every field intact.</summary>
    public static async Task ListsTheClaimsNotesInWrittenOrder_ScopedToTheTenant(IClaimNoteRepository repo)
    {
        await repo.AppendAsync(Note("t1", "C1", "second", 10));
        await repo.AppendAsync(Note("t1", "C1", "first", 0));
        await repo.AppendAsync(Note("t1", "C2", "other claim", 5));
        await repo.AppendAsync(Note("t2", "C1", "other tenant", 5));

        var notes = await repo.ListByClaimAsync("C1", "t1");

        notes.Select(n => n.Text).Should().Equal("first", "second");
        var first = notes[0];
        first.Id.Should().NotBeNullOrEmpty();
        first.ClaimNumber.Should().Be("CN-C1");
        first.Category.Should().Be(ClaimNoteCategories.Decision);
        first.AuthorId.Should().Be("examiner-1");
        first.AuthorName.Should().Be("Pat Examiner");
        first.CreatedAt.Should().Be(T0);
    }
}

[Collection(MongoRunnerFixture.CollectionName)]
public class ClaimNoteRepositoryMongoTests
{
    private readonly MongoRunnerFixture _mongo;

    public ClaimNoteRepositoryMongoTests(MongoRunnerFixture mongo) => _mongo = mongo;

    [Fact]
    public async Task ListsTheClaimsNotesInWrittenOrder_ScopedToTheTenant()
    {
        var database = _mongo.CreateDatabase("claim_notes");
        try
        {
            ClaimNoteRepositoryMongo.EnsureIndexes(database);
            await ClaimNoteRepositoryContract.ListsTheClaimsNotesInWrittenOrder_ScopedToTheTenant(
                new ClaimNoteRepositoryMongo(database));
        }
        finally
        {
            await _mongo.DropDatabaseAsync(database);
        }
    }
}
