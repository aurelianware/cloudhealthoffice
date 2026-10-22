using CorrespondenceService.Models;
using MongoDB.Driver;

namespace CorrespondenceService.Repositories;

/// <summary>A save lost to a concurrent change; the caller reloads and retries or reports a conflict.</summary>
public sealed class CorrespondenceConcurrencyException(string message) : Exception(message);

/// <summary>Letter types, template versions and letters. Every query is scoped to one tenant.</summary>
public interface ICorrespondenceRepository
{
    Task<LetterType?> GetLetterTypeAsync(string tenantId, string code, CancellationToken ct = default);
    Task<IReadOnlyList<LetterType>> ListLetterTypesAsync(string tenantId, CancellationToken ct = default);

    /// <summary>False when the tenant already has a letter type with that code.</summary>
    Task<bool> TryCreateLetterTypeAsync(LetterType letterType, CancellationToken ct = default);

    Task<LetterTemplate?> GetTemplateVersionAsync(string tenantId, string templateId, int version, CancellationToken ct = default);
    Task<IReadOnlyList<LetterTemplate>> ListTemplateVersionsAsync(string tenantId, string templateId, CancellationToken ct = default);
    Task<IReadOnlyList<LetterTemplate>> ListTemplatesAsync(string tenantId, string? letterTypeCode, CancellationToken ct = default);

    /// <summary>Approved versions of a letter type's templates in a language and format: the candidates for a letter.</summary>
    Task<IReadOnlyList<LetterTemplate>> ListApprovedAsync(string tenantId, string letterTypeCode, string language, TemplateFormat format, CancellationToken ct = default);

    /// <summary>False when that template id and version already exist.</summary>
    Task<bool> TryCreateTemplateVersionAsync(LetterTemplate template, CancellationToken ct = default);

    /// <summary>Replaces a version if its stored revision is still <paramref name="expectedRevision"/>; bumps the revision.</summary>
    Task SaveTemplateVersionAsync(LetterTemplate template, long expectedRevision, CancellationToken ct = default);

    Task CreateLetterAsync(Letter letter, CancellationToken ct = default);
    Task<Letter?> GetLetterAsync(string tenantId, string letterId, CancellationToken ct = default);
    Task<IReadOnlyList<Letter>> ListLettersForSubjectAsync(string tenantId, string subjectKind, string subjectId, int limit, CancellationToken ct = default);

    /// <summary>The tenant's newest letters, optionally of one status and letter type.</summary>
    Task<IReadOnlyList<Letter>> ListRecentLettersAsync(string tenantId, LetterStatus? status, string? letterTypeCode, int limit, CancellationToken ct = default);

    /// <summary>How many of the tenant's letters were created since <paramref name="since"/>, by status.</summary>
    Task<IReadOnlyDictionary<LetterStatus, long>> CountLettersSinceAsync(string tenantId, DateTime since, CancellationToken ct = default);

    /// <summary>Appends to a letter's event log; existing events never change.</summary>
    Task AppendEventAsync(string tenantId, string letterId, LetterEvent letterEvent, CancellationToken ct = default);
}

public sealed class CorrespondenceRepositoryMongo : ICorrespondenceRepository
{
    public const string LetterTypesCollection = "correspondence_letter_types";
    public const string TemplatesCollection = "correspondence_templates";
    public const string LettersCollection = "correspondence_letters";

    private readonly IMongoCollection<LetterType> _letterTypes;
    private readonly IMongoCollection<LetterTemplate> _templates;
    private readonly IMongoCollection<Letter> _letters;

    public CorrespondenceRepositoryMongo(IMongoDatabase database)
    {
        _letterTypes = database.GetCollection<LetterType>(LetterTypesCollection);
        _templates = database.GetCollection<LetterTemplate>(TemplatesCollection);
        _letters = database.GetCollection<Letter>(LettersCollection);
        // Once per database per process: the repository is scoped, the indexes are not.
        if (IndexedDatabases.TryAdd(database.DatabaseNamespace.DatabaseName, true))
            EnsureIndexes();
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> IndexedDatabases = new();

    private void EnsureIndexes()
    {
        _letterTypes.Indexes.CreateOne(new CreateIndexModel<LetterType>(
            Builders<LetterType>.IndexKeys.Ascending(x => x.TenantId).Ascending(x => x.Code),
            new CreateIndexOptions { Unique = true }));
        _templates.Indexes.CreateMany([
            new CreateIndexModel<LetterTemplate>(
                Builders<LetterTemplate>.IndexKeys.Ascending(x => x.TenantId).Ascending(x => x.TemplateId).Ascending(x => x.Version),
                new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<LetterTemplate>(
                Builders<LetterTemplate>.IndexKeys.Ascending(x => x.TenantId).Ascending(x => x.LetterTypeCode)
                    .Ascending(x => x.Status).Ascending(x => x.Language)),
        ]);
        _letters.Indexes.CreateMany([
            new CreateIndexModel<Letter>(
                Builders<Letter>.IndexKeys.Ascending(x => x.TenantId).Ascending("Subjects.Kind").Ascending("Subjects.Id")),
            new CreateIndexModel<Letter>(
                Builders<Letter>.IndexKeys.Ascending(x => x.TenantId).Descending(x => x.CreatedAt)),
        ]);
    }

    public async Task<LetterType?> GetLetterTypeAsync(string tenantId, string code, CancellationToken ct = default) =>
        await _letterTypes.Find(x => x.TenantId == tenantId && x.Code == code).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<LetterType>> ListLetterTypesAsync(string tenantId, CancellationToken ct = default) =>
        await _letterTypes.Find(x => x.TenantId == tenantId).SortBy(x => x.Code).ToListAsync(ct);

    public async Task<bool> TryCreateLetterTypeAsync(LetterType letterType, CancellationToken ct = default)
    {
        try
        {
            await _letterTypes.InsertOneAsync(letterType, cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task<LetterTemplate?> GetTemplateVersionAsync(string tenantId, string templateId, int version, CancellationToken ct = default) =>
        await _templates.Find(x => x.TenantId == tenantId && x.TemplateId == templateId && x.Version == version).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<LetterTemplate>> ListTemplateVersionsAsync(string tenantId, string templateId, CancellationToken ct = default) =>
        await _templates.Find(x => x.TenantId == tenantId && x.TemplateId == templateId).SortBy(x => x.Version).ToListAsync(ct);

    public async Task<IReadOnlyList<LetterTemplate>> ListTemplatesAsync(string tenantId, string? letterTypeCode, CancellationToken ct = default)
    {
        var filter = Builders<LetterTemplate>.Filter.Eq(x => x.TenantId, tenantId);
        if (!string.IsNullOrWhiteSpace(letterTypeCode))
            filter &= Builders<LetterTemplate>.Filter.Eq(x => x.LetterTypeCode, letterTypeCode);
        return await _templates.Find(filter).SortBy(x => x.TemplateId).ThenBy(x => x.Version).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<LetterTemplate>> ListApprovedAsync(
        string tenantId, string letterTypeCode, string language, TemplateFormat format, CancellationToken ct = default) =>
        await _templates.Find(x => x.TenantId == tenantId && x.LetterTypeCode == letterTypeCode
                                   && x.Status == TemplateStatus.Approved && x.Language == language && x.Format == format)
            .ToListAsync(ct);

    public async Task<bool> TryCreateTemplateVersionAsync(LetterTemplate template, CancellationToken ct = default)
    {
        try
        {
            await _templates.InsertOneAsync(template, cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task SaveTemplateVersionAsync(LetterTemplate template, long expectedRevision, CancellationToken ct = default)
    {
        template.Revision = expectedRevision + 1;
        var result = await _templates.ReplaceOneAsync(
            x => x.TenantId == template.TenantId && x.Id == template.Id && x.Revision == expectedRevision,
            template, cancellationToken: ct);
        if (result.MatchedCount == 0)
        {
            template.Revision = expectedRevision;
            throw new CorrespondenceConcurrencyException($"Template {template.Id} changed since it was read.");
        }
    }

    public Task CreateLetterAsync(Letter letter, CancellationToken ct = default) =>
        _letters.InsertOneAsync(letter, cancellationToken: ct);

    public async Task<Letter?> GetLetterAsync(string tenantId, string letterId, CancellationToken ct = default) =>
        await _letters.Find(x => x.TenantId == tenantId && x.Id == letterId).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<Letter>> ListLettersForSubjectAsync(
        string tenantId, string subjectKind, string subjectId, int limit, CancellationToken ct = default) =>
        await _letters.Find(x => x.TenantId == tenantId
                                 && x.Subjects.Any(s => s.Kind == subjectKind && s.Id == subjectId))
            .SortByDescending(x => x.CreatedAt)
            .Limit(Math.Clamp(limit, 1, 500))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Letter>> ListRecentLettersAsync(
        string tenantId, LetterStatus? status, string? letterTypeCode, int limit, CancellationToken ct = default)
    {
        var filter = Builders<Letter>.Filter.Eq(x => x.TenantId, tenantId);
        if (status is { } s) filter &= Builders<Letter>.Filter.Eq(x => x.Status, s);
        if (!string.IsNullOrWhiteSpace(letterTypeCode)) filter &= Builders<Letter>.Filter.Eq(x => x.LetterTypeCode, letterTypeCode);
        return await _letters.Find(filter)
            .SortByDescending(x => x.CreatedAt)
            .Limit(Math.Clamp(limit, 1, 500))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyDictionary<LetterStatus, long>> CountLettersSinceAsync(string tenantId, DateTime since, CancellationToken ct = default)
    {
        var counts = await _letters.Aggregate()
            .Match(x => x.TenantId == tenantId && x.CreatedAt >= since)
            .Group(x => x.Status, g => new { Status = g.Key, Count = g.LongCount() })
            .ToListAsync(ct);
        return counts.ToDictionary(c => c.Status, c => c.Count);
    }

    public Task AppendEventAsync(string tenantId, string letterId, LetterEvent letterEvent, CancellationToken ct = default) =>
        _letters.UpdateOneAsync(
            x => x.TenantId == tenantId && x.Id == letterId,
            Builders<Letter>.Update.Push(x => x.Events, letterEvent),
            cancellationToken: ct);
}
