using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EncounterSubmissionService.Models;
using EncounterSubmissionService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace CloudHealthOffice.EncounterSubmissionService.Tests.Florida;

/// <summary>
/// The Florida (FMMIS) encounter service without a database: the 60-day window
/// from the tenant's compliance config, a clean batch, and the 999 parser.
/// </summary>
public class FloridaEncounterServiceTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private readonly Mock<IMongoCollection<EncounterSubmission>> _collection = new();
    private readonly Mock<IHttpClientFactory> _http = new();
    private EncounterSubmission? _inserted;

    public FloridaEncounterServiceTests()
    {
        _collection.Setup(c => c.InsertOneAsync(It.IsAny<EncounterSubmission>(), It.IsAny<InsertOneOptions>(), It.IsAny<CancellationToken>()))
            .Callback((EncounterSubmission s, InsertOneOptions _, CancellationToken _) => _inserted = s)
            .Returns(Task.CompletedTask);
        _collection.Setup(c => c.UpdateManyAsync(It.IsAny<FilterDefinition<EncounterSubmission>>(),
                It.IsAny<UpdateDefinition<EncounterSubmission>>(), It.IsAny<UpdateOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpdateResult.Acknowledged(2, 2, null));
    }

    private EncounterSubmissionServiceImpl Service()
    {
        var database = new Mock<IMongoDatabase>();
        database.Setup(d => d.GetCollection<EncounterSubmission>("encounter_submissions", null)).Returns(_collection.Object);
        return new EncounterSubmissionServiceImpl(database.Object, _http.Object,
            new ConfigurationBuilder().Build(), NullLogger<EncounterSubmissionServiceImpl>.Instance);
    }

    private Handler UseClient(string name, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new Handler(respond);
        _http.Setup(f => f.CreateClient(name)).Returns(new HttpClient(handler) { BaseAddress = new Uri($"http://{name.ToLowerInvariant()}") });
        return handler;
    }

    // ── Creating a tracking record ─────────────────────────────────────

    [Fact]
    public async Task NewRecord_UsesTheTenantsComplianceWindow()
    {
        var handler = UseClient("ReferenceDataService", _ => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { encounterSubmissionDays = 45, promptPayElectronicDays = 15 })
        });
        var adjudicated = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

        var record = await Service().CreateSubmissionRecordAsync("claim-1", "tenant-1", adjudicated);

        record.Should().BeSameAs(_inserted);
        record.SubmissionDeadline.Should().Be(adjudicated.AddDays(45));
        record.StateCode.Should().Be("FL");
        record.Status.Should().Be(EncounterSubmissionStatus.Pending);
        record.TenantId.Should().Be("tenant-1");
        record.ClaimAdjudicatedAt.Should().Be(adjudicated);
        var request = handler.Requests.Single();
        request.RequestUri!.AbsolutePath.Should().Be("/api/compliance-config/tenant-1/state");
        request.Headers.GetValues("X-Tenant-ID").Should().Equal("tenant-1");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{}")]
    [InlineData(HttpStatusCode.OK, "{\"encounterSubmissionDays\":0}")]
    [InlineData(HttpStatusCode.OK, "null")]
    public async Task NewRecord_FallsBackToSixtyDays_WhenTheConfigIsMissingOrEmpty(HttpStatusCode status, string body)
    {
        UseClient("ReferenceDataService", _ => new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        var adjudicated = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

        var record = await Service().CreateSubmissionRecordAsync("claim-1", "tenant-1", adjudicated);

        record.SubmissionDeadline.Should().Be(adjudicated.AddDays(60));
    }

    [Fact]
    public async Task NewRecord_FallsBackToSixtyDays_WhenReferenceDataIsUnreachable()
    {
        UseClient("ReferenceDataService", _ => throw new HttpRequestException("connection refused"));
        var adjudicated = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

        var record = await Service().CreateSubmissionRecordAsync("claim-1", "tenant-1", adjudicated);

        record.SubmissionDeadline.Should().Be(adjudicated.AddDays(60));
        _inserted.Should().NotBeNull();
    }

    // ── Batching ───────────────────────────────────────────────────────

    private static EncounterSubmission Sub(string id, EncounterSubmissionStatus status = EncounterSubmissionStatus.Pending) =>
        new() { Id = id, ClaimId = "claim-" + id, TenantId = "tenant-1", StateCode = "FL", Status = status };

    [Fact]
    public async Task CleanBatch_FetchesEachClaim_AndMarksTheRecordsBatched()
    {
        var handler = UseClient("ClaimsService", r => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { id = Uri.UnescapeDataString(r.RequestUri!.AbsolutePath.Split('/').Last()), total = 10m })
        });

        var file = await Service().BuildFmmisSubmissionBatchAsync(
            new[] { Sub("1"), Sub("2", EncounterSubmissionStatus.Rejected) }, "tenant-1");

        file.TransactionCount.Should().Be(2);
        file.ClaimIds.Should().Equal("claim-1", "claim-2");
        file.FileName.Should().StartWith($"FMMIS_tenant-1_{file.BatchId}_").And.EndWith(".json");
        JsonDocument.Parse(file.Content).RootElement.GetArrayLength().Should().Be(2);
        handler.Requests.Should().OnlyContain(r => r.Headers.GetValues("X-Tenant-ID").Single() == "tenant-1");
        _collection.Verify(c => c.UpdateManyAsync(It.IsAny<FilterDefinition<EncounterSubmission>>(),
            It.IsAny<UpdateDefinition<EncounterSubmission>>(), It.IsAny<UpdateOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ClaimIdsAreEscapedInTheClaimsUrl()
    {
        var handler = UseClient("ClaimsService", _ => new(HttpStatusCode.OK) { Content = JsonContent.Create(new { id = "a/b?c" }) });
        var sub = Sub("1");
        sub.ClaimId = "a/b?c";

        await Service().BuildFmmisSubmissionBatchAsync(new[] { sub }, "tenant-1");

        handler.Requests.Single().RequestUri!.AbsolutePath.Should().Be("/api/claims/a%2Fb%3Fc");
    }

    [Theory]
    [InlineData("{\"id\":\"some-other-claim\"}")]
    [InlineData("{\"total\":10}")]
    [InlineData("[]")]
    public async Task AClaimResponseForTheWrongClaim_RefusesTheBatch(string body)
    {
        UseClient("ClaimsService", _ => new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });

        var act = () => Service().BuildFmmisSubmissionBatchAsync(new[] { Sub("1") }, "tenant-1");

        await act.Should().ThrowAsync<JsonException>();
        _collection.Verify(c => c.UpdateManyAsync(It.IsAny<FilterDefinition<EncounterSubmission>>(),
            It.IsAny<UpdateDefinition<EncounterSubmission>>(), It.IsAny<UpdateOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(EncounterSubmissionStatus.Batched)]
    [InlineData(EncounterSubmissionStatus.Accepted)]
    [InlineData(EncounterSubmissionStatus.Submitted)]
    public async Task AlreadyProcessedRecords_CannotBeBatchedAgain(EncounterSubmissionStatus status)
    {
        var act = () => Service().BuildFmmisSubmissionBatchAsync(new[] { Sub("1", status) }, "tenant-1");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task DuplicateClaims_OrAnEmptyBatch_AreRefused()
    {
        var dupe = Sub("2");
        dupe.ClaimId = "claim-1";

        await Service().Invoking(s => s.BuildFmmisSubmissionBatchAsync(new[] { Sub("1"), dupe }, "tenant-1"))
            .Should().ThrowAsync<ArgumentException>();
        await Service().Invoking(s => s.BuildFmmisSubmissionBatchAsync(Array.Empty<EncounterSubmission>(), "tenant-1"))
            .Should().ThrowAsync<ArgumentException>();
        await Service().Invoking(s => s.BuildFmmisSubmissionBatchAsync(new[] { Sub("1") }, " "))
            .Should().ThrowAsync<ArgumentException>();
    }

    // ── 999 parsing ────────────────────────────────────────────────────

    [Theory]
    [InlineData("ST*999*0001~AK1*HC*1~AK9*A*1*1*1~SE*4*0001~", "A")]
    [InlineData("ST*999*0001~AK9*E*1*1*1~", "E")]
    [InlineData("ST*999*0001~AK9*R*1*1*0~", "R")]
    [InlineData("ST*999*0001~SE*2*0001~", "R")]
    public void Parse999_ReadsTheAk9Code_DefaultingToRejected(string content, string expected)
    {
        var (code, _) = EncounterSubmissionServiceImpl.Parse999Acknowledgment(content);

        code.Should().Be(expected);
    }

    [Fact]
    public void Parse999_ReportsTheSyntaxErrorCodes_NotTheLoopOrElementReference()
    {
        // IK3*NM1*12*2010BA*8: segment NM1 at 12 in loop 2010BA, error 8.
        // IK4*3*66*7: element 3 (data element 66), error 7.
        var (code, errors) = EncounterSubmissionServiceImpl.Parse999Acknowledgment(
            "ST*999*0001~\nIK3*NM1*12*2010BA*8~\nIK4*3*66*7~\nIK3*N4*14**3~\nIK4*2**1~\nIK3*N4~\nAK9*R*1*1*0~");

        code.Should().Be("R");
        errors.Should().Equal(
            "Segment NM1 position 12: error code 8",
            "Element error at position 3: code 7",
            "Segment N4 position 14: error code 3",
            "Element error at position 2: code 1");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse999_Empty_IsARejection(string content)
    {
        var (code, errors) = EncounterSubmissionServiceImpl.Parse999Acknowledgment(content);

        code.Should().Be("R");
        errors.Should().ContainSingle().Which.Should().Contain("Empty");
    }
}
