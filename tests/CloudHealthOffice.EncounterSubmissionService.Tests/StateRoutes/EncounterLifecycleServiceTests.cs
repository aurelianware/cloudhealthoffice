using System.Net;
using System.Net.Http.Json;
using CloudHealthOffice.Events;
using CloudHealthOffice.Testing.Mongo;
using EncounterSubmissionService.Models;
using EncounterSubmissionService.StateRoutes;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using static CloudHealthOffice.EncounterSubmissionService.Tests.StateRoutes.StateRouteTestData;

namespace CloudHealthOffice.EncounterSubmissionService.Tests.StateRoutes;

[Collection(MongoRunnerFixture.CollectionName)]
public sealed class EncounterLifecycleServiceTests : IAsyncLifetime
{
    private readonly MongoRunnerFixture _mongo;
    private readonly IMongoDatabase _db;
    private readonly IMongoCollection<EncounterSubmission> _submissions;
    private string _lineOfBusiness = "Medicaid";

    public EncounterLifecycleServiceTests(MongoRunnerFixture mongo)
    {
        _mongo = mongo;
        _db = mongo.CreateDatabase("enc_lifecycle");
        _submissions = _db.GetCollection<EncounterSubmission>("encounter_submissions");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_db);

    private (EncounterLifecycleService Lifecycle, StateEncounterBatchService Batches) Create(
        IStateEncounterRouteRegistry? registry = null)
    {
        registry ??= Registry(tx: TexasOptions());
        var inner = StateEncounterBatchServiceTests.Respond();
        var handler = new StateEncounterBatchServiceTests.Handler(r =>
        {
            var path = r.RequestUri!.AbsolutePath;
            if (path.StartsWith("/api/claims/"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(StateEncounterBatchServiceTests.ClaimJson(
                        path.Split('/')[^1], lineOfBusiness: _lineOfBusiness))
                };
            return inner(r);
        });
        var factory = new Mock<IHttpClientFactory>();
        foreach (var name in new[] { "ClaimsService", "MemberService", "ProviderService" })
            factory.Setup(f => f.CreateClient(name)).Returns(new HttpClient(handler) { BaseAddress = new Uri("http://svc") });

        var batches = new StateEncounterBatchService(_db, factory.Object, registry,
            new StateEncounterBatchServiceTests.FixedControlNumbers(), NullLogger<StateEncounterBatchService>.Instance);
        var lifecycle = new EncounterLifecycleService(_db, registry, batches, factory.Object,
            NullLogger<EncounterLifecycleService>.Instance);
        return (lifecycle, batches);
    }

    private static ClaimFinalizedEvent Finalized(string claimId, string status = "Paid",
        string? originalClaimId = null, string? frequency = null) => new()
    {
        TenantId = "tenant-1",
        ClaimId = claimId,
        FinalStatus = status,
        OriginalClaimId = originalClaimId,
        ClaimFrequencyCode = frequency,
        AdjudicationTimestamp = new DateTimeOffset(2026, 10, 20, 15, 0, 0, TimeSpan.Zero)
    };

    private Task<EncounterSubmission> Get(string id) => _submissions.Find(s => s.Id == id).FirstAsync();

    private Task SetStatus(string id, EncounterSubmissionStatus status, string? stateReference = null) =>
        _submissions.UpdateOneAsync(s => s.Id == id, Builders<EncounterSubmission>.Update
            .Set(s => s.Status, status)
            .Set(s => s.StateClaimReference, stateReference));

    private async Task<List<EncounterSubmission>> Pending() =>
        await _submissions.Find(s => s.Status == EncounterSubmissionStatus.Pending).ToListAsync();

    [Fact]
    public async Task PaidMedicaidClaim_CreatesAnOriginal_OnceEvenIfRedelivered()
    {
        var (lifecycle, _) = Create();

        var first = await lifecycle.HandleFinalizedAsync(Finalized("c1"));
        var again = await lifecycle.HandleFinalizedAsync(Finalized("c1"));

        first.Action.Should().Be(EncounterLifecycleAction.CreatedOriginal);
        first.SubmissionId.Should().Be("tenant-1:TX:c1");
        again.SubmissionId.Should().Be("tenant-1:TX:c1");
        var record = await Get("tenant-1:TX:c1");
        record.Status.Should().Be(EncounterSubmissionStatus.Pending);
        record.Receiver.Should().Be("TMHP");
        record.FrequencyCode.Should().Be("1");
        (await _submissions.CountDocumentsAsync(FilterDefinition<EncounterSubmission>.Empty)).Should().Be(1);
    }

    [Fact]
    public async Task ReplacementOfAnAcceptedEncounter_SendsFrequency7_WithTheStateReference()
    {
        var (lifecycle, batches) = Create();
        await lifecycle.HandleFinalizedAsync(Finalized("c1"));
        await SetStatus("tenant-1:TX:c1", EncounterSubmissionStatus.Accepted, "TXICN0001");

        var result = await lifecycle.HandleFinalizedAsync(Finalized("c1-adj", originalClaimId: "c1", frequency: "7"));

        result.Action.Should().Be(EncounterLifecycleAction.CreatedReplacement);
        var replacement = await Get("tenant-1:TX:c1-adj");
        replacement.FrequencyCode.Should().Be("7");
        replacement.OriginalSubmissionId.Should().Be("tenant-1:TX:c1");
        (await Get("tenant-1:TX:c1")).SupersededBySubmissionId.Should().Be("tenant-1:TX:c1-adj");

        var batch = await batches.BuildBatchAsync(StateEncounterRoute.Texas, await Pending(), "tenant-1");

        batch.BatchedSubmissionIds.Should().Equal("tenant-1:TX:c1-adj");
        var file = await batches.GetFileAsync("tenant-1", batch.Files.Single().Id);
        file!.Content.Should().Contain("CLM*CLM-c1-adj*250***11:B:7*").And.Contain("REF*F8*TXICN0001~");
        (await Get("tenant-1:TX:c1-adj")).Status.Should().Be(EncounterSubmissionStatus.Batched);
    }

    [Fact]
    public async Task ReplacementBeforeTheOriginalWasSent_ClosesTheOriginal_AndSendsANewOriginal()
    {
        var (lifecycle, _) = Create();
        await lifecycle.HandleFinalizedAsync(Finalized("c1"));

        var result = await lifecycle.HandleFinalizedAsync(Finalized("c1-adj", originalClaimId: "c1", frequency: "7"));

        result.Action.Should().Be(EncounterLifecycleAction.CreatedOriginal);
        var original = await Get("tenant-1:TX:c1");
        original.Status.Should().Be(EncounterSubmissionStatus.ClosedNotReportable);
        original.ReworkHistory.Should().ContainSingle().Which.ReasonCode.Should().Be("CLAIM_ADJUSTED");
        (await Get("tenant-1:TX:c1-adj")).FrequencyCode.Should().Be("1");
    }

    [Fact]
    public async Task ReplacementOfARejectedOriginal_GoesAsANewOriginal()
    {
        var (lifecycle, _) = Create();
        await lifecycle.HandleFinalizedAsync(Finalized("c1"));
        await SetStatus("tenant-1:TX:c1", EncounterSubmissionStatus.Rejected);

        var result = await lifecycle.HandleFinalizedAsync(Finalized("c1-adj", originalClaimId: "c1"));

        result.Action.Should().Be(EncounterLifecycleAction.CreatedOriginal);
        (await Get("tenant-1:TX:c1")).Status.Should().Be(EncounterSubmissionStatus.ClosedNotReportable);
        (await Get("tenant-1:TX:c1-adj")).OriginalSubmissionId.Should().BeNull();
    }

    [Fact]
    public async Task ReplacementWhileTheOriginalIsInFlight_WaitsForTheStateReference()
    {
        var (lifecycle, batches) = Create();
        await lifecycle.HandleFinalizedAsync(Finalized("c1"));
        await SetStatus("tenant-1:TX:c1", EncounterSubmissionStatus.Submitted);
        await lifecycle.HandleFinalizedAsync(Finalized("c1-adj", originalClaimId: "c1"));

        var waiting = await batches.BuildBatchAsync(StateEncounterRoute.Texas, await Pending(), "tenant-1");

        waiting.WaitingOnOriginal.Should().Equal("tenant-1:TX:c1-adj");
        waiting.Files.Should().BeEmpty();
        (await Get("tenant-1:TX:c1-adj")).Status.Should().Be(EncounterSubmissionStatus.Pending);

        // The state rejects the original: the replacement goes as a new original.
        await SetStatus("tenant-1:TX:c1", EncounterSubmissionStatus.Rejected);
        var sent = await batches.BuildBatchAsync(StateEncounterRoute.Texas, await Pending(), "tenant-1");

        sent.BatchedSubmissionIds.Should().Equal("tenant-1:TX:c1-adj");
        var file = await batches.GetFileAsync("tenant-1", sent.Files.Single().Id);
        file!.Content.Should().Contain("11:B:1*").And.NotContain("REF*F8");
        (await Get("tenant-1:TX:c1-adj")).FrequencyCode.Should().Be("1");
    }

    [Fact]
    public async Task ReversalOfAnAcceptedEncounter_SendsAVoid()
    {
        var (lifecycle, batches) = Create();
        await lifecycle.HandleFinalizedAsync(Finalized("c1"));
        await SetStatus("tenant-1:TX:c1", EncounterSubmissionStatus.StateFrontEndAccepted, "TXICN0001");

        var result = await lifecycle.HandleFinalizedAsync(Finalized("c1", status: "Reversed"));

        result.Action.Should().Be(EncounterLifecycleAction.CreatedVoid);
        result.SubmissionId.Should().Be("tenant-1:TX:c1:void");
        var batch = await batches.BuildBatchAsync(StateEncounterRoute.Texas, await Pending(), "tenant-1");
        var file = await batches.GetFileAsync("tenant-1", batch.Files.Single().Id);
        file!.Content.Should().Contain("11:B:8*").And.Contain("REF*F8*TXICN0001~");

        // A second reversal event is a no-op on the same void record.
        (await lifecycle.HandleFinalizedAsync(Finalized("c1", status: "Reversed"))).SubmissionId
            .Should().Be("tenant-1:TX:c1:void");
    }

    [Fact]
    public async Task ReversalBeforeSubmission_ClosesTheEncounter()
    {
        var (lifecycle, _) = Create();
        await lifecycle.HandleFinalizedAsync(Finalized("c1"));

        var result = await lifecycle.HandleFinalizedAsync(Finalized("c1", status: "Reversed"));

        result.Action.Should().Be(EncounterLifecycleAction.ClosedBeforeSubmission);
        (await Get("tenant-1:TX:c1")).Status.Should().Be(EncounterSubmissionStatus.ClosedNotReportable);
        (await _submissions.CountDocumentsAsync(FilterDefinition<EncounterSubmission>.Empty)).Should().Be(1);
    }

    [Fact]
    public async Task DeniedClaims_AreReportedOnlyWhenTheRouteAsks()
    {
        var (off, _) = Create();
        (await off.HandleFinalizedAsync(Finalized("c1", status: "Denied"))).Action
            .Should().Be(EncounterLifecycleAction.Ignored);

        var tx = TexasOptions();
        tx.ReportDeniedClaims = true;
        var (on, _) = Create(Registry(tx: tx));
        (await on.HandleFinalizedAsync(Finalized("c1", status: "Denied"))).Action
            .Should().Be(EncounterLifecycleAction.CreatedOriginal);
    }

    [Fact]
    public async Task NonMedicaid_Unenrolled_AndMultiStateTenants_AreIgnored()
    {
        _lineOfBusiness = "Commercial";
        var (lifecycle, _) = Create();
        (await lifecycle.HandleFinalizedAsync(Finalized("c1"))).Action.Should().Be(EncounterLifecycleAction.Ignored);

        _lineOfBusiness = "Medicaid";
        var (unenrolled, _) = Create(Registry(tx: TexasOptions(withTenant: false)));
        (await unenrolled.HandleFinalizedAsync(Finalized("c1"))).Reason.Should().Contain("not on a state X12");

        var (twoStates, _) = Create(Registry(tx: TexasOptions(), ca: CaliforniaOptions()));
        (await twoStates.HandleFinalizedAsync(Finalized("c1"))).Reason.Should().Contain("more than one state route");

        (await _submissions.CountDocumentsAsync(FilterDefinition<EncounterSubmission>.Empty)).Should().Be(0);
    }
}
