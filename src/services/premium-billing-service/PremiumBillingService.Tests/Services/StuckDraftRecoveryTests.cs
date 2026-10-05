using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.NachaTransmission;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using PremiumBillingService.Clients;
using PremiumBillingService.Models;
using PremiumBillingService.Repositories;
using PremiumBillingService.Services;

namespace PremiumBillingService.Tests.Services;

/// <summary>
/// Drafts left Releasing (the release stopped mid-send) or PaymentUnknown are
/// listed for a person and resolved only by a user other than the releaser,
/// with a reason, after checking with the bank. Nothing is recovered
/// automatically. Against a real mongod and the real Mongo repository.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class StuckDraftRecoveryTests : IAsyncLifetime
{
    private const string Tenant = "tenant-stuck";

    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;
    private readonly AuditLog _log = new();

    public StuckDraftRecoveryTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("pb_stuck");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);

    private sealed class AuditLog : ILogger<EftDraftService>
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

    private EftDraftRepositoryMongo Drafts() => new(_database, Request());

    private EftDraftService Approver(string userId, bool isService = false)
    {
        var accessor = Request();
        var actor = new Mock<ICurrentActor>();
        actor.SetupGet(a => a.UserId).Returns(userId);
        actor.SetupGet(a => a.TenantId).Returns(Tenant);
        actor.SetupGet(a => a.IsAuthenticated).Returns(true);
        actor.SetupGet(a => a.IsService).Returns(isService);
        var dispatcher = new Mock<INachaDispatcher>();
        dispatcher.Setup(d => d.ListHeldAsync(Tenant, It.IsAny<CancellationToken>())).ReturnsAsync(new List<NachaHeldFile>
        {
            new() { FileReference = "NACHA-STUCK", TenantId = Tenant, Status = NachaHeldFileStatus.Transmitting, LastAttemptAt = DateTime.UtcNow.AddHours(-2) },
            new() { FileReference = "NACHA-RETRYING", TenantId = Tenant, Status = NachaHeldFileStatus.Transmitting, LastAttemptAt = DateTime.UtcNow },
            new() { FileReference = "NACHA-HELD", TenantId = Tenant, Status = NachaHeldFileStatus.AwaitingRetrieval, CreatedAt = DateTime.UtcNow.AddHours(-2) },
        });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        return new EftDraftService(
            new EftDraftRepositoryMongo(_database, accessor),
            Mock.Of<IPremiumInvoiceRepository>(), Mock.Of<IBillingRunRepository>(),
            new NachaFileService(config, Mock.Of<ILogger<NachaFileService>>()),
            Mock.Of<IStripeAchService>(), Mock.Of<ISponsorBankAccountSource>(), dispatcher.Object, actor.Object, accessor, config,
            _log);
    }

    private async Task<EftDraft> SeedAsync(EftDraftStatus status, string invoiceId, TimeSpan claimedAgo, EftMethod method = EftMethod.Nacha, string? releasedBy = "approver-1")
        => await Drafts().CreateAsync(new EftDraft
        {
            InvoiceId = invoiceId, InvoiceNumber = invoiceId.ToUpperInvariant(), GroupNumber = "GRP001", Amount = 100m, Method = method,
            Status = status, ReleaseClaimId = status == EftDraftStatus.Releasing ? "claim-1" : null,
            ReleaseClaimedAt = DateTime.UtcNow - claimedAgo, ReleasedBy = releasedBy, InitiatedBy = "approver-1",
        });

    [Fact]
    public async Task ListStuck_ShowsOnlyRowsThatNeedAPerson()
    {
        var stuck = await SeedAsync(EftDraftStatus.Releasing, "inv-1", TimeSpan.FromHours(2));
        await SeedAsync(EftDraftStatus.Releasing, "inv-2", TimeSpan.FromMinutes(1));
        var unknown = await SeedAsync(EftDraftStatus.PaymentUnknown, "inv-3", TimeSpan.Zero, EftMethod.StripeAch);
        var delivery = await SeedAsync(EftDraftStatus.DeliveryUnknown, "inv-4", TimeSpan.Zero);
        await SeedAsync(EftDraftStatus.Pending, "inv-5", TimeSpan.Zero);

        var report = await Approver("approver-2").ListStuckDraftsAsync(TimeSpan.FromMinutes(30));

        report.Releasing.Select(d => d.Id).Should().Equal(stuck.Id);
        report.PaymentUnknown.Select(d => d.Id).Should().Equal(unknown.Id);
        report.DeliveryUnknown.Select(d => d.Id).Should().Equal(delivery.Id);
        report.TransmittingHeldFiles.Select(f => f.FileReference).Should().Equal("NACHA-STUCK");
    }

    [Fact]
    public async Task ResolveStuck_ByTheReleaser_OrAService_OrWithoutAReason_IsRefused_AndChangesNothing()
    {
        var draft = await SeedAsync(EftDraftStatus.Releasing, "inv-1", TimeSpan.FromHours(2));

        await FluentActions.Awaiting(() => Approver("APPROVER-1").ResolveStuckDraftAsync(draft.Id, false, "bank has nothing", null))
            .Should().ThrowAsync<SeparationOfDutiesException>();
        await FluentActions.Awaiting(() => Approver("svc-premium", isService: true).ResolveStuckDraftAsync(draft.Id, false, "bank has nothing", null))
            .Should().ThrowAsync<SeparationOfDutiesException>();
        await FluentActions.Awaiting(() => Approver("approver-2").ResolveStuckDraftAsync(draft.Id, false, "  ", null))
            .Should().ThrowAsync<ArgumentException>();

        (await Drafts().GetByIdAsync(draft.Id))!.Status.Should().Be(EftDraftStatus.Releasing);
    }

    [Fact]
    public async Task ResolveStuck_WithoutARecordedReleaser_IsRefused()
    {
        var draft = await SeedAsync(EftDraftStatus.Releasing, "inv-1", TimeSpan.FromHours(2), releasedBy: null);

        await FluentActions.Awaiting(() => Approver("approver-2").ResolveStuckDraftAsync(draft.Id, false, "bank has nothing", null))
            .Should().ThrowAsync<SeparationOfDutiesException>();
    }

    [Fact]
    public async Task ResolveStuck_AReleaseThatMayStillBeRunning_IsRefused()
    {
        var draft = await SeedAsync(EftDraftStatus.Releasing, "inv-1", TimeSpan.FromMinutes(1));

        await FluentActions.Awaiting(() => Approver("approver-2").ResolveStuckDraftAsync(draft.Id, false, "bank has nothing", null))
            .Should().ThrowAsync<InvalidOperationException>();
        (await Drafts().GetByIdAsync(draft.Id))!.Status.Should().Be(EftDraftStatus.Releasing);
    }

    [Fact]
    public async Task ResolveStuck_NotSent_GoesBackToPending_AndIsAudited()
    {
        var draft = await SeedAsync(EftDraftStatus.Releasing, "inv-1", TimeSpan.FromHours(2));

        var resolved = await Approver("approver-2").ResolveStuckDraftAsync(draft.Id, false, "Bank ops: no file received", null);

        resolved.Status.Should().Be(EftDraftStatus.Pending);
        var stored = (await Drafts().GetByIdAsync(draft.Id))!;
        stored.Status.Should().Be(EftDraftStatus.Pending);
        stored.ReleaseClaimId.Should().BeNull();
        stored.ReleasedBy.Should().BeNull();
        _log.Lines.Should().Contain(l => l.StartsWith("AUDIT") && l.Contains("approver-2") && l.Contains("Bank ops: no file received"));
    }

    [Fact]
    public async Task ResolveStuck_Sent_IsSubmitted()
    {
        var draft = await SeedAsync(EftDraftStatus.Releasing, "inv-1", TimeSpan.FromHours(2));

        var resolved = await Approver("approver-2").ResolveStuckDraftAsync(draft.Id, true, "Bank ops: file received", null);

        resolved.Status.Should().Be(EftDraftStatus.Submitted);
        (await Drafts().GetByIdAsync(draft.Id))!.Status.Should().Be(EftDraftStatus.Submitted);
    }

    [Fact]
    public async Task ResolveStuck_StripePaymentUnknown_SentNeedsThePaymentIntent_NotSentFreesTheInvoice()
    {
        var draft = await SeedAsync(EftDraftStatus.PaymentUnknown, "inv-1", TimeSpan.Zero, EftMethod.StripeAch);

        await FluentActions.Awaiting(() => Approver("approver-2").ResolveStuckDraftAsync(draft.Id, true, "Stripe shows the debit", null))
            .Should().ThrowAsync<ArgumentException>();

        var failed = await Approver("approver-2").ResolveStuckDraftAsync(draft.Id, false, "Stripe shows no PaymentIntent", null);
        failed.Status.Should().Be(EftDraftStatus.Failed);
        // The invoice can be drafted again.
        await Drafts().CreateAsync(new EftDraft { InvoiceId = "inv-1", GroupNumber = "GRP001", Amount = 100m, Status = EftDraftStatus.Pending });

        var other = await SeedAsync(EftDraftStatus.PaymentUnknown, "inv-2", TimeSpan.Zero, EftMethod.StripeAch);
        var sent = await Approver("approver-2").ResolveStuckDraftAsync(other.Id, true, "Stripe shows the debit", "pi_123");
        sent.Status.Should().Be(EftDraftStatus.Submitted);
        sent.StripePaymentIntentId.Should().Be("pi_123");
    }

    [Fact]
    public async Task ResolveStuck_ARowThatIsNotStuck_IsRefused()
    {
        var draft = await SeedAsync(EftDraftStatus.DeliveryUnknown, "inv-1", TimeSpan.Zero);

        await FluentActions.Awaiting(() => Approver("approver-2").ResolveStuckDraftAsync(draft.Id, false, "bank has nothing", null))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*resolve-delivery*");
    }
}
