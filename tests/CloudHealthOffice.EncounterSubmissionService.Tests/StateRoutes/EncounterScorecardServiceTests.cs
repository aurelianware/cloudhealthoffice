using CloudHealthOffice.Testing.Mongo;
using EncounterSubmissionService.Models;
using EncounterSubmissionService.StateRoutes;
using MongoDB.Driver;
using static CloudHealthOffice.EncounterSubmissionService.Tests.StateRoutes.StateRouteTestData;

namespace CloudHealthOffice.EncounterSubmissionService.Tests.StateRoutes;

[Collection(MongoRunnerFixture.CollectionName)]
public sealed class EncounterScorecardServiceTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 12, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime October = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Deadline = new(2026, 11, 30, 23, 59, 59, DateTimeKind.Utc);

    private readonly MongoRunnerFixture _mongo;
    private readonly IMongoDatabase _db;
    private readonly IMongoCollection<EncounterSubmission> _submissions;

    public EncounterScorecardServiceTests(MongoRunnerFixture mongo)
    {
        _mongo = mongo;
        _db = mongo.CreateDatabase("enc_scorecard");
        _submissions = _db.GetCollection<EncounterSubmission>("encounter_submissions");
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_db);

    private EncounterScorecardService Service(StateEncounterRouteOptions? tx = null) =>
        new(_db, Registry(tx: tx ?? TexasOptions()), new FixedClock(Now));

    private sealed class FixedClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }

    private static EncounterResponseEvent Response(string kind, EncounterSubmissionStatus result, params string[] codes) => new()
    {
        ResponseId = Guid.NewGuid().ToString(),
        Kind = kind,
        ReceivedAt = Now.AddDays(-30),
        ResultingStatus = result,
        StatusCodes = codes.ToList()
    };

    private static EncounterSubmission Record(string id, EncounterSubmissionStatus status, DateTime? submitted,
        string tenant = "tenant-1", string state = "TX", DateTime? adjudicated = null, DateTime? updated = null,
        params EncounterResponseEvent[] responses) => new()
    {
        Id = $"{tenant}:{state}:{id}",
        TenantId = tenant,
        StateCode = state,
        ClaimId = id,
        ClaimAdjudicatedAt = adjudicated ?? October.AddDays(14),
        SubmissionDeadline = Deadline,
        SubmittedAt = submitted,
        Status = status,
        ResponseHistory = responses.ToList(),
        UpdatedAt = updated ?? Now.AddDays(-2)
    };

    private async Task SeedAsync()
    {
        var onTime = Deadline.AddDays(-20);
        var late = Deadline.AddDays(3);
        await _submissions.InsertManyAsync(new[]
        {
            // first-pass accepted, on time, one TMHP R3 warning
            Record("a1", EncounterSubmissionStatus.StateFrontEndAccepted, onTime, responses: new[]
            {
                Response("999", EncounterSubmissionStatus.SyntaxAccepted, "IK5:A"),
                Response("277CA", EncounterSubmissionStatus.StateFrontEndAccepted, "A2:PR001", "R3:Bx000095W")
            }),
            Record("a2", EncounterSubmissionStatus.StateFrontEndAccepted, onTime,
                responses: Response("277CA", EncounterSubmissionStatus.StateFrontEndAccepted, "A2:PR001")),
            // accepted on the second send, which went late
            Record("a3", EncounterSubmissionStatus.StateFrontEndAccepted, late, responses: new[]
            {
                Response("277CA", EncounterSubmissionStatus.Rejected, "A3:Bx0000011"),
                Response("277CA", EncounterSubmissionStatus.StateFrontEndAccepted, "A2:PR001")
            }),
            // state rejection, open 20 days
            Record("r1", EncounterSubmissionStatus.Rejected, onTime, updated: Now.AddDays(-20),
                responses: Response("277CA", EncounterSubmissionStatus.Rejected, "A3:Bx0000011")),
            // syntax rejection, open 40 days
            Record("r2", EncounterSubmissionStatus.Rejected, onTime, updated: Now.AddDays(-40),
                responses: Response("999", EncounterSubmissionStatus.Rejected, "IK5:R")),
            // held back by validation, never sent, past the deadline
            Record("v1", EncounterSubmissionStatus.ValidationFailed, null, updated: Now.AddDays(-5)),
            // sent, awaiting the 277CA
            Record("s1", EncounterSubmissionStatus.SyntaxAccepted, onTime),
            // closed, not counted
            Record("c1", EncounterSubmissionStatus.ClosedNotReportable, null),
            // outside the period, another tenant, another state
            Record("x1", EncounterSubmissionStatus.Rejected, onTime, adjudicated: October.AddMonths(1)),
            Record("x2", EncounterSubmissionStatus.Rejected, onTime, tenant: "tenant-2"),
            Record("x3", EncounterSubmissionStatus.Rejected, onTime, state: "CA")
        });
    }

    [Fact]
    public async Task Scorecard_MeasuresTimelinessAcceptanceAndAging_ForOneTenantStateAndMonth()
    {
        await SeedAsync();

        var card = await Service().GetAsync(StateEncounterRoute.Texas, "tenant-1", October, October.AddMonths(1));

        card.Total.Should().Be(8);
        card.ClosedNotReportable.Should().Be(1);
        card.Reportable.Should().Be(7);

        card.Timeliness.OnTime.Should().Be(5);
        card.Timeliness.Late.Should().Be(1);
        card.Timeliness.Overdue.Should().Be(1, "v1 was never sent and its deadline passed");
        card.Timeliness.NotYetDue.Should().Be(0);
        card.Timeliness.Rate.Should().Be(71.43m);

        card.Accuracy.Accepted.Should().Be(3);
        card.Accuracy.Rejected.Should().Be(2);
        card.Accuracy.AwaitingResponse.Should().Be(1);
        card.Accuracy.HeldByValidation.Should().Be(1);
        card.Accuracy.AcceptanceRate.Should().Be(60m);
        card.Accuracy.FirstPassAcceptanceRate.Should().Be(40m, "a3 was rejected before it was accepted");
        card.Accuracy.SyntaxRejected.Should().Be(1);
        card.Accuracy.StateRejected.Should().Be(1);
        card.Accuracy.TopRejectionCodes.Should().Equal(
            new EncounterCodeCount("A3:Bx0000011", 2), new EncounterCodeCount("IK5:R", 1));
        card.Accuracy.TopWarningCodes.Should().Equal(new EncounterCodeCount("R3:Bx000095W", 1));

        card.OpenRework.Days0To15.Should().Be(1);
        card.OpenRework.Days16To30.Should().Be(1);
        card.OpenRework.Over30Days.Should().Be(1);
        card.OpenRework.PastCorrectionWindow.Should().BeNull("no correction window is configured");

        card.Targets.Timeliness.Should().BeNull("no state threshold is defaulted in code");
        card.Targets.MeetsTimeliness.Should().BeNull();
    }

    [Fact]
    public async Task ConfiguredTargets_AreCompared_AndShownWithTheirSource()
    {
        await SeedAsync();
        var options = TexasOptions();
        options.TimelinessTarget = 70m;
        options.AcceptanceTarget = 95m;
        options.CorrectionDays = 15;
        options.ScorecardTargetSource = "Plan contract Attachment X (synthetic test value)";

        var card = await Service(options).GetAsync(StateEncounterRoute.Texas, "tenant-1", October, October.AddMonths(1));

        card.Targets.MeetsTimeliness.Should().BeTrue();
        card.Targets.MeetsAcceptance.Should().BeFalse();
        card.Targets.Source.Should().Contain("synthetic");
        card.OpenRework.PastCorrectionWindow.Should().Be(2, "r1 (20 days) and r2 (40 days) are past 15 days");
    }

    [Fact]
    public async Task EmptyPeriod_HasNoRates()
    {
        var card = await Service().GetAsync(StateEncounterRoute.Texas, "tenant-1", October, October.AddMonths(1));

        card.Total.Should().Be(0);
        card.Timeliness.Rate.Should().BeNull();
        card.Accuracy.AcceptanceRate.Should().BeNull();
    }

    [Fact]
    public async Task BadPeriodsAndFlorida_AreRefused()
    {
        var service = Service();

        await service.Invoking(s => s.GetAsync(StateEncounterRoute.Texas, "tenant-1", October, October))
            .Should().ThrowAsync<ArgumentException>();
        await service.Invoking(s => s.GetAsync(StateEncounterRoute.Texas, "tenant-1", October, October.AddYears(2)))
            .Should().ThrowAsync<ArgumentException>();
        await service.Invoking(s => s.GetAsync(StateEncounterRoute.Florida, "tenant-1", October, October.AddMonths(1)))
            .Should().ThrowAsync<ArgumentException>();
    }
}
