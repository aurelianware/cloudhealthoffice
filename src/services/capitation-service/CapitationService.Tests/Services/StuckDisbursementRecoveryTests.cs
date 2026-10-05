using CapitationService.Models;
using CapitationService.Repositories;
using CapitationService.Services;
using CapitationService.Tests.Support;
using CloudHealthOffice.NachaTransmission;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace CapitationService.Tests.Services;

/// <summary>
/// Disbursements left Releasing (the release stopped mid-send) or PaymentUnknown
/// are listed for a person and resolved only by a user other than the releaser,
/// with a reason, after checking with the bank or Stripe. Nothing is recovered
/// automatically. Against a real mongod and the real Mongo repositories.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class StuckDisbursementRecoveryTests : IAsyncLifetime
{
    private const string Tenant = "tenant-stuck";

    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;
    private readonly AuditLog _log = new();

    public StuckDisbursementRecoveryTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("cap_stuck");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);

    private sealed class AuditLog : ILogger<CapitationDisbursementService>
    {
        public List<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }

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

    private CapitationStatementRepositoryMongo Statements() => new(_database, Request(), Mock.Of<ILogger<CapitationStatementRepositoryMongo>>());
    private CapitationDisbursementRepositoryMongo Disbursements() => new(_database, Request());

    private CapitationDisbursementService Service()
    {
        var accessor = Request();
        var dispatcher = new Mock<INachaDispatcher>();
        dispatcher.Setup(d => d.ListHeldAsync(Tenant, It.IsAny<CancellationToken>())).ReturnsAsync(new List<NachaHeldFile>
        {
            new() { FileReference = "NACHA-STUCK", TenantId = Tenant, Status = NachaHeldFileStatus.Transmitting, LastAttemptAt = DateTime.UtcNow.AddHours(-2) },
            new() { FileReference = "NACHA-RETRYING", TenantId = Tenant, Status = NachaHeldFileStatus.Transmitting, LastAttemptAt = DateTime.UtcNow },
        });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        return new CapitationDisbursementService(
            new CapitationDisbursementRepositoryMongo(_database, accessor),
            new CapitationStatementRepositoryMongo(_database, accessor, Mock.Of<ILogger<CapitationStatementRepositoryMongo>>()),
            Mock.Of<ICapitationRunRepository>(),
            new NachaCreditFileService(config, Mock.Of<ILogger<NachaCreditFileService>>()),
            Mock.Of<IStripeConnectService>(), Mock.Of<IHttpClientFactory>(), config,
            TestSeparationOfDuties.Create(tenantId: Tenant),
            Mock.Of<IProviderBankAccountSource>(), dispatcher.Object, _log, accessor);
    }

    private static NachaActor User(string id) => new(id, false);

    private async Task<CapitationDisbursement> SeedAsync(DisbursementStatus status, string statementId, TimeSpan claimedAgo,
        DisbursementMethod method = DisbursementMethod.NachaCredit, string? releasedBy = "approver-1")
    {
        var statementStatus = status == DisbursementStatus.PaymentUnknown ? CapitationStatementStatus.PaymentUnknown : CapitationStatementStatus.PaymentInitiated;
        var disbursement = new CapitationDisbursement
        {
            StatementId = statementId, StatementNumber = $"CAPSTMT-{statementId}", ProviderNPI = "1234567890", ProviderName = "Dr. Smith",
            Amount = 500m, Method = method, Status = status, ReleaseClaimId = status == DisbursementStatus.Releasing ? "claim-1" : null,
            ReleaseClaimedAt = DateTime.UtcNow - claimedAgo, ReleasedBy = releasedBy, InitiatedBy = "approver-1",
        };
        await Statements().CreateAsync(new CapitationStatement
        {
            Id = statementId, StatementNumber = $"CAPSTMT-{statementId}", ProviderNPI = "1234567890", Status = statementStatus,
            NetPayable = 500m, EftDisbursementId = disbursement.Id, CreatedBy = "maker-1",
        });
        return await Disbursements().CreateAsync(disbursement);
    }

    [Fact]
    public async Task ListStuck_ShowsOnlyRowsThatNeedAPerson()
    {
        var stuck = await SeedAsync(DisbursementStatus.Releasing, "s-1", TimeSpan.FromHours(2));
        await SeedAsync(DisbursementStatus.Releasing, "s-2", TimeSpan.FromMinutes(1));
        var unknown = await SeedAsync(DisbursementStatus.PaymentUnknown, "s-3", TimeSpan.Zero, DisbursementMethod.StripeConnect);
        var delivery = await SeedAsync(DisbursementStatus.DeliveryUnknown, "s-4", TimeSpan.Zero);
        await SeedAsync(DisbursementStatus.Pending, "s-5", TimeSpan.Zero);

        var report = await Service().ListStuckDisbursementsAsync(Tenant, TimeSpan.FromMinutes(30));

        report.Releasing.Select(d => d.Id).Should().Equal(stuck.Id);
        report.PaymentUnknown.Select(d => d.Id).Should().Equal(unknown.Id);
        report.DeliveryUnknown.Select(d => d.Id).Should().Equal(delivery.Id);
        report.TransmittingHeldFiles.Select(f => f.FileReference).Should().Equal("NACHA-STUCK");
    }

    [Fact]
    public async Task ResolveStuck_ByTheReleaser_OrAService_OrWithoutAReason_OrUnrecordedReleaser_IsRefused()
    {
        var d = await SeedAsync(DisbursementStatus.Releasing, "s-1", TimeSpan.FromHours(2));
        var unrecorded = await SeedAsync(DisbursementStatus.Releasing, "s-2", TimeSpan.FromHours(2), releasedBy: null);

        await FluentActions.Awaiting(() => Service().ResolveStuckDisbursementAsync(d.Id, User("APPROVER-1"), false, "no file", null))
            .Should().ThrowAsync<SeparationOfDutiesException>();
        await FluentActions.Awaiting(() => Service().ResolveStuckDisbursementAsync(d.Id, new NachaActor("svc", true), false, "no file", null))
            .Should().ThrowAsync<SeparationOfDutiesException>();
        await FluentActions.Awaiting(() => Service().ResolveStuckDisbursementAsync(d.Id, User("approver-2"), false, " ", null))
            .Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => Service().ResolveStuckDisbursementAsync(unrecorded.Id, User("approver-2"), false, "no file", null))
            .Should().ThrowAsync<SeparationOfDutiesException>();

        (await Disbursements().GetByIdAsync(d.Id))!.Status.Should().Be(DisbursementStatus.Releasing);
    }

    [Fact]
    public async Task ResolveStuck_AReleaseThatMayStillBeRunning_IsRefused()
    {
        var d = await SeedAsync(DisbursementStatus.Releasing, "s-1", TimeSpan.FromMinutes(1));

        await FluentActions.Awaiting(() => Service().ResolveStuckDisbursementAsync(d.Id, User("approver-2"), false, "no file", null))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ResolveStuck_Releasing_NotSent_GoesBackToPending_Sent_IsSubmitted_AndBothAreAudited()
    {
        var notSent = await SeedAsync(DisbursementStatus.Releasing, "s-1", TimeSpan.FromHours(2));
        var sent = await SeedAsync(DisbursementStatus.Releasing, "s-2", TimeSpan.FromHours(2));

        (await Service().ResolveStuckDisbursementAsync(notSent.Id, User("approver-2"), false, "Bank ops: no file", null))
            .Status.Should().Be(DisbursementStatus.Pending);
        (await Service().ResolveStuckDisbursementAsync(sent.Id, User("approver-2"), true, "Bank ops: file received", null))
            .Status.Should().Be(DisbursementStatus.Submitted);

        var stored = (await Disbursements().GetByIdAsync(notSent.Id))!;
        stored.Status.Should().Be(DisbursementStatus.Pending);
        stored.ReleaseClaimId.Should().BeNull();
        (await Disbursements().GetByIdAsync(sent.Id))!.Status.Should().Be(DisbursementStatus.Submitted);
        _log.Lines.Count(l => l.StartsWith("AUDIT") && l.Contains("approver-2")).Should().Be(2);
    }

    [Fact]
    public async Task ResolveStuck_StripePaymentUnknown_NotSent_MakesTheStatementPayableAgain()
    {
        var d = await SeedAsync(DisbursementStatus.PaymentUnknown, "s-1", TimeSpan.Zero, DisbursementMethod.StripeConnect);

        var resolved = await Service().ResolveStuckDisbursementAsync(d.Id, User("approver-2"), false, "Stripe shows no transfer for the key", null);

        resolved.Status.Should().Be(DisbursementStatus.Failed);
        var statement = (await Statements().GetByIdAsync("s-1"))!;
        statement.Status.Should().Be(CapitationStatementStatus.Approved);
        statement.EftDisbursementId.Should().BeNull();
    }

    [Fact]
    public async Task ResolveStuck_StripePaymentUnknown_Sent_NeedsTheTransfer_AndKeepsTheStatementPaid()
    {
        var d = await SeedAsync(DisbursementStatus.PaymentUnknown, "s-1", TimeSpan.Zero, DisbursementMethod.StripeConnect);

        await FluentActions.Awaiting(() => Service().ResolveStuckDisbursementAsync(d.Id, User("approver-2"), true, "Stripe shows it", null))
            .Should().ThrowAsync<ArgumentException>();
        var resolved = await Service().ResolveStuckDisbursementAsync(d.Id, User("approver-2"), true, "Stripe shows it", "tr_123");

        resolved.Status.Should().Be(DisbursementStatus.Submitted);
        resolved.StripeTransferId.Should().Be("tr_123");
        (await Statements().GetByIdAsync("s-1"))!.Status.Should().Be(CapitationStatementStatus.PaymentInitiated);
    }

    [Fact]
    public async Task Claim_RecordsTheReleaser_AndReleasingTheClaimClearsIt()
    {
        var d = await SeedAsync(DisbursementStatus.Pending, "s-1", TimeSpan.Zero, releasedBy: null);
        var repository = Disbursements();

        (await repository.TryClaimForReleaseAsync(d.Id, "claim-9", DateTime.UtcNow, "approver-1")).Should().BeTrue();
        (await repository.GetByIdAsync(d.Id))!.ReleasedBy.Should().Be("approver-1", "a release that stops mid-send still names its releaser");

        await repository.ReleaseClaimAsync(d.Id, "claim-9", "nothing was sent");
        (await repository.GetByIdAsync(d.Id))!.ReleasedBy.Should().BeNull();
    }
}
