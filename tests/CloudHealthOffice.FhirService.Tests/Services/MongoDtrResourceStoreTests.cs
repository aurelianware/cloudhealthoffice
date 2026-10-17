using CloudHealthOffice.Testing.Mongo;
using FhirService.Models;
using FhirService.Services;
using FluentAssertions;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CloudHealthOffice.FhirService.Tests.Services;

/// <summary>
/// <see cref="MongoDtrResourceStore"/> on a real mongod (EphemeralMongo), and
/// <see cref="DtrService"/> over it: what is written survives into a new
/// service instance (a restart), in the collections, documents and indexes
/// DtrService has always used.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class MongoDtrResourceStoreTests : IAsyncLifetime
{
    private const string Tenant = "tenant-a";

    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;
    private IConfiguration _configuration = null!;

    public MongoDtrResourceStoreTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public System.Threading.Tasks.Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("dtr_store");
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MongoDb:DatabaseName"] = _database.DatabaseNamespace.DatabaseName,
            })
            .Build();
        return System.Threading.Tasks.Task.CompletedTask;
    }

    public System.Threading.Tasks.Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);

    private MongoDtrResourceStore NewStore()
        => new(_mongo.Client, _configuration, NullLogger<MongoDtrResourceStore>.Instance);

    private DtrService NewService()
        => new(Options.Create(new DtrConfig { Enabled = true }), NewStore(), NullLogger<DtrService>.Instance);

    private IMongoCollection<BsonDocument> Questionnaires
        => _database.GetCollection<BsonDocument>(MongoDtrResourceStore.QuestionnaireCollectionName);

    private IMongoCollection<BsonDocument> Responses
        => _database.GetCollection<BsonDocument>(MongoDtrResourceStore.ResponseCollectionName);

    private static Questionnaire Form(string id, string title) => new()
    {
        Id = id,
        Status = PublicationStatus.Active,
        Title = title,
        Item = new List<Questionnaire.ItemComponent>
        {
            new() { LinkId = "1", Text = "Q1", Type = Questionnaire.QuestionnaireItemType.String },
        },
    };

    [Fact]
    public async System.Threading.Tasks.Task Construction_creates_the_dtr_indexes()
    {
        NewStore();

        var questionnaireIndexes = await (await Questionnaires.Indexes.ListAsync()).ToListAsync();
        questionnaireIndexes.Should().Contain(i =>
            i["key"].AsBsonDocument.Equals(new BsonDocument { { "tenantId", 1 }, { "resourceId", 1 } })
            && i.Contains("unique") && i["unique"].ToBoolean());

        var responseKeys = (await (await Responses.Indexes.ListAsync()).ToListAsync())
            .Select(i => i["key"].AsBsonDocument)
            .ToList();
        responseKeys.Should().Contain(new BsonDocument { { "tenantId", 1 }, { "resourceId", 1 } });
        responseKeys.Should().Contain(new BsonDocument { { "tenantId", 1 }, { "questionnaireRef", 1 } });
    }

    [Fact]
    public async System.Threading.Tasks.Task The_default_library_is_seeded_once_and_read_back_by_every_tenant()
    {
        NewService();
        NewService(); // a restart must not insert the seeds again

        var defaults = await Questionnaires
            .Find(Builders<BsonDocument>.Filter.Eq("tenantId", DtrService.DefaultLibraryTenant))
            .ToListAsync();
        defaults.Should().HaveCount(5);
        defaults.Select(d => d["resourceId"].AsString).Should().Contain("q-imaging-mri");

        var mri = await NewService().GetQuestionnaireAsync("q-imaging-mri", Tenant);
        mri.Should().NotBeNull();
        mri!.Title.Should().Contain("MRI");
    }

    [Fact]
    public async System.Threading.Tasks.Task A_created_questionnaire_round_trips_through_mongo()
    {
        var created = await NewService().CreateQuestionnaireAsync(Form("q-roundtrip", "Round-trip form"), Tenant);

        var doc = await Questionnaires.Find(Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("tenantId", Tenant),
            Builders<BsonDocument>.Filter.Eq("resourceId", "q-roundtrip"))).SingleAsync();
        doc.Names.Should().BeEquivalentTo("_id", "tenantId", "resourceId", "fhirJson", "createdAt");

        // A new service instance has nothing in process: the read comes from Mongo.
        var read = await NewService().GetQuestionnaireAsync("q-roundtrip", Tenant);
        read.Should().NotBeNull();
        read!.Id.Should().Be(created.Id);
        read.Title.Should().Be("Round-trip form");
        read.Meta.VersionId.Should().Be("1");
        read.Meta.Profile.Should().Equal(created.Meta.Profile);
        read.Item.Should().ContainSingle().Which.LinkId.Should().Be("1");

        (await NewStore().FindQuestionnaireAsync("tenant-b", "q-roundtrip")).Should().BeNull();
    }

    [Fact]
    public async System.Threading.Tasks.Task An_upsert_replaces_the_tenants_copy()
    {
        var store = NewStore();
        await store.UpsertQuestionnaireAsync(Form("q-upsert", "First"), Tenant);
        await store.UpsertQuestionnaireAsync(Form("q-upsert", "Second"), Tenant);

        (await Questionnaires.CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("resourceId", "q-upsert")))
            .Should().Be(1);
        (await store.FindQuestionnaireAsync(Tenant, "q-upsert"))!.Title.Should().Be("Second");
    }

    [Fact]
    public async System.Threading.Tasks.Task A_submitted_response_round_trips_through_mongo()
    {
        var submitted = await NewService().SubmitResponseAsync(new QuestionnaireResponse
        {
            Status = QuestionnaireResponse.QuestionnaireResponseStatus.Completed,
            Questionnaire = "Questionnaire/q-imaging-mri",
            Subject = new ResourceReference("Patient/456"),
            Item = new List<QuestionnaireResponse.ItemComponent>
            {
                new()
                {
                    LinkId = "1",
                    Answer = new List<QuestionnaireResponse.AnswerComponent>
                    {
                        new() { Value = new FhirString("back pain") },
                    },
                },
            },
        }, Tenant);

        var doc = await Responses.Find(Builders<BsonDocument>.Filter.Eq("resourceId", submitted.Id)).SingleAsync();
        doc["tenantId"].AsString.Should().Be(Tenant);
        doc["questionnaireRef"].AsString.Should().Be("Questionnaire/q-imaging-mri");
        doc.Names.Should().BeEquivalentTo("_id", "tenantId", "resourceId", "questionnaireRef", "fhirJson", "createdAt");

        var read = await NewService().GetResponseAsync(submitted.Id, Tenant);
        read.Should().NotBeNull();
        read!.Status.Should().Be(QuestionnaireResponse.QuestionnaireResponseStatus.Completed);
        read.Subject.Reference.Should().Be("Patient/456");
        read.Item.Should().ContainSingle().Which.Answer.Should().ContainSingle()
            .Which.Value.Should().BeOfType<FhirString>().Which.Value.Should().Be("back pain");

        (await NewService().GetResponseAsync(submitted.Id, "tenant-b")).Should().BeNull();
    }
}
