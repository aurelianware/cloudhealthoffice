using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EncounterSubmissionService.Models;
using EncounterSubmissionService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CloudHealthOffice.EncounterSubmissionService.Tests;

public class BatchIntegrityTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }

    private static (EncounterSubmissionServiceImpl Service, Mock<IMongoCollection<EncounterSubmission>> Collection)
        Create(HttpMessageHandler handler)
    {
        var collection = new Mock<IMongoCollection<EncounterSubmission>>();
        var database = new Mock<IMongoDatabase>();
        database.Setup(d => d.GetCollection<EncounterSubmission>("encounter_submissions", null))
            .Returns(collection.Object);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("ClaimsService"))
            .Returns(new HttpClient(handler) { BaseAddress = new Uri("http://claims-service") });
        return (new EncounterSubmissionServiceImpl(database.Object, factory.Object,
            new ConfigurationBuilder().Build(), NullLogger<EncounterSubmissionServiceImpl>.Instance), collection);
    }

    private static EncounterSubmission Submission(string id, string tenant = "tenant-1", string state = "FL")
        => new() { Id = id, ClaimId = "claim-" + id, TenantId = tenant, StateCode = state };

    [Fact]
    public async Task MissingClaim_DoesNotMarkAnySubmissionBatched()
    {
        var handler = new Handler(r => r.RequestUri!.AbsolutePath.EndsWith("claim-1")
            ? new(HttpStatusCode.OK) { Content = JsonContent.Create(new { id = "claim-1" }) }
            : new(HttpStatusCode.ServiceUnavailable));
        var (service, collection) = Create(handler);

        var act = () => service.BuildFmmisSubmissionBatchAsync(new[] { Submission("1"), Submission("2") }, "tenant-1");

        await act.Should().ThrowAsync<HttpRequestException>();
        collection.Verify(c => c.UpdateManyAsync(It.IsAny<FilterDefinition<EncounterSubmission>>(),
            It.IsAny<UpdateDefinition<EncounterSubmission>>(), It.IsAny<UpdateOptions>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("tenant-2", "FL")]
    [InlineData("tenant-1", "CA")]
    [InlineData("tenant-1", "OR")]
    public async Task WrongTenantOrState_IsRefusedBeforeFetchingOrWriting(string tenant, string state)
    {
        var handler = new Handler(_ => new(HttpStatusCode.OK));
        var (service, collection) = Create(handler);

        var act = () => service.BuildFmmisSubmissionBatchAsync(new[] { Submission("1", tenant, state) }, "tenant-1");

        await act.Should().ThrowAsync<Exception>();
        handler.Calls.Should().Be(0);
        collection.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"id\":\"another-claim\"}")]
    public async Task UnidentifiedOrWrongClaim_IsNotBatched(string payload)
    {
        var (service, collection) = Create(new Handler(_ => new(HttpStatusCode.OK)
            { Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json") }));

        var act = () => service.BuildFmmisSubmissionBatchAsync(new[] { Submission("1") }, "tenant-1");

        await act.Should().ThrowAsync<JsonException>();
        collection.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CompleteBatch_ContainsExactlyTheRequestedClaims()
    {
        var (service, collection) = Create(new Handler(r => new(HttpStatusCode.OK)
            { Content = JsonContent.Create(new { id = r.RequestUri!.Segments.Last() }) }));
        collection.Setup(c => c.UpdateManyAsync(It.IsAny<FilterDefinition<EncounterSubmission>>(),
                It.IsAny<UpdateDefinition<EncounterSubmission>>(), It.IsAny<UpdateOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpdateResult.Acknowledged(2, 2, null));

        var batch = await service.BuildFmmisSubmissionBatchAsync(new[] { Submission("1"), Submission("2") }, "tenant-1");

        batch.TransactionCount.Should().Be(2);
        batch.ClaimIds.Should().Equal("claim-1", "claim-2");
        JsonSerializer.Deserialize<JsonElement[]>(batch.Content)!.Select(c => c.GetProperty("id").GetString())
            .Should().Equal("claim-1", "claim-2");
    }

    [Theory]
    [InlineData("A", EncounterSubmissionStatus.SyntaxAccepted)]
    [InlineData("E", EncounterSubmissionStatus.SyntaxAcceptedWithErrors)]
    public async Task SyntaxAcknowledgment_DoesNotAssertFinalStateAcceptance(string ack, EncounterSubmissionStatus expected)
    {
        var (service, collection) = Create(new Handler(_ => new(HttpStatusCode.OK)));
        var cursor = new Mock<IAsyncCursor<EncounterSubmission>>();
        cursor.SetupGet(c => c.Current).Returns(new[] { Submission("1") });
        cursor.SetupSequence(c => c.MoveNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true).ReturnsAsync(false);
        collection.Setup(c => c.FindAsync(It.IsAny<FilterDefinition<EncounterSubmission>>(),
                It.IsAny<FindOptions<EncounterSubmission, EncounterSubmission>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(cursor.Object);
        UpdateDefinition<EncounterSubmission>? written = null;
        collection.Setup(c => c.UpdateManyAsync(It.IsAny<FilterDefinition<EncounterSubmission>>(),
                It.IsAny<UpdateDefinition<EncounterSubmission>>(), It.IsAny<UpdateOptions>(), It.IsAny<CancellationToken>()))
            .Callback<FilterDefinition<EncounterSubmission>, UpdateDefinition<EncounterSubmission>, UpdateOptions, CancellationToken>(
                (_, update, _, _) => written = update)
            .ReturnsAsync(new UpdateResult.Acknowledged(1, 1, null));

        await service.ProcessAcknowledgmentAsync("batch-1", $"ST*999*1~AK9*{ack}*1*1*1~", "tenant-1");

        var document = written!.Render(new RenderArgs<EncounterSubmission>(
            MongoDB.Bson.Serialization.BsonSerializer.SerializerRegistry.GetSerializer<EncounterSubmission>(),
            MongoDB.Bson.Serialization.BsonSerializer.SerializerRegistry));
        document["$set"]["Status"].AsInt32.Should().Be((int)expected);
        expected.Should().NotBe(EncounterSubmissionStatus.Accepted);
    }
}
