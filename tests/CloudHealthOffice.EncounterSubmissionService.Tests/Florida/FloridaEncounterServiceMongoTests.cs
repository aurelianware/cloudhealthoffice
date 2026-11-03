using CloudHealthOffice.Testing.Mongo;
using EncounterSubmissionService.Models;
using EncounterSubmissionService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace CloudHealthOffice.EncounterSubmissionService.Tests.Florida;

/// <summary>
/// The Florida (FMMIS) encounter service against a real mongod: the pending queue,
/// deadline sweeps, the dashboard summary, 999 acknowledgments and retries, each
/// kept to its own tenant (and, for acknowledgments, to Florida records only).
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class FloridaEncounterServiceMongoTests : IAsyncLifetime
{
    private const string Tenant = "tenant-1";
    private const string Other = "tenant-2";

    private readonly MongoRunnerFixture _mongo;
    private readonly IMongoDatabase _db;
    private readonly IMongoCollection<EncounterSubmission> _submissions;
    private readonly EncounterSubmissionServiceImpl _service;
    private readonly DateTime _now = DateTime.UtcNow;

    public FloridaEncounterServiceMongoTests(MongoRunnerFixture mongo)
    {
        _mongo = mongo;
        _db = mongo.CreateDatabase("enc_florida");
        _submissions = _db.GetCollection<EncounterSubmission>("encounter_submissions");
        _service = new EncounterSubmissionServiceImpl(_db, Mock.Of<IHttpClientFactory>(),
            new ConfigurationBuilder().Build(), NullLogger<EncounterSubmissionServiceImpl>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_db);

    private async Task<EncounterSubmission> Seed(string id, EncounterSubmissionStatus status = EncounterSubmissionStatus.Pending,
        double deadlineDays = 30, string tenant = Tenant, string state = "FL", string? batchId = null, int retries = 0)
    {
        var s = new EncounterSubmission
        {
            Id = id,
            TenantId = tenant,
            ClaimId = "claim-" + id,
            StateCode = state,
            Status = status,
            SubmissionDeadline = _now.AddDays(deadlineDays),
            BatchId = batchId,
            RetryCount = retries,
            LastError = status == EncounterSubmissionStatus.Rejected ? "999 rejected" : null
        };
        await _submissions.InsertOneAsync(s);
        return s;
    }

    private Task<EncounterSubmission> Load(string id) => _submissions.Find(s => s.Id == id).FirstAsync();

    // ── Pending queue ──────────────────────────────────────────────────

    [Fact]
    public async Task PendingQueue_IsTheTenantsRetryableWork_NearestDeadlineFirst()
    {
        await Seed("late", deadlineDays: 40);
        await Seed("soon", EncounterSubmissionStatus.DeadlineWarning, deadlineDays: 3);
        await Seed("retry", EncounterSubmissionStatus.Rejected, deadlineDays: 20, retries: 2);
        await Seed("exhausted", EncounterSubmissionStatus.Rejected, deadlineDays: 10, retries: 3);
        await Seed("batched", EncounterSubmissionStatus.Batched, deadlineDays: 1);
        await Seed("accepted", EncounterSubmissionStatus.Accepted, deadlineDays: 1);
        await Seed("other", deadlineDays: 1, tenant: Other);

        var pending = (await _service.GetPendingSubmissionsAsync(Tenant)).Select(s => s.Id);

        pending.Should().Equal("soon", "retry", "late");
    }

    [Fact]
    public async Task PendingQueue_Pages()
    {
        for (var i = 1; i <= 5; i++) await Seed($"s{i}", deadlineDays: i);

        var page2 = (await _service.GetPendingSubmissionsAsync(Tenant, page: 2, pageSize: 2)).Select(s => s.Id);

        page2.Should().Equal("s3", "s4");
    }

    [Fact]
    public async Task GetById_DoesNotCrossTenants()
    {
        await Seed("mine");
        await Seed("theirs", tenant: Other);

        (await _service.GetByIdAsync("mine", Tenant)).Should().NotBeNull();
        (await _service.GetByIdAsync("theirs", Tenant)).Should().BeNull();
    }

    // ── Deadlines ──────────────────────────────────────────────────────

    [Fact]
    public async Task DeadlineWarnings_AreOpenWorkInsideTheWindow_ForThisTenant()
    {
        await Seed("in-window", deadlineDays: 5);
        await Seed("warned", EncounterSubmissionStatus.DeadlineWarning, deadlineDays: 2);
        await Seed("outside", deadlineDays: 10);
        await Seed("missed", deadlineDays: -1);
        await Seed("batched", EncounterSubmissionStatus.Batched, deadlineDays: 2);
        await Seed("other", deadlineDays: 2, tenant: Other);

        var warnings = (await _service.GetDeadlineWarningsAsync(Tenant, 7)).Select(s => s.Id);

        warnings.Should().Equal("warned", "in-window");
    }

    [Fact]
    public async Task ApproachingDeadlineSweep_CoversEveryTenant()
    {
        await Seed("mine", deadlineDays: 5);
        await Seed("theirs", deadlineDays: 1, tenant: Other);
        await Seed("outside", deadlineDays: 30);

        var approaching = (await _service.GetApproachingDeadlineAsync(7)).Select(s => s.Id);

        approaching.Should().Equal("theirs", "mine");
    }

    [Fact]
    public async Task FlaggingAWarning_OnlyMovesAPendingRecord_UnderItsOwnTenant()
    {
        var pending = await Seed("pending", deadlineDays: 3);
        var batched = await Seed("batched", EncounterSubmissionStatus.Batched, deadlineDays: 3);

        await _service.FlagDeadlineWarningAsync(pending);
        await _service.FlagDeadlineWarningAsync(batched);
        await _service.FlagDeadlineWarningAsync(new EncounterSubmission { Id = "pending", TenantId = Other });

        (await Load("pending")).Status.Should().Be(EncounterSubmissionStatus.DeadlineWarning);
        (await Load("batched")).Status.Should().Be(EncounterSubmissionStatus.Batched);
    }

    // ── Summary ────────────────────────────────────────────────────────

    [Fact]
    public async Task Summary_CountsEachStatus_ForThisTenantOnly()
    {
        await Seed("p1");
        await Seed("p2");
        await Seed("b", EncounterSubmissionStatus.Batched);
        await Seed("a", EncounterSubmissionStatus.Accepted);
        await Seed("w", EncounterSubmissionStatus.DeadlineWarning);
        await Seed("r", EncounterSubmissionStatus.Rejected);
        await Seed("v", EncounterSubmissionStatus.ValidationFailed, state: "TX");
        await Seed("c", EncounterSubmissionStatus.ClosedNotReportable, state: "CA");
        await Seed("x", tenant: Other);

        var summary = await _service.GetStatusSummaryAsync(Tenant);

        summary.TenantId.Should().Be(Tenant);
        summary.Pending.Should().Be(2);
        summary.Batched.Should().Be(1);
        summary.Accepted.Should().Be(1);
        summary.DeadlineWarning.Should().Be(1);
        summary.Rejected.Should().Be(1);
        summary.ValidationFailed.Should().Be(1);
        summary.ClosedNotReportable.Should().Be(1);
        summary.Submitted.Should().Be(0);
        summary.Total.Should().Be(8);
    }

    // ── 999 acknowledgments ────────────────────────────────────────────

    [Theory]
    [InlineData("A", EncounterSubmissionStatus.SyntaxAccepted)]
    [InlineData("E", EncounterSubmissionStatus.SyntaxAcceptedWithErrors)]
    public async Task AcceptedAck_MovesTheWholeFloridaBatch(string ak9, EncounterSubmissionStatus expected)
    {
        await Seed("1", EncounterSubmissionStatus.Submitted, batchId: "batch-1");
        await Seed("2", EncounterSubmissionStatus.Submitted, batchId: "batch-1");

        await _service.ProcessAcknowledgmentAsync("batch-1", $"ST*999*0001~AK9*{ak9}*1*1*1~", Tenant);

        foreach (var id in new[] { "1", "2" })
        {
            var s = await Load(id);
            s.Status.Should().Be(expected);
            s.AcknowledgmentCode.Should().Be(ak9);
            s.AcknowledgedAt.Should().NotBeNull();
            s.RetryCount.Should().Be(0);
        }
    }

    [Fact]
    public async Task RejectedAck_RecordsTheErrors_AndCountsARetry()
    {
        await Seed("1", EncounterSubmissionStatus.Submitted, batchId: "batch-1", retries: 1);

        await _service.ProcessAcknowledgmentAsync("batch-1", "ST*999*0001~IK3*NM1*12*2010BA*8~AK9*R*1*1*0~", Tenant);

        var s = await Load("1");
        s.Status.Should().Be(EncounterSubmissionStatus.Rejected);
        s.AcknowledgmentCode.Should().Be("R");
        s.LastError.Should().Be("Segment NM1 position 12: error code 8");
        s.RetryCount.Should().Be(2);
    }

    [Fact]
    public async Task RejectedAck_WithoutDetail_SaysWhichBatch()
    {
        await Seed("1", EncounterSubmissionStatus.Submitted, batchId: "batch-1");

        await _service.ProcessAcknowledgmentAsync("batch-1", "ST*999*0001~AK9*R*1*1*0~", Tenant);

        (await Load("1")).LastError.Should().Be("FMMIS 999 rejected batch batch-1");
    }

    [Fact]
    public async Task Ack_NeverTouchesAnotherTenant_OrATexasCaliforniaRecordInTheSameBatchId()
    {
        await Seed("fl", EncounterSubmissionStatus.Submitted, batchId: "batch-1");
        await Seed("tx", EncounterSubmissionStatus.Submitted, batchId: "batch-1", state: "TX");
        await Seed("theirs", EncounterSubmissionStatus.Submitted, batchId: "batch-1", tenant: Other);

        await _service.ProcessAcknowledgmentAsync("batch-1", "ST*999*0001~AK9*R*1*1*0~", Tenant);

        (await Load("fl")).Status.Should().Be(EncounterSubmissionStatus.Rejected);
        (await Load("tx")).Status.Should().Be(EncounterSubmissionStatus.Submitted);
        (await Load("theirs")).Status.Should().Be(EncounterSubmissionStatus.Submitted);
    }

    [Fact]
    public async Task Ack_ForAnUnknownBatch_ChangesNothing()
    {
        await Seed("1", EncounterSubmissionStatus.Submitted, batchId: "batch-1");

        await _service.ProcessAcknowledgmentAsync("batch-404", "ST*999*0001~AK9*A*1*1*1~", Tenant);

        (await Load("1")).Status.Should().Be(EncounterSubmissionStatus.Submitted);
    }

    // ── Retry ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Retry_PutsARejectedRecordBackInTheQueue()
    {
        await Seed("1", EncounterSubmissionStatus.Rejected, batchId: "batch-1", retries: 1);

        var returned = await _service.RetrySubmissionAsync("1", Tenant);

        returned.Status.Should().Be(EncounterSubmissionStatus.Pending);
        var stored = await Load("1");
        stored.Status.Should().Be(EncounterSubmissionStatus.Pending);
        stored.BatchId.Should().BeNull();
        stored.LastError.Should().BeNull();
        stored.RetryCount.Should().Be(1, "the count goes up when the state rejects it again, not on retry");
    }

    [Fact]
    public async Task Retry_RefusesNonRejected_Exhausted_AndOtherTenantsRecords()
    {
        await Seed("accepted", EncounterSubmissionStatus.Accepted);
        await Seed("exhausted", EncounterSubmissionStatus.Rejected, retries: 3);
        await Seed("theirs", EncounterSubmissionStatus.Rejected, tenant: Other);

        await _service.Invoking(s => s.RetrySubmissionAsync("accepted", Tenant)).Should().ThrowAsync<InvalidOperationException>();
        await _service.Invoking(s => s.RetrySubmissionAsync("exhausted", Tenant)).Should().ThrowAsync<InvalidOperationException>();
        await _service.Invoking(s => s.RetrySubmissionAsync("theirs", Tenant)).Should().ThrowAsync<KeyNotFoundException>();
        (await Load("theirs")).Status.Should().Be(EncounterSubmissionStatus.Rejected);
    }
}
