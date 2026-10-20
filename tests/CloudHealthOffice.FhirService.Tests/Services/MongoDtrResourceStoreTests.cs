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
    public async System.Threading.Tasks.Task Insert_is_create_only_and_a_conditional_update_replaces_the_tenants_copy()
    {
        var store = NewStore();
        (await store.TryInsertQuestionnaireAsync(Form("q-upsert", "First"), Tenant)).Should().BeTrue();
        (await store.TryInsertQuestionnaireAsync(Form("q-upsert", "Duplicate"), Tenant)).Should().BeFalse();
        (await store.TryReplaceQuestionnaireAsync(Form("q-upsert", "Second"), Tenant, null)).Should().BeTrue();

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
    [Fact]
    public async System.Threading.Tasks.Task Existing_pods_and_restarts_search_and_update_the_same_durable_resources()
    {
        var reader = NewService(); // alive before another pod writes
        var writer = NewService();
        await writer.CreateQuestionnaireAsync(Form("q-live", "Original"), Tenant);
        (await reader.QuestionnaireExistsAsync("Questionnaire/q-live", Tenant)).Should().BeTrue();
        (await reader.QuestionnaireExistsAsync("q-live", "tenant-b")).Should().BeFalse();
        var first = await reader.SearchQuestionnairesAsync(new() { Id = "q-live" }, Tenant);
        first.Total.Should().Be(1);

        var update = Form("ignored-body-id", "Updated after restart");
        update.Name = "durable-updated";
        update.Status = PublicationStatus.Draft;
        (await NewService().UpdateQuestionnaireAsync("q-live", update, Tenant))!.Meta.VersionId.Should().Be("2");
        var result = await reader.SearchQuestionnairesAsync(new()
        {
            Id = "q-live", Name = "UPDATED", Title = "AFTER RESTART", Status = "draft",
        }, Tenant);
        result.Total.Should().Be(1);
        result.Items.Should().ContainSingle().Which.Title.Should().Be("Updated after restart");
        (await writer.GetQuestionnaireAsync("q-live", Tenant))!.Meta.VersionId.Should().Be("2");
        (await NewService().GetQuestionnairePackageAsync("q-live", null, Tenant))!
            .Entry.Single().Resource.Should().BeOfType<Questionnaire>().Which.Title.Should().Be("Updated after restart");
    }

    [Fact]
    public async System.Threading.Tasks.Task Shared_defaults_are_shadowed_before_filtering_and_overrides_never_cross_tenants()
    {
        var reader = NewService();
        await NewService().UpdateQuestionnaireAsync("q-imaging-mri", Form("ignored", "Tenant custom form"), Tenant);
        await NewService().UpdateQuestionnaireAsync("q-imaging-mri", Form("ignored", "Another tenant"), "tenant-b");
        var hidden = await reader.SearchQuestionnairesAsync(new() { Id = "q-imaging-mri", Title = "MRI" }, Tenant);
        hidden.Total.Should().Be(0); // matching default must stay hidden
        var own = await reader.SearchQuestionnairesAsync(new() { Id = "q-imaging-mri" }, Tenant);
        own.Items.Should().ContainSingle().Which.Title.Should().Be("Tenant custom form");
        (await NewService().GetQuestionnaireAsync("q-imaging-mri", "tenant-c"))!.Title.Should().Contain("MRI");
        (await NewStore().FindQuestionnaireAsync(DtrService.DefaultLibraryTenant, "q-imaging-mri"))!
            .Title.Should().Contain("MRI");
        (await NewService().UpdateQuestionnaireAsync("missing", Form("missing", "Missing"), Tenant)).Should().BeNull();
    }

    [Fact]
    public async System.Threading.Tasks.Task Questionnaire_search_is_stably_paged_and_uses_exact_stored_tenant_keys()
    {
        foreach (var id in new[] { "q-z", "q-a", "q-m" })
            await NewService().CreateQuestionnaireAsync(Form(id, "Paging form"), Tenant);
        await NewService().CreateQuestionnaireAsync(Form("q-b", "Paging foreign"), Tenant + ":nested");
        await NewService().CreateQuestionnaireAsync(Form("q-c", "Paging foreign"), "tenant-b");
        var first = await NewService().SearchQuestionnairesAsync(new() { Title = "Paging", Count = 2, Page = 1 }, Tenant);
        var second = await NewService().SearchQuestionnairesAsync(new() { Title = "Paging", Count = 2, Page = 2 }, Tenant);
        first.Total.Should().Be(3);
        second.Total.Should().Be(3);
        first.Items.Select(q => q.Id).Should().Equal("q-a", "q-m");
        second.Items.Select(q => q.Id).Should().Equal("q-z");
    }

    private static QuestionnaireResponse Answer(string patient, string questionnaire = "Questionnaire/q-imaging-mri") => new()
    {
        Status = QuestionnaireResponse.QuestionnaireResponseStatus.Completed,
        Questionnaire = questionnaire,
        Subject = new ResourceReference(patient),
    };

    [Fact]
    public async System.Threading.Tasks.Task Response_search_survives_restarts_and_filters_before_count_and_pagination()
    {
        var reader = NewService();
        var writer = NewService();
        var one = await writer.SubmitResponseAsync(Answer("Patient/member-1"), Tenant);
        var two = await writer.SubmitResponseAsync(Answer("member-1"), Tenant);
        await writer.SubmitResponseAsync(Answer("Patient/member-2"), Tenant);
        await writer.SubmitResponseAsync(Answer("Patient/member-1"), "tenant-b");
        await writer.SubmitResponseAsync(Answer("Patient/member-1"), Tenant + ":nested");
        await writer.SubmitResponseAsync(Answer("Patient/member-1", "Questionnaire/q-dme-equipment"), Tenant);
        var amended = Answer("Patient/member-1");
        amended.Status = QuestionnaireResponse.QuestionnaireResponseStatus.Amended;
        await writer.SubmitResponseAsync(amended, Tenant);
        var filter = new QuestionnaireResponseSearchParams
        {
            Patient = "Patient/member-1", QuestionnaireRef = "Questionnaire/q-imaging-mri", Status = "completed", Count = 1,
        };
        var first = await reader.SearchResponsesAsync(filter, Tenant);
        filter.Page = 2;
        var second = await NewService().SearchResponsesAsync(filter, Tenant);
        first.Total.Should().Be(2);
        second.Total.Should().Be(2);
        first.Items.Concat(second.Items).Select(r => r.Id)
            .Should().Equal(new[] { one.Id, two.Id }.OrderBy(id => id, StringComparer.Ordinal));
        var byId = await reader.SearchResponsesAsync(new() { Id = one.Id }, Tenant);
        byId.Items.Should().ContainSingle().Which.Id.Should().Be(one.Id);
        (await reader.SearchResponsesAsync(new() { Id = one.Id }, "tenant-b")).Total.Should().Be(0);
    }

    [Fact]
    public async System.Threading.Tasks.Task Legacy_payload_only_documents_remain_searchable_and_updates_preserve_envelope_fields()
    {
        NewService();
        var serializer = new Hl7.Fhir.Serialization.FhirJsonSerializer();
        await Questionnaires.InsertOneAsync(new BsonDocument
        {
            { "tenantId", Tenant }, { "resourceId", "q-legacy" },
            { "fhirJson", serializer.SerializeToString(Form("q-legacy", "Legacy form")) },
            { "createdAt", "original timestamp" }, { "futureEnvelopeField", "preserve" },
        });
        await Responses.InsertOneAsync(new BsonDocument
        {
            { "tenantId", Tenant }, { "resourceId", "qr-legacy" },
            { "fhirJson", serializer.SerializeToString(new QuestionnaireResponse
                { Id = "qr-legacy", Subject = new ResourceReference("Patient/legacy"),
                  Questionnaire = "Questionnaire/q-legacy", Status = QuestionnaireResponse.QuestionnaireResponseStatus.Completed }) },
        });
        var reader = NewService();
        (await reader.SearchQuestionnairesAsync(new() { Id = "q-legacy" }, Tenant)).Total.Should().Be(1);
        (await reader.SearchResponsesAsync(new() { Patient = "legacy" }, Tenant)).Total.Should().Be(1);
        (await reader.UpdateQuestionnaireAsync("q-legacy", Form("ignored", "Migrated by normal update"), Tenant))!
            .Meta.VersionId.Should().Be("1");
        var doc = await Questionnaires.Find(Builders<BsonDocument>.Filter.Eq("resourceId", "q-legacy")).SingleAsync();
        doc["createdAt"].AsString.Should().Be("original timestamp");
        doc["futureEnvelopeField"].AsString.Should().Be("preserve");
    }

    [Fact]
    public async System.Threading.Tasks.Task Concurrent_creates_with_the_same_id_never_overwrite_the_first_form()
    {
        var services = new[] { NewService(), NewService() };
        var created = await System.Threading.Tasks.Task.WhenAll(services.Select((s, i) =>
            s.CreateQuestionnaireAsync(Form("q-race", $"Form {i}"), Tenant)));
        created.Select(q => q.Id).Distinct().Should().HaveCount(2);
        var reader = NewService();
        foreach (var form in created)
            (await reader.GetQuestionnaireAsync(form.Id, Tenant))!.Title.Should().Be(form.Title);
        (await reader.SearchQuestionnairesAsync(new() { Title = "Form " }, Tenant)).Total.Should().Be(2);
    }

    [Fact]
    public async System.Threading.Tasks.Task Conditional_updates_have_one_winner_and_reject_stale_or_foreign_writes()
    {
        await NewService().CreateQuestionnaireAsync(Form("q-cas", "Original"), Tenant);
        var contenders = new[] { Form("q-cas", "One"), Form("q-cas", "Two") };
        foreach (var q in contenders) q.Meta = new Meta { VersionId = "2" };
        var results = await System.Threading.Tasks.Task.WhenAll(contenders.Select(q =>
            NewStore().TryReplaceQuestionnaireAsync(q, Tenant, "1")));
        results.Count(won => won).Should().Be(1);
        (await NewStore().TryReplaceQuestionnaireAsync(Form("q-cas", "Stale"), Tenant, "1")).Should().BeFalse();
        (await NewStore().TryReplaceQuestionnaireAsync(Form("q-cas", "Foreign"), "tenant-b", "2")).Should().BeFalse();
        (await NewService().GetQuestionnaireAsync("q-cas", Tenant))!.Meta.VersionId.Should().Be("2");
    }

    [Fact]
    public async System.Threading.Tasks.Task Concurrent_pod_seeding_keeps_one_copy_of_each_default()
    {
        NewStore();
        await System.Threading.Tasks.Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            System.Threading.Tasks.Task.Run(() => NewService())));
        (await Questionnaires.CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("tenantId", DtrService.DefaultLibraryTenant)))
            .Should().Be(5);
    }

    [Fact]
    public async System.Threading.Tasks.Task Response_intake_checks_the_durable_library_from_an_already_running_pod()
    {
        var reader = NewService();
        var controller = new global::FhirService.Controllers.DtrController(reader,
            new FhirBundleBuilder(_configuration), NullLogger<global::FhirService.Controllers.DtrController>.Instance);
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Items["TenantId"] = Tenant;
        context.Request.Scheme = "https";
        context.Request.Host = new Microsoft.AspNetCore.Http.HostString("test.example.com");
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = context };
        await NewService().CreateQuestionnaireAsync(Form("q-another-pod", "New form"), Tenant);
        var response = Answer("Patient/member-1", "Questionnaire/q-another-pod");
        var result = await controller.SubmitQuestionnaireResponse(response, CancellationToken.None);
        result.Should().BeOfType<Microsoft.AspNetCore.Mvc.ObjectResult>().Which.StatusCode.Should().Be(201);
        (await NewService().GetResponseAsync(response.Id, Tenant)).Should().NotBeNull();
        await NewService().CreateQuestionnaireAsync(Form("q-foreign", "Foreign"), "tenant-b");
        var refused = await controller.SubmitQuestionnaireResponse(
            Answer("Patient/member-1", "Questionnaire/q-foreign"), CancellationToken.None);
        refused.Should().BeOfType<Microsoft.AspNetCore.Mvc.ObjectResult>().Which.StatusCode.Should().Be(400);
    }

}
