using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using MongoDB.Bson;
using MongoDB.Driver;
using Task = System.Threading.Tasks.Task;

namespace FhirService.Services;

/// <summary>
/// MongoDB-backed <see cref="IDtrResourceStore"/>. Uses the host's
/// <see cref="IMongoClient"/> and the database named by
/// <c>MongoDb:DatabaseName</c> (default <c>CloudHealthOffice</c>, the host's
/// default: MongoDB refuses two databases differing only in case).
///
/// Two collections, each holding the FHIR JSON of one resource per document:
///   * <c>dtr_questionnaires</c> — unique on (tenantId, resourceId).
///   * <c>dtr_responses</c> — indexed on (tenantId, resourceId) and
///     (tenantId, questionnaireRef).
///
/// Registered as a SINGLETON: it ensures its indexes at construction, so a
/// per-request instance would re-issue createIndex on every call.
/// </summary>
public sealed class MongoDtrResourceStore : IDtrResourceStore
{
    public const string QuestionnaireCollectionName = "dtr_questionnaires";
    public const string ResponseCollectionName = "dtr_responses";

    private static readonly FhirJsonSerializer _serializer = new(new SerializerSettings { Pretty = false });
    private static readonly FhirJsonParser _parser = new(new ParserSettings { PermissiveParsing = true });

    private readonly IMongoCollection<BsonDocument> _questionnaires;
    private readonly IMongoCollection<BsonDocument> _responses;

    public MongoDtrResourceStore(IMongoClient client, IConfiguration configuration, ILogger<MongoDtrResourceStore> logger)
    {
        var database = client.GetDatabase(configuration["MongoDb:DatabaseName"] ?? "CloudHealthOffice");
        _questionnaires = database.GetCollection<BsonDocument>(QuestionnaireCollectionName);
        _responses = database.GetCollection<BsonDocument>(ResponseCollectionName);
        EnsureIndexes();
        logger.LogInformation("DTR service using MongoDB persistence");
    }

    private void EnsureIndexes()
    {
        _questionnaires.Indexes.CreateOne(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("tenantId").Ascending("resourceId"),
            new CreateIndexOptions { Unique = true }));

        _responses.Indexes.CreateOne(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("tenantId").Ascending("resourceId")));

        _responses.Indexes.CreateOne(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("tenantId").Ascending("questionnaireRef")));
    }

    private static FilterDefinition<BsonDocument> Key(string tenantId, string id)
        => Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("tenantId", tenantId),
            Builders<BsonDocument>.Filter.Eq("resourceId", id));

    public void SeedDefaultQuestionnaire(Questionnaire questionnaire)
    {
        // Insert with the unique tenant/id index, so concurrent pods never write
        // the same library entry twice and a seeding race is harmless.
        if (TryInsertQuestionnaireAsync(questionnaire, DtrService.DefaultLibraryTenant).GetAwaiter().GetResult())
            return;

        // Already there: upgrade it only when this build ships a newer revision
        // (a higher meta.versionId). The default library is read-only through the
        // API, so the stored copy is only ever an older seed. The replace is
        // fenced on the version read, so of two pods upgrading at once one wins
        // and the other's write is a no-op.
        var stored = FindQuestionnaireAsync(DtrService.DefaultLibraryTenant, questionnaire.Id).GetAwaiter().GetResult();
        if (DtrService.IsNewerSeed(questionnaire, stored))
        {
            TryReplaceQuestionnaireAsync(questionnaire, DtrService.DefaultLibraryTenant, stored!.Meta?.VersionId)
                .GetAwaiter().GetResult();
        }
    }

    public async Task<Questionnaire?> FindQuestionnaireAsync(string tenantId, string id, CancellationToken ct = default)
    {
        var doc = await _questionnaires.Find(Key(tenantId, id)).FirstOrDefaultAsync(ct);
        return doc != null ? _parser.Parse<Questionnaire>(doc["fhirJson"].AsString) : null;
    }

    public async Task<bool> TryInsertQuestionnaireAsync(Questionnaire questionnaire, string tenantId, CancellationToken ct = default)
    {
        try
        {
            await _questionnaires.InsertOneAsync(SerializeQuestionnaire(questionnaire, tenantId), cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task<bool> TryReplaceQuestionnaireAsync(
        Questionnaire questionnaire, string tenantId, string? expectedVersion, CancellationToken ct = default)
    {
        var key = Key(tenantId, questionnaire.Id);
        var previous = await _questionnaires.Find(key).FirstOrDefaultAsync(ct);
        if (previous == null) return false;
        var json = previous["fhirJson"].AsString;
        if (_parser.Parse<Questionnaire>(json).Meta?.VersionId != expectedVersion) return false;

        // Fence with the exact stored JSON, including legacy resources without meta.
        // Patch only the payload: preserve createdAt and fields from newer pods.
        var result = await _questionnaires.UpdateOneAsync(
            Builders<BsonDocument>.Filter.And(key, Builders<BsonDocument>.Filter.Eq("fhirJson", json)),
            Builders<BsonDocument>.Update.Set("fhirJson", _serializer.SerializeToString(questionnaire)),
            cancellationToken: ct);
        return result.ModifiedCount == 1;
    }

    public async Task<IReadOnlyList<Questionnaire>> ListVisibleQuestionnairesAsync(string tenantId, CancellationToken ct = default)
    {
        var docs = await _questionnaires.Find(Builders<BsonDocument>.Filter.In("tenantId",
            new[] { tenantId, DtrService.DefaultLibraryTenant })).ToListAsync(ct);
        // Resolve overrides BEFORE search filters/pagination: a hidden default
        // must not reappear merely because the tenant's override does not match.
        return docs.GroupBy(d => d["resourceId"].AsString, StringComparer.Ordinal)
            .Select(g => g.FirstOrDefault(d => d["tenantId"].AsString == tenantId) ?? g.First())
            .Select(d => _parser.Parse<Questionnaire>(d["fhirJson"].AsString)).ToList();
    }

    public async Task<IReadOnlyList<QuestionnaireResponse>> ListResponsesAsync(string tenantId, CancellationToken ct = default)
    {
        var docs = await _responses.Find(Builders<BsonDocument>.Filter.Eq("tenantId", tenantId)).ToListAsync(ct);
        return docs.Select(d => _parser.Parse<QuestionnaireResponse>(d["fhirJson"].AsString)).ToList();
    }

    public async Task<QuestionnaireResponse?> FindResponseAsync(string tenantId, string id, CancellationToken ct = default)
    {
        var doc = await _responses.Find(Key(tenantId, id)).FirstOrDefaultAsync(ct);
        return doc != null ? _parser.Parse<QuestionnaireResponse>(doc["fhirJson"].AsString) : null;
    }

    public Task InsertResponseAsync(QuestionnaireResponse response, string tenantId, CancellationToken ct = default)
        => _responses.InsertOneAsync(new BsonDocument
        {
            { "tenantId", tenantId },
            { "resourceId", response.Id },
            { "questionnaireRef", response.Questionnaire ?? "" },
            { "fhirJson", _serializer.SerializeToString(response) },
            { "createdAt", DateTimeOffset.UtcNow.ToString("o") },
        }, cancellationToken: ct);

    private static BsonDocument SerializeQuestionnaire(Questionnaire q, string tenantId) => new()
    {
        { "tenantId", tenantId },
        { "resourceId", q.Id },
        { "fhirJson", _serializer.SerializeToString(q) },
        { "createdAt", DateTimeOffset.UtcNow.ToString("o") },
    };
}
