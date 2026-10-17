using CloudHealthOffice.Testing.Mongo;
using MemberService.Models;
using MemberService.Repositories;
using MongoDB.Driver;

namespace MemberService.Tests.Repositories;

/// <summary>
/// The last-name search over real MongoDB (EphemeralMongo). The input used
/// to go straight into a <c>$regex</c>, so <c>.*</c> matched every member of
/// the tenant and <c>(a+)+$</c> was a catastrophic-backtracking pattern run
/// by the database. It is now a case-insensitive prefix match on the literal
/// text.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class MemberLastNameSearchTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "t1";
    private IMongoDatabase _db = null!;
    private MemberRepositoryMongo _repo = null!;

    public async Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("members_last_name_search");
        _repo = new MemberRepositoryMongo(_db);
        await _db.GetCollection<Member>("Members").InsertManyAsync(new[]
        {
            Member("M1", "Smith"),
            Member("M2", "smithers"),
            Member("M3", "Goldsmith"),
            Member("M4", "O'Brien"),
            Member("M5", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!"),
            Member("M6", ".*Literal"),
            Member("M7", "Smith", tenant: "t2"),
        });
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private static Member Member(string memberId, string lastName, string tenant = Tenant) => new()
    {
        TenantId = tenant,
        MemberId = memberId,
        FirstName = "Pat",
        LastName = lastName,
        GroupNumber = "G1",
        DateOfBirth = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private async Task<string[]> Search(string lastName) =>
        (await _repo.SearchAsync(Tenant, lastName: lastName, pageSize: 100)).Items
            .Select(m => m.MemberId).OrderBy(x => x).ToArray();

    [Fact]
    public async Task NormalName_MatchesNamesStartingWithIt_CaseInsensitive_InTheTenantOnly()
    {
        (await Search("smith")).Should().Equal("M1", "M2");
        (await Search("SMITH")).Should().Equal("M1", "M2");
        (await Search("o'b")).Should().Equal("M4");
    }

    [Fact]
    public async Task DotStar_IsLiteral_AndDoesNotMatchEveryMember()
    {
        (await Search(".*")).Should().Equal("M6");
    }

    [Fact]
    public async Task CatastrophicBacktrackingPattern_IsLiteral_AndReturnsPromptly()
    {
        var search = Search("(a+)+$");

        (await search.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeEmpty();
    }

    [Theory]
    [InlineData("[")]
    [InlineData("(")]
    [InlineData("\\")]
    public async Task InvalidRegexSyntax_IsLiteral_NotAServerError(string lastName)
    {
        (await Search(lastName)).Should().BeEmpty();
    }

    [Fact]
    public async Task Substring_InTheMiddle_DoesNotMatch()
    {
        (await Search("mith")).Should().BeEmpty();
    }
}
