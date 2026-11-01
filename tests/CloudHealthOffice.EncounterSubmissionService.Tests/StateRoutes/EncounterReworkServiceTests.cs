using CloudHealthOffice.Testing.Mongo;
using EncounterSubmissionService.Models;
using EncounterSubmissionService.StateRoutes;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace CloudHealthOffice.EncounterSubmissionService.Tests.StateRoutes;

[Collection(MongoRunnerFixture.CollectionName)]
public sealed class EncounterReworkServiceTests : IAsyncLifetime
{
    private readonly MongoRunnerFixture _mongo;
    private readonly IMongoDatabase _db;
    private readonly IMongoCollection<EncounterSubmission> _submissions;
    private readonly EncounterReworkService _rework;
    private readonly DateTime _now = DateTime.UtcNow;

    public EncounterReworkServiceTests(MongoRunnerFixture mongo)
    {
        _mongo = mongo;
        _db = mongo.CreateDatabase("enc_rework");
        _submissions = _db.GetCollection<EncounterSubmission>("encounter_submissions");
        _rework = new EncounterReworkService(_db, NullLogger<EncounterReworkService>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_db);

    private async Task<EncounterSubmission> Seed(string id, EncounterSubmissionStatus status, int deadlineDays,
        string state = "TX", string tenant = "tenant-1", string? lastResponseKind = null)
    {
        var s = new EncounterSubmission
        {
            Id = $"{tenant}:{state}:{id}",
            TenantId = tenant,
            ClaimId = id,
            StateCode = state,
            Status = status,
            SubmissionDeadline = _now.AddDays(deadlineDays),
            BatchId = "batch-1",
            FileName = "TMHP_4A_837P_TEST_x.x12",
            InterchangeControlNumber = "000000100",
            GroupControlNumber = "100",
            TransactionSetControlNumber = "0001",
            PatientControlNumber = "CLM-" + id,
            LastError = "something failed",
            UpdatedAt = _now
        };
        if (status == EncounterSubmissionStatus.ValidationFailed)
            s.ValidationErrors = new List<string> { "member address (2010BA N3/N4) is incomplete" };
        if (lastResponseKind is not null)
            s.ResponseHistory.Add(new EncounterResponseEvent
            {
                Kind = lastResponseKind,
                ResultingStatus = status,
                Errors = lastResponseKind == "999" ? new List<string> { "segment N4 at position 12: error 8" } : new List<string>()
            });
        await _submissions.InsertOneAsync(s);
        return s;
    }

    private Task<EncounterSubmission> Get(string id) => _submissions.Find(s => s.Id == id).FirstAsync();

    [Fact]
    public async Task Queue_ListsHeldBackAndRejected_NearestDeadlineFirst_WithReasons()
    {
        await Seed("late", EncounterSubmissionStatus.ValidationFailed, 20);
        await Seed("soon", EncounterSubmissionStatus.Rejected, 3, lastResponseKind: "999");
        await Seed("state", EncounterSubmissionStatus.Rejected, 10, state: "CA", lastResponseKind: "277");
        await Seed("fine", EncounterSubmissionStatus.StateFrontEndAccepted, 1);
        await Seed("fl", EncounterSubmissionStatus.Rejected, 1, state: "FL");
        await Seed("other", EncounterSubmissionStatus.ValidationFailed, 1, tenant: "tenant-2");

        var page = await _rework.ListAsync("tenant-1", null, null, 1, 50);

        page.TotalCount.Should().Be(3);
        page.Items.Select(i => i.ClaimId).Should().Equal("soon", "state", "late");
        page.Items[0].ReasonCode.Should().Be(EncounterReworkReasons.SyntaxRejected);
        page.Items[0].Reasons.Should().Contain(r => r.Contains("segment N4"));
        page.Items[1].ReasonCode.Should().Be(EncounterReworkReasons.StateRejected);
        page.Items[2].ReasonCode.Should().Be(EncounterReworkReasons.Validation);
        page.Items[2].Reasons.Should().Contain(r => r.Contains("member address"));
        page.Items[0].DaysToDeadline.Should().BeApproximately(3, 0.2);
    }

    [Fact]
    public async Task Queue_FiltersByStateAndReason()
    {
        await Seed("a", EncounterSubmissionStatus.ValidationFailed, 5);
        await Seed("b", EncounterSubmissionStatus.Rejected, 5, lastResponseKind: "999");
        await Seed("c", EncounterSubmissionStatus.Rejected, 5, state: "CA", lastResponseKind: "277");

        (await _rework.ListAsync("tenant-1", "CA", null, 1, 50)).Items.Select(i => i.ClaimId).Should().Equal("c");
        (await _rework.ListAsync("tenant-1", null, EncounterReworkReasons.SyntaxRejected, 1, 50))
            .Items.Select(i => i.ClaimId).Should().Equal("b");
        (await _rework.ListAsync("tenant-1", "tx", EncounterReworkReasons.Validation, 1, 50))
            .Items.Select(i => i.ClaimId).Should().Equal("a");
    }

    [Fact]
    public async Task Requeue_ReturnsToPending_ClearsTheOldSend_AndRecordsWhoAndWhy()
    {
        await Seed("a", EncounterSubmissionStatus.Rejected, 5, lastResponseKind: "277");

        var (outcome, updated) = await _rework.RequeueAsync("tenant-1", "tenant-1:TX:a", "user-7", "fixed billing ZIP+4");

        outcome.Should().Be(ReworkOutcome.Done);
        var a = await Get("tenant-1:TX:a");
        a.Status.Should().Be(EncounterSubmissionStatus.Pending);
        a.RetryCount.Should().Be(1);
        a.BatchId.Should().BeNull();
        a.FileName.Should().BeNull();
        a.TransactionSetControlNumber.Should().BeNull();
        a.LastError.Should().BeNull();
        a.PatientControlNumber.Should().Be("CLM-a", "CLM01 stays so a late response still matches the claim");
        a.ResponseHistory.Should().HaveCount(1, "response history is kept");
        a.ReworkHistory.Should().ContainSingle().Which.Should().Match<EncounterReworkEvent>(e =>
            e.Action == "Requeued" && e.ActorId == "user-7" && e.Note == "fixed billing ZIP+4"
            && e.ReasonCode == EncounterReworkReasons.StateRejected);
        updated!.Status.Should().Be(EncounterSubmissionStatus.Pending);

        (await _rework.ListAsync("tenant-1", null, null, 1, 50)).TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task Close_NeedsAReason_AndLeavesTheQueue()
    {
        await Seed("a", EncounterSubmissionStatus.ValidationFailed, 5);

        (await _rework.CloseAsync("tenant-1", "tenant-1:TX:a", "user-7", "  ")).Outcome
            .Should().Be(ReworkOutcome.ReasonRequired);

        var (outcome, _) = await _rework.CloseAsync("tenant-1", "tenant-1:TX:a", "user-7", "duplicate of claim B");
        outcome.Should().Be(ReworkOutcome.Done);
        var a = await Get("tenant-1:TX:a");
        a.Status.Should().Be(EncounterSubmissionStatus.ClosedNotReportable);
        a.ReworkHistory.Single().Note.Should().Be("duplicate of claim B");
        (await _rework.ListAsync("tenant-1", null, null, 1, 50)).TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task Actions_RefuseRecordsOutsideTheQueue_OtherTenants_AndFlorida()
    {
        await Seed("ok", EncounterSubmissionStatus.StateFrontEndAccepted, 5);
        await Seed("fl", EncounterSubmissionStatus.Rejected, 5, state: "FL");
        await Seed("other", EncounterSubmissionStatus.Rejected, 5, tenant: "tenant-2");

        (await _rework.RequeueAsync("tenant-1", "tenant-1:TX:ok", "u", null)).Outcome.Should().Be(ReworkOutcome.NotInRework);
        (await _rework.RequeueAsync("tenant-1", "tenant-1:FL:fl", "u", null)).Outcome.Should().Be(ReworkOutcome.NotInRework);
        (await _rework.RequeueAsync("tenant-1", "tenant-2:TX:other", "u", null)).Outcome.Should().Be(ReworkOutcome.NotFound);
        (await _rework.CloseAsync("tenant-1", "tenant-2:TX:other", "u", "x")).Outcome.Should().Be(ReworkOutcome.NotFound);

        (await Get("tenant-2:TX:other")).Status.Should().Be(EncounterSubmissionStatus.Rejected);
    }
}
