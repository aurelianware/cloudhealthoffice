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
        var exists = _questionnaires.Find(Key(DtrService.DefaultLibraryTenant, questionnaire.Id)).Any();
        if (!exists)
            _questionnaires.InsertOne(SerializeQuestionnaire(questionnaire, DtrService.DefaultLibraryTenant));
    }

    public async Task<Questionnaire?> FindQuestionnaireAsync(string tenantId, string id, CancellationToken ct = default)
    {
        var doc = await _questionnaires.Find(Key(tenantId, id)).FirstOrDefaultAsync(ct);
        return doc != null ? _parser.Parse<Questionnaire>(doc["fhirJson"].AsString) : null;
    }

    public Task UpsertQuestionnaireAsync(Questionnaire questionnaire, string tenantId, CancellationToken ct = default)
        => _questionnaires.ReplaceOneAsync(
            Key(tenantId, questionnaire.Id), SerializeQuestionnaire(questionnaire, tenantId),
            new ReplaceOptions { IsUpsert = true }, ct);

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
