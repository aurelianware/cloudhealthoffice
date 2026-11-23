using System.Net;
using System.Net.Http.Json;
using CloudHealthOffice.Events;
using CloudHealthOffice.EncounterSubmissionService.Tests.StateRoutes;
using CloudHealthOffice.Testing.Mongo;
using EncounterSubmissionService.Models;
using EncounterSubmissionService.StateRoutes;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using static CloudHealthOffice.EncounterSubmissionService.Tests.StateRoutes.StateRouteTestData;

namespace CloudHealthOffice.EncounterSubmissionService.Tests.Texas;

/// <summary>
/// One Texas encounter cycle against TMHP-shaped files, with synthetic data: claims
/// finalize, a batch is written and handed off, TMHP's 999 and 277CA come back
/// (one accepted, one accepted with an R3 warning, one rejected on a Bx edit), the
/// scorecard reflects it, a claim adjustment waits for the ICN, the ICN is loaded,
/// and the replacement and the corrected rejection go in the next batch.
/// This is what CHO does on its side of a TMHP test cycle. It is not evidence that
/// TMHP accepts the files.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class TexasEncounterEndToEndTests : IAsyncLifetime
{
    private const string Tenant = "tenant-1";
    private readonly MongoRunnerFixture _mongo;
    private readonly IMongoDatabase _db;
    private readonly IMongoCollection<EncounterSubmission> _submissions;

    public TexasEncounterEndToEndTests(MongoRunnerFixture mongo)
    {
        _mongo = mongo;
        _db = mongo.CreateDatabase("enc_tx_e2e");
        _submissions = _db.GetCollection<EncounterSubmission>("encounter_submissions");
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_db);

    private static ClaimFinalizedEvent Finalized(string claimId, string? originalClaimId = null, string? frequency = null) => new()
    {
        TenantId = Tenant,
        ClaimId = claimId,
        FinalStatus = "Paid",
        OriginalClaimId = originalClaimId,
        ClaimFrequencyCode = frequency,
        AdjudicationTimestamp = new DateTimeOffset(2026, 10, 20, 15, 0, 0, TimeSpan.Zero)
    };

    private async Task<List<EncounterSubmission>> Pending() =>
        await _submissions.Find(s => s.TenantId == Tenant && s.Status == EncounterSubmissionStatus.Pending).ToListAsync();

    private Task<EncounterSubmission> Get(string claim) =>
        _submissions.Find(s => s.Id == $"{Tenant}:TX:{claim}").FirstAsync();

    [Fact]
    public async Task TexasCycle_SendRespondScoreCorrectAndResend()
    {
        var registry = Registry(tx: TexasOptions());
        var handler = new StateEncounterBatchServiceTests.Handler(StateEncounterBatchServiceTests.Respond());
        var factory = new Mock<IHttpClientFactory>();
        foreach (var name in new[] { "ClaimsService", "MemberService", "ProviderService" })
            factory.Setup(f => f.CreateClient(name)).Returns(new HttpClient(handler) { BaseAddress = new Uri("http://svc") });
        var batches = new StateEncounterBatchService(_db, factory.Object, registry,
            new StateEncounterBatchServiceTests.FixedControlNumbers(), NullLogger<StateEncounterBatchService>.Instance);
        var lifecycle = new EncounterLifecycleService(_db, registry, batches, factory.Object,
            NullLogger<EncounterLifecycleService>.Instance);
        var reconciler = new StateResponseReconciler(_db, NullLogger<StateResponseReconciler>.Instance);
        var rework = new EncounterReworkService(_db, NullLogger<EncounterReworkService>.Instance);
        var scorecards = new EncounterScorecardService(_db, registry);

        // ── 1. Three paid claims become Texas encounters, batched and handed off ──
        foreach (var c in new[] { "c1", "c2", "c3" })
            await lifecycle.HandleFinalizedAsync(Finalized(c));
        var first = await batches.BuildBatchAsync(StateEncounterRoute.Texas, await Pending(), Tenant);
        var file = first.Files.Should().ContainSingle().Subject;
        file.FileName.Should().EndWith(".dat");
        TmhpFiles.Check837(file.Content, Encounter837Writer.ProfessionalVersion).Should().BeEmpty();
        await batches.MarkTransmittedAsync(Tenant, file.Id);

        var sent = await Get("c1");
        var gs06 = sent.GroupControlNumber!;

        // ── 2. TMHP's 999 accepts all three, then its 277CA answers each ──
        var ack999 = await reconciler.ApplyAsync(StateEncounterRoute.Texas, Tenant, "SYNTHTPID.D0000001.999",
            TmhpFiles.Ack999("TPID12345", gs06, ("0001", "A"), ("0002", "A"), ("0003", "A")));
        ack999.Accepted.Should().Be(3);

        var ack277 = await reconciler.ApplyAsync(StateEncounterRoute.Texas, Tenant, "SYNTHTPID.D0000001.277CA",
            TmhpFiles.Ack277Ca("TPID12345", "CLM-c1",
                ("CLM-c1", new[] { "A2:PR001" }, 250m),
                ("CLM-c2", new[] { "A2:PR001", "R3:Bx000095W" }, 250m),
                ("CLM-c3", new[] { "A3:Bx0000011" }, 250m)));
        ack277.Accepted.Should().Be(2);
        ack277.Rejected.Should().Be(1);
        (await Get("c2")).Status.Should().Be(EncounterSubmissionStatus.StateFrontEndAccepted, "R3 is a TMHP warning");
        (await Get("c3")).Status.Should().Be(EncounterSubmissionStatus.Rejected);

        // ── 3. The scorecard shows it ──
        var card = await scorecards.GetAsync(StateEncounterRoute.Texas, Tenant,
            new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));
        card.Accuracy.Accepted.Should().Be(2);
        card.Accuracy.StateRejected.Should().Be(1);
        card.Accuracy.TopRejectionCodes.Should().ContainSingle().Which.Code.Should().Be("A3:Bx0000011");
        card.Accuracy.TopWarningCodes.Should().ContainSingle().Which.Code.Should().Be("R3:Bx000095W");
        card.Timeliness.OnTime.Should().Be(3);

        // ── 4. c1 is adjusted: the replacement waits, because TMHP sent no ICN ──
        await lifecycle.HandleFinalizedAsync(Finalized("c1-adj", originalClaimId: "c1", frequency: "7"));
        var waiting = await batches.BuildBatchAsync(StateEncounterRoute.Texas, await Pending(), Tenant);
        waiting.WaitingOnOriginal.Should().Equal($"{Tenant}:TX:c1-adj");
        waiting.Files.Should().BeEmpty();

        // ── 5. The ICN comes in from the plan's encounter report; c3 is fixed and requeued ──
        var refs = await reconciler.ApplyReferencesAsync(StateEncounterRoute.Texas, Tenant, "synthetic-icn-report.csv",
            new[] { new StateClaimReferenceRow { PatientControlNumber = "CLM-c1", StateClaimReference = "SYNICN000000101" } });
        refs.Matched.Should().Be(1);
        var queue = await rework.ListAsync(Tenant, "TX", "STATE_REJECTED", 1, 50);
        queue.Items.Should().ContainSingle().Which.Reasons.Should().Contain(r => r.Contains("Bx0000011"));
        (await rework.RequeueAsync(Tenant, $"{Tenant}:TX:c3", "analyst-7", "fixed the original ICN")).Outcome
            .Should().Be(ReworkOutcome.Done);

        // ── 6. The next batch carries the replacement with REF*F8 and c3 as a new original ──
        var second = await batches.BuildBatchAsync(StateEncounterRoute.Texas, await Pending(), Tenant);
        second.BatchedSubmissionIds.Should().BeEquivalentTo(new[] { $"{Tenant}:TX:c1-adj", $"{Tenant}:TX:c3" });
        var resend = second.Files.Should().ContainSingle().Subject.Content;
        resend.Should().Contain("CLM*CLM-c1-adj*250***11:B:7*").And.Contain("REF*F8*SYNICN000000101~");
        resend.Should().Contain("CLM*CLM-c3*250***11:B:1*");
        TmhpFiles.Check837(resend, Encounter837Writer.ProfessionalVersion).Should().BeEmpty();
    }
}
