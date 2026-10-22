using CloudHealthOffice.Testing.Mongo;
using CorrespondenceService.Models;
using CorrespondenceService.Repositories;
using CorrespondenceService.Tests.Support;
using MongoDB.Driver;

namespace CorrespondenceService.Tests.Repositories;

[Collection(MongoRunnerFixture.CollectionName)]
public sealed class CorrespondenceRepositoryMongoTests : IAsyncLifetime
{
    private readonly MongoRunnerFixture _mongo;
    private readonly IMongoDatabase _db;
    private readonly CorrespondenceRepositoryMongo _repo;

    public CorrespondenceRepositoryMongoTests(MongoRunnerFixture mongo)
    {
        _mongo = mongo;
        _db = mongo.CreateDatabase("correspondence");
        _repo = new CorrespondenceRepositoryMongo(_db);
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_db);

    [Fact]
    public async Task Templates_round_trip_with_dates_and_stay_in_their_tenant()
    {
        var t = Builders.Template(to: new DateOnly(2027, 1, 1));
        t.FormExpiresOn = new DateOnly(2028, 12, 31);
        (await _repo.TryCreateTemplateVersionAsync(t)).Should().BeTrue();
        (await _repo.TryCreateTemplateVersionAsync(t)).Should().BeFalse();

        var loaded = await _repo.GetTemplateVersionAsync(Builders.Tenant, t.TemplateId, 1);
        loaded!.EffectiveFrom.Should().Be(new DateOnly(2026, 1, 1));
        loaded.EffectiveTo.Should().Be(new DateOnly(2027, 1, 1));
        loaded.FormExpiresOn.Should().Be(new DateOnly(2028, 12, 31));
        loaded.Blocks.Single(b => b.Id == "rights").ApprovedTextHash.Should().NotBeNull();

        (await _repo.GetTemplateVersionAsync("tenant-b", t.TemplateId, 1)).Should().BeNull();
        (await _repo.ListApprovedAsync(Builders.Tenant, Builders.LetterTypeCode, "en", TemplateFormat.Standard)).Should().ContainSingle();
        (await _repo.ListApprovedAsync("tenant-b", Builders.LetterTypeCode, "en", TemplateFormat.Standard)).Should().BeEmpty();
        (await _repo.ListApprovedAsync(Builders.Tenant, Builders.LetterTypeCode, "es", TemplateFormat.Standard)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_template_id_held_by_another_tenant_is_refused_not_overwritten()
    {
        // Template ids are server-generated; a clash is refused, never merged.
        var a = Builders.Template();
        var b = Builders.Template();
        b.TenantId = "tenant-b";
        b.Name = "Other tenant";
        (await _repo.TryCreateTemplateVersionAsync(a)).Should().BeTrue();
        (await _repo.TryCreateTemplateVersionAsync(b)).Should().BeFalse();
        (await _repo.GetTemplateVersionAsync(Builders.Tenant, a.TemplateId, 1))!.Name.Should().Be("Appeal acknowledgment");
        (await _repo.GetTemplateVersionAsync("tenant-b", a.TemplateId, 1)).Should().BeNull();
    }

    [Fact]
    public async Task Saving_a_stale_revision_is_refused()
    {
        var t = Builders.Template(status: TemplateStatus.Draft);
        await _repo.TryCreateTemplateVersionAsync(t);
        var first = (await _repo.GetTemplateVersionAsync(Builders.Tenant, t.TemplateId, 1))!;
        var second = (await _repo.GetTemplateVersionAsync(Builders.Tenant, t.TemplateId, 1))!;

        first.Name = "First";
        await _repo.SaveTemplateVersionAsync(first, first.Revision);
        second.Name = "Second";
        var stale = () => _repo.SaveTemplateVersionAsync(second, second.Revision);
        await stale.Should().ThrowAsync<CorrespondenceConcurrencyException>();
        (await _repo.GetTemplateVersionAsync(Builders.Tenant, t.TemplateId, 1))!.Name.Should().Be("First");
    }

    [Fact]
    public async Task Letter_types_are_unique_per_tenant()
    {
        (await _repo.TryCreateLetterTypeAsync(Builders.LetterType())).Should().BeTrue();
        (await _repo.TryCreateLetterTypeAsync(Builders.LetterType())).Should().BeFalse();
        (await _repo.TryCreateLetterTypeAsync(Builders.LetterType(tenant: "tenant-b"))).Should().BeTrue();
        (await _repo.ListLetterTypesAsync(Builders.Tenant)).Should().ContainSingle();
    }

    [Fact]
    public async Task Letters_list_by_subject_newest_first_and_events_append()
    {
        Letter Make(string id, int minutes, string tenant = Builders.Tenant) => new()
        {
            Id = id, TenantId = tenant, LetterTypeCode = Builders.LetterTypeCode, LetterDate = Builders.Today,
            Subjects = [new() { Kind = "Appeal", Id = "APL-1" }], CreatedAt = DateTime.UtcNow.AddMinutes(minutes),
            Events = [new() { Type = "Requested", OccurredAt = DateTime.UtcNow }],
        };
        await _repo.CreateLetterAsync(Make("LTR-1", 0));
        await _repo.CreateLetterAsync(Make("LTR-2", 5));
        await _repo.CreateLetterAsync(Make("LTR-3", 9, "tenant-b"));

        (await _repo.ListLettersForSubjectAsync(Builders.Tenant, "Appeal", "APL-1", 10)).Select(l => l.Id).Should().Equal("LTR-2", "LTR-1");
        (await _repo.GetLetterAsync("tenant-b", "LTR-1")).Should().BeNull();

        await _repo.AppendEventAsync(Builders.Tenant, "LTR-1", new LetterEvent { Type = "Viewed", OccurredAt = DateTime.UtcNow });
        await _repo.AppendEventAsync("tenant-b", "LTR-1", new LetterEvent { Type = "Spoofed", OccurredAt = DateTime.UtcNow });
        (await _repo.ListRecentLettersAsync(Builders.Tenant, null, null, 10)).Select(l => l.Id).Should().Equal("LTR-2", "LTR-1");
        (await _repo.ListRecentLettersAsync(Builders.Tenant, LetterStatus.CheckFailed, null, 10)).Should().BeEmpty();
        (await _repo.ListRecentLettersAsync(Builders.Tenant, null, "OTHER", 10)).Should().BeEmpty();
        var counts = await _repo.CountLettersSinceAsync(Builders.Tenant, DateTime.UtcNow.AddMinutes(-1));
        counts[LetterStatus.Rendered].Should().Be(2);
        (await _repo.CountLettersSinceAsync("tenant-b", DateTime.UtcNow.AddMinutes(-1)))[LetterStatus.Rendered].Should().Be(1);

        var loaded = (await _repo.GetLetterAsync(Builders.Tenant, "LTR-1"))!;
        loaded.Events.Select(e => e.Type).Should().Equal("Requested", "Viewed");
        loaded.LetterDate.Should().Be(Builders.Today);
    }
}
