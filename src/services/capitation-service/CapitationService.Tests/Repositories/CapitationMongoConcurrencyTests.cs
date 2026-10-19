using CapitationService.Models;
using CapitationService.Repositories;
using CapitationService.Services;
using CapitationService.Tests.Support;
using CloudHealthOffice.Infrastructure.Data;
using CloudHealthOffice.NachaTransmission;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace CapitationService.Tests.Repositories;

/// <summary>
/// Optimistic concurrency on the MongoDB capitation run, disbursement and
/// statement repositories, against a real mongod. <c>UpdateAsync</c> replaced
/// the whole document unconditionally, so of two concurrent updates the later
/// silently erased the earlier, and a replace from a stale read could undo a
/// conditional payment transition (Pending to Releasing, Approved to
/// PaymentInitiated) made in between. Every write now bumps <c>Revision</c>,
/// and a replace is conditional on the revision it read.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class CapitationMongoConcurrencyTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "tenant-cap-concurrency";
    private IMongoDatabase _db = null!;

    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("cap_concurrency");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    /// <summary>One request's context (not the AsyncLocal-backed HttpContextAccessor: these tests run requests at once).</summary>
    private sealed class RequestContext : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private static IHttpContextAccessor Request()
    {
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = Tenant;
        return new RequestContext { HttpContext = http };
    }

    private CapitationRunRepositoryMongo Runs() => new(_db, Request(), Mock.Of<ILogger<CapitationRunRepositoryMongo>>());
    private CapitationStatementRepositoryMongo Statements() => new(_db, Request(), Mock.Of<ILogger<CapitationStatementRepositoryMongo>>());
    private CapitationDisbursementRepositoryMongo Disbursements() => new(_db, Request());

    private Task<CapitationRun> SeedRunAsync(string id) => Runs().CreateAsync(new CapitationRun
    {
        Id = id, RunNumber = "CAPRUN-" + id, Status = CapitationRunStatus.Pending, CreatedBy = "maker-1",
        CapitationPeriod = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        Criteria = new CapitationRunCriteria { LineOfBusiness = LineOfBusiness.Medicare },
    });

    private Task<CapitationStatement> SeedStatementAsync(string id, CapitationStatementStatus status) => Statements().CreateAsync(new CapitationStatement
    {
        Id = id, StatementNumber = "CAPSTMT-" + id, CapitationRunId = "run-1", ProviderNPI = "1234567890",
        ProviderName = "Dr. Smith", Status = status, NetPayable = 500m, GrossCapitation = 500m, CreatedBy = "maker-1", RunCreatedBy = "maker-1",
    });

    private Task<CapitationDisbursement> SeedDisbursementAsync(string id) => Disbursements().CreateAsync(new CapitationDisbursement
    {
        Id = id, StatementId = "s-1", StatementNumber = "CAPSTMT-s-1", ProviderNPI = "1234567890", ProviderName = "Dr. Smith",
        Amount = 500m, Method = DisbursementMethod.NachaCredit, Status = DisbursementStatus.Pending,
    });

    private static async Task<Exception?> Try(Func<Task> write)
    {
        try
        {
            await write();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private Task UnsetRevisionAsync(string collection, string id)
        => _db.GetCollection<MongoDB.Bson.BsonDocument>(collection).UpdateOneAsync(
            new MongoDB.Bson.BsonDocument("_id", id), new MongoDB.Bson.BsonDocument("$unset", new MongoDB.Bson.BsonDocument("Revision", "")));

    // ── Capitation run ─────────────────────────────────────────────────

    [Fact]
    public async Task Run_TwoConcurrentUpdates_FromTheSameRead_ExactlyOneWins_TheOtherConflicts()
    {
        await SeedRunAsync("r-1");
        var a = (await Runs().GetByIdAsync("r-1"))!;
        var b = (await Runs().GetByIdAsync("r-1"))!;
        a.Status = CapitationRunStatus.Running;
        b.Status = CapitationRunStatus.Cancelled;

        var outcomes = await Task.WhenAll(Try(() => Runs().UpdateAsync(a)), Try(() => Runs().UpdateAsync(b)));

        outcomes.Count(o => o is null).Should().Be(1, "exactly one write wins");
        outcomes.Count(o => o is CapitationConcurrencyException).Should().Be(1, "the other gets a conflict, not a silent overwrite");
        var stored = (await Runs().GetByIdAsync("r-1"))!;
        stored.Status.Should().BeOneOf(CapitationRunStatus.Running, CapitationRunStatus.Cancelled);
        stored.Revision.Should().Be(1);
    }

    [Fact]
    public async Task Run_ReplaceFromAReadBeforeAnotherWrite_FailsInsteadOfUndoingIt()
    {
        await SeedRunAsync("r-1");
        var stale = (await Runs().GetByIdAsync("r-1"))!;
        var fresh = (await Runs().GetByIdAsync("r-1"))!;
        fresh.Status = CapitationRunStatus.Cancelled;
        await Runs().UpdateAsync(fresh);

        stale.Warnings.Add("stale");
        var act = () => Runs().UpdateAsync(stale);

        (await act.Should().ThrowAsync<ConcurrencyConflictException>()).Which.Should().BeOfType<CapitationConcurrencyException>();
        stale.Revision.Should().Be(0, "a failed write leaves the caller's revision as it was");
        var stored = (await Runs().GetByIdAsync("r-1"))!;
        stored.Status.Should().Be(CapitationRunStatus.Cancelled);
        stored.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_LegacyDocumentWithoutRevision_UpdatesOnce()
    {
        await SeedRunAsync("r-1");
        await UnsetRevisionAsync("capitation-runs", "r-1");
        var a = (await Runs().GetByIdAsync("r-1"))!;
        var b = (await Runs().GetByIdAsync("r-1"))!;
        a.Revision.Should().Be(0);

        a.Status = CapitationRunStatus.Running;
        await Runs().UpdateAsync(a);
        b.Status = CapitationRunStatus.Cancelled;
        var second = () => Runs().UpdateAsync(b);

        await second.Should().ThrowAsync<CapitationConcurrencyException>();
        var stored = (await Runs().GetByIdAsync("r-1"))!;
        stored.Status.Should().Be(CapitationRunStatus.Running);
        stored.Revision.Should().Be(1);
    }

    [Fact]
    public async Task Run_UpdateOfAMissingRun_WritesNothing_AsBefore()
    {
        var run = new CapitationRun { Id = "missing", TenantId = Tenant, RunNumber = "CAPRUN-missing" };

        var result = await Runs().UpdateAsync(run);

        result.Should().BeSameAs(run);
        (await Runs().GetByIdAsync("missing")).Should().BeNull();
    }

    [Fact]
    public async Task TwoConcurrentExecutes_OfOnePendingRun_StartItOnce()
    {
        await SeedRunAsync("r-1");
        // Both executes read the run while it is still Pending before either writes.
        var bothRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var contractCalls = 0;
        CapitationRunService Executor()
        {
            var real = Runs();
            var runs = new Mock<ICapitationRunRepository>();
            runs.Setup(r => r.GetByIdAsync(It.IsAny<string>())).Returns(async (string id) =>
            {
                var run = await real.GetByIdAsync(id);
                if (Interlocked.Increment(ref reads) == 2) bothRead.TrySetResult();
                await bothRead.Task.WaitAsync(TimeSpan.FromSeconds(30));
                return run;
            });
            runs.Setup(r => r.UpdateAsync(It.IsAny<CapitationRun>())).Returns((CapitationRun r) => real.UpdateAsync(r));
            var contracts = new Mock<ICapitationContractRepository>();
            contracts.Setup(c => c.GetActiveContractsAsync(It.IsAny<LineOfBusiness?>(), It.IsAny<ContractType?>()))
                .ReturnsAsync(() =>
                {
                    Interlocked.Increment(ref contractCalls);
                    return new List<CapitationContract>();
                });
            return new CapitationRunService(runs.Object, contracts.Object, Statements(), Mock.Of<IHttpClientFactory>(),
                TestSeparationOfDuties.Create(tenantId: Tenant), Mock.Of<ILogger<CapitationRunService>>());
        }

        var outcomes = await Task.WhenAll(
            Try(() => Executor().ExecuteRunAsync("r-1", "executor-1")),
            Try(() => Executor().ExecuteRunAsync("r-1", "executor-2")));

        outcomes.Count(o => o is null).Should().Be(1, "one execute runs");
        outcomes.Count(o => o is CapitationConcurrencyException).Should().Be(1, "the other is refused (409) before it does anything");
        contractCalls.Should().Be(1, "statements are generated by one execution only");
        var stored = (await Runs().GetByIdAsync("r-1"))!;
        stored.Status.Should().Be(CapitationRunStatus.Completed);
        stored.Revision.Should().Be(2, "Running, then Completed, by the one execution");
    }

    // ── Capitation disbursement ────────────────────────────────────────

    [Fact]
    public async Task Disbursement_TwoConcurrentUpdates_FromTheSameRead_ExactlyOneWins_TheOtherConflicts()
    {
        await SeedDisbursementAsync("d-1");
        var a = (await Disbursements().GetByIdAsync("d-1"))!;
        var b = (await Disbursements().GetByIdAsync("d-1"))!;
        a.Status = DisbursementStatus.Cancelled;
        b.ErrorMessage = "note B";

        var outcomes = await Task.WhenAll(Try(() => Disbursements().UpdateAsync(a)), Try(() => Disbursements().UpdateAsync(b)));

        outcomes.Count(o => o is null).Should().Be(1);
        outcomes.Count(o => o is CapitationConcurrencyException).Should().Be(1);
        (await Disbursements().GetByIdAsync("d-1"))!.Revision.Should().Be(1);
    }

    [Fact]
    public async Task Disbursement_ReplaceFromAReadBeforeTryClaimForRelease_FailsInsteadOfUndoingIt()
    {
        await SeedDisbursementAsync("d-1");
        var stale = (await Disbursements().GetByIdAsync("d-1"))!;
        (await Disbursements().TryClaimForReleaseAsync("d-1", "claim-1", DateTime.UtcNow, "approver-1")).Should().BeTrue();

        stale.Status = DisbursementStatus.Cancelled;
        var act = () => Disbursements().UpdateAsync(stale);

        await act.Should().ThrowAsync<CapitationConcurrencyException>();
        var stored = (await Disbursements().GetByIdAsync("d-1"))!;
        stored.Status.Should().Be(DisbursementStatus.Releasing);
        stored.ReleaseClaimId.Should().Be("claim-1");
        stored.Revision.Should().Be(1);
    }

    [Fact]
    public async Task Disbursement_TargetedWrites_BumpTheRevision()
    {
        await SeedDisbursementAsync("d-1");
        await Disbursements().TryClaimForReleaseAsync("d-1", "claim-1", DateTime.UtcNow, "approver-1");
        await Disbursements().ReleaseClaimAsync("d-1", "claim-1", "nothing sent");

        var stored = (await Disbursements().GetByIdAsync("d-1"))!;
        stored.Status.Should().Be(DisbursementStatus.Pending);
        stored.Revision.Should().Be(2);
        stored.ErrorMessage = "after the targeted writes";
        await Disbursements().UpdateAsync(stored);
        (await Disbursements().GetByIdAsync("d-1"))!.Revision.Should().Be(3);
    }

    [Fact]
    public async Task Disbursement_LegacyDocumentWithoutRevision_UpdatesOnce()
    {
        await SeedDisbursementAsync("d-1");
        await UnsetRevisionAsync("capitation-disbursements", "d-1");
        var a = (await Disbursements().GetByIdAsync("d-1"))!;
        var b = (await Disbursements().GetByIdAsync("d-1"))!;

        a.ErrorMessage = "A";
        await Disbursements().UpdateAsync(a);
        b.ErrorMessage = "B";
        var second = () => Disbursements().UpdateAsync(b);

        await second.Should().ThrowAsync<CapitationConcurrencyException>();
        var stored = (await Disbursements().GetByIdAsync("d-1"))!;
        stored.ErrorMessage.Should().Be("A");
        stored.Revision.Should().Be(1);
    }

    [Fact]
    public async Task Disbursement_UpdateOfAMissingDisbursement_WritesNothing_AsBefore()
    {
        var d = new CapitationDisbursement { Id = "missing", TenantId = Tenant };

        (await Disbursements().UpdateAsync(d)).Should().BeSameAs(d);
        (await Disbursements().GetByIdAsync("missing")).Should().BeNull();
    }

    [Fact]
    public async Task StaleCancel_CannotUndoTryClaimForRelease()
    {
        await SeedStatementAsync("s-1", CapitationStatementStatus.PaymentInitiated);
        await SeedDisbursementAsync("d-1");
        // The cancel reads the disbursement Pending; a NACHA release claims it
        // (Pending to Releasing) before the cancel writes.
        var real = Disbursements();
        var disbursements = new Mock<ICapitationDisbursementRepository>();
        disbursements.Setup(r => r.GetByIdAsync("d-1")).Returns(async () =>
        {
            var read = await real.GetByIdAsync("d-1");
            (await Disbursements().TryClaimForReleaseAsync("d-1", "claim-1", DateTime.UtcNow, "approver-1")).Should().BeTrue();
            return read;
        });
        disbursements.Setup(r => r.UpdateAsync(It.IsAny<CapitationDisbursement>())).Returns((CapitationDisbursement d) => real.UpdateAsync(d));
        var config = new ConfigurationBuilder().Build();
        var service = new CapitationDisbursementService(
            disbursements.Object, Statements(), Mock.Of<ICapitationRunRepository>(), Mock.Of<INachaCreditFileService>(),
            Mock.Of<IStripeConnectService>(), Mock.Of<IHttpClientFactory>(), config,
            TestSeparationOfDuties.Create(tenantId: Tenant), new FactoryBackedProviderBankAccountSource(Mock.Of<IHttpClientFactory>()),
            Mock.Of<INachaDispatcher>(), Mock.Of<ILogger<CapitationDisbursementService>>(), Request());

        var act = () => service.CancelDisbursementAsync("d-1");

        await act.Should().ThrowAsync<CapitationConcurrencyException>("the release claimed it first; the cancel is refused (409)");
        var stored = (await Disbursements().GetByIdAsync("d-1"))!;
        stored.Status.Should().Be(DisbursementStatus.Releasing);
        stored.ReleaseClaimId.Should().Be("claim-1");
        (await Statements().GetByIdAsync("s-1"))!.Status.Should().Be(CapitationStatementStatus.PaymentInitiated,
            "the statement is not reverted to Approved by a cancel that did not happen");
    }

    // ── Capitation statement ───────────────────────────────────────────

    [Fact]
    public async Task Statement_TwoConcurrentUpdates_FromTheSameRead_ExactlyOneWins_TheOtherConflicts()
    {
        await SeedStatementAsync("s-1", CapitationStatementStatus.Generated);
        var a = (await Statements().GetByIdAsync("s-1"))!;
        var b = (await Statements().GetByIdAsync("s-1"))!;
        a.Status = CapitationStatementStatus.Approved;
        b.Status = CapitationStatementStatus.Voided;

        var outcomes = await Task.WhenAll(Try(() => Statements().UpdateAsync(a)), Try(() => Statements().UpdateAsync(b)));

        outcomes.Count(o => o is null).Should().Be(1);
        outcomes.Count(o => o is CapitationConcurrencyException).Should().Be(1);
        var stored = (await Statements().GetByIdAsync("s-1"))!;
        stored.Status.Should().BeOneOf(CapitationStatementStatus.Approved, CapitationStatementStatus.Voided);
        stored.Revision.Should().Be(1);
    }

    [Fact]
    public async Task Statement_ReplaceFromAReadBeforeTryStartPayment_FailsInsteadOfUndoingIt()
    {
        await SeedStatementAsync("s-1", CapitationStatementStatus.Approved);
        var stale = (await Statements().GetByIdAsync("s-1"))!;
        (await Statements().TryStartPaymentAsync("s-1", "d-1")).Should().BeTrue();

        stale.Status = CapitationStatementStatus.Voided;
        var act = () => Statements().UpdateAsync(stale);

        await act.Should().ThrowAsync<CapitationConcurrencyException>();
        var stored = (await Statements().GetByIdAsync("s-1"))!;
        stored.Status.Should().Be(CapitationStatementStatus.PaymentInitiated);
        stored.EftDisbursementId.Should().Be("d-1");
    }

    [Fact]
    public async Task Statement_PaymentTransitions_BumpTheRevision_SoAReplaceBeforeAnyOfThemFails()
    {
        await SeedStatementAsync("s-1", CapitationStatementStatus.Approved);
        (await Statements().TryStartPaymentAsync("s-1", "d-1")).Should().BeTrue();
        var beforeUnknown = (await Statements().GetByIdAsync("s-1"))!;
        (await Statements().MarkPaymentUnknownAsync("s-1", "d-1")).Should().BeTrue();
        var beforeResolve = (await Statements().GetByIdAsync("s-1"))!;
        (await Statements().ResolvePaymentUnknownAsync("s-1", "d-1", paid: false)).Should().BeTrue();
        (await Statements().TryStartPaymentAsync("s-1", "d-2")).Should().BeTrue();
        await Statements().UndoStartPaymentAsync("s-1", "d-2");

        var stored = (await Statements().GetByIdAsync("s-1"))!;
        stored.Status.Should().Be(CapitationStatementStatus.Approved);
        stored.Revision.Should().Be(5, "each of the five transitions bumps it");

        beforeUnknown.Status = CapitationStatementStatus.Paid;
        await FluentActions.Awaiting(() => Statements().UpdateAsync(beforeUnknown)).Should().ThrowAsync<CapitationConcurrencyException>();
        beforeResolve.Status = CapitationStatementStatus.Paid;
        await FluentActions.Awaiting(() => Statements().UpdateAsync(beforeResolve)).Should().ThrowAsync<CapitationConcurrencyException>();
        (await Statements().GetByIdAsync("s-1"))!.Status.Should().Be(CapitationStatementStatus.Approved);
    }

    [Fact]
    public async Task Statement_LegacyDocumentWithoutRevision_UpdatesOnce()
    {
        await SeedStatementAsync("s-1", CapitationStatementStatus.Generated);
        await UnsetRevisionAsync("capitation-statements", "s-1");
        var a = (await Statements().GetByIdAsync("s-1"))!;
        var b = (await Statements().GetByIdAsync("s-1"))!;

        a.Status = CapitationStatementStatus.Approved;
        await Statements().UpdateAsync(a);
        b.Status = CapitationStatementStatus.Voided;
        var second = () => Statements().UpdateAsync(b);

        await second.Should().ThrowAsync<CapitationConcurrencyException>();
        var stored = (await Statements().GetByIdAsync("s-1"))!;
        stored.Status.Should().Be(CapitationStatementStatus.Approved);
        stored.Revision.Should().Be(1);
    }

    [Fact]
    public async Task Statement_UpdateOfAMissingStatement_WritesNothing_AsBefore()
    {
        var s = new CapitationStatement { Id = "missing", TenantId = Tenant };

        (await Statements().UpdateAsync(s)).Should().BeSameAs(s);
        (await Statements().GetByIdAsync("missing")).Should().BeNull();
    }

    [Fact]
    public async Task StaleStatementReplace_CannotUndoTryStartPayment()
    {
        await SeedStatementAsync("s-1", CapitationStatementStatus.Approved);
        // A hold reads the statement Approved; a release starts paying it
        // (Approved to PaymentInitiated) before the hold writes.
        var real = Statements();
        var statements = new Mock<ICapitationStatementRepository>();
        statements.Setup(r => r.GetByIdAsync("s-1")).Returns(async () =>
        {
            var read = await real.GetByIdAsync("s-1");
            (await Statements().TryStartPaymentAsync("s-1", "d-1")).Should().BeTrue();
            return read;
        });
        statements.Setup(r => r.UpdateAsync(It.IsAny<CapitationStatement>())).Returns((CapitationStatement s) => real.UpdateAsync(s));
        var service = new CapitationRunService(Mock.Of<ICapitationRunRepository>(), Mock.Of<ICapitationContractRepository>(),
            statements.Object, Mock.Of<IHttpClientFactory>(), TestSeparationOfDuties.Create(tenantId: Tenant),
            Mock.Of<ILogger<CapitationRunService>>());

        var act = () => service.HoldStatementAsync("s-1", "query on rates", "reviewer-1");

        await act.Should().ThrowAsync<CapitationConcurrencyException>("the payment started first; the hold is refused (409)");
        var stored = (await Statements().GetByIdAsync("s-1"))!;
        stored.Status.Should().Be(CapitationStatementStatus.PaymentInitiated);
        stored.EftDisbursementId.Should().Be("d-1");
    }

    [Fact]
    public async Task NachaRelease_RecordsItsOutcome_OverTheRevisionItsOwnClaimMadeFresh()
    {
        // The release claims (bumps) and later replaces the same disbursement:
        // its own claim must not make its outcome write look stale.
        await SeedStatementAsync("s-1", CapitationStatementStatus.PaymentInitiated);
        await SeedDisbursementAsync("d-1");
        var dispatcher = new Mock<INachaDispatcher>();
        dispatcher.Setup(d => d.DispatchAsync(It.IsAny<NachaTransmissionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((NachaTransmissionRequest r, CancellationToken _) => new NachaDispatchOutcome
            {
                Status = NachaTransmissionStatus.Transmitted,
                Receipt = new NachaTransmissionReceipt { TenantId = r.TenantId, FileReference = r.FileReference },
            });
        var bank = new Mock<IProviderBankAccountSource>();
        bank.Setup(b => b.GetForDisbursementAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderBankAccountLookup.Of(new ProviderBankAccountDto
            {
                EftEnabled = true, RoutingNumber = "091000019", AccountNumber = "123456789", AccountType = "Checking",
                AccountHolderName = "Dr. Smith",
            }));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Nacha:ImmediateDestination"] = "091000019", ["Nacha:ImmediateOrigin"] = "1234567890",
            ["Nacha:ImmediateDestinationName"] = "TEST BANK", ["Nacha:ImmediateOriginName"] = "HEALTH PLAN",
            ["Nacha:CompanyName"] = "HEALTH PLAN", ["Nacha:CompanyId"] = "1234567890", ["Nacha:OriginatingDfi"] = "9100001",
        }).Build();
        var accessor = Request();
        var service = new CapitationDisbursementService(
            new CapitationDisbursementRepositoryMongo(_db, accessor),
            new CapitationStatementRepositoryMongo(_db, accessor, Mock.Of<ILogger<CapitationStatementRepositoryMongo>>()),
            Mock.Of<ICapitationRunRepository>(), new NachaCreditFileService(config, Mock.Of<ILogger<NachaCreditFileService>>()),
            Mock.Of<IStripeConnectService>(), Mock.Of<IHttpClientFactory>(), config,
            TestSeparationOfDuties.Create(tenantId: Tenant), bank.Object, dispatcher.Object,
            Mock.Of<ILogger<CapitationDisbursementService>>(), accessor);

        var result = await service.GenerateNachaCreditFileAsync("approver-1");

        result.Entries.Should().ContainSingle();
        var stored = (await Disbursements().GetByIdAsync("d-1"))!;
        stored.Status.Should().Be(DisbursementStatus.Submitted);
        stored.Revision.Should().Be(2, "claimed, then the outcome recorded");
    }
}
