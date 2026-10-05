using System.Collections.Concurrent;
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
/// An invoice is debited by at most one active draft: running the batch twice,
/// or a single draft racing a batch, never puts the same invoice in two drafts
/// or two NACHA files. Against a real mongod and the real Mongo repository.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class InvoiceDraftGuardTests : IAsyncLifetime
{
    private const string Tenant = "tenant-guard";

    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;
    private readonly Dictionary<string, PremiumInvoice> _invoices = new();

    public InvoiceDraftGuardTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("pb_invoice_guard");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);

    private sealed class Bank : INachaDispatcher
    {
        public ConcurrentBag<NachaTransmissionRequest> Files { get; } = new();

        public async Task<NachaDispatchOutcome> DispatchAsync(NachaTransmissionRequest request, CancellationToken cancellationToken = default)
        {
            Files.Add(request);
            await Task.Delay(20, cancellationToken);
            return new NachaDispatchOutcome
            {
                Status = NachaTransmissionStatus.Transmitted,
                Receipt = new NachaTransmissionReceipt { TenantId = request.TenantId, FileReference = request.FileReference },
            };
        }

        public Task<IReadOnlyList<NachaHeldFile>> ListHeldAsync(string tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<NachaHeldFile?> GetHeldAsync(string tenantId, string fileReference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<NachaDispatchOutcome> RetryAsync(string tenantId, string fileReference, NachaActor actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<NachaRetrievedFile> RetrieveAsync(string tenantId, string fileReference, NachaActor actor, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<NachaHeldFile> ResolveDeliveryUnknownAsync(string tenantId, string fileReference, NachaActor actor, bool bankReceived, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Nacha:ImmediateDestination"] = "091000019",
        ["Nacha:ImmediateOrigin"] = "1234567890",
        ["Nacha:ImmediateDestinationName"] = "TEST BANK",
        ["Nacha:ImmediateOriginName"] = "HEALTH PLAN",
        ["Nacha:CompanyName"] = "HEALTH PLAN",
        ["Nacha:CompanyId"] = "1234567890",
        ["Nacha:OriginatingDfi"] = "9100001",
    }).Build();

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

    private void SeedInvoices(int count)
    {
        for (var i = 0; i < count; i++)
        {
            _invoices[$"inv-{i}"] = new PremiumInvoice
            {
                Id = $"inv-{i}", TenantId = Tenant, InvoiceNumber = $"INV-{i}", GroupNumber = $"GRP{i:000}", SponsorName = "ACME",
                Status = InvoiceStatus.Sent, TotalAmount = 100m + i, BalanceDue = 100m + i, CreatedBy = "maker-1",
            };
        }
    }

    private EftDraftService Approver(string userId, INachaDispatcher bank)
    {
        var accessor = Request();
        var actor = new Mock<ICurrentActor>();
        actor.SetupGet(a => a.UserId).Returns(userId);
        actor.SetupGet(a => a.TenantId).Returns(Tenant);
        actor.SetupGet(a => a.IsAuthenticated).Returns(true);
        var invoices = new Mock<IPremiumInvoiceRepository>();
        invoices.Setup(r => r.GetByIdAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => _invoices.TryGetValue(id, out var invoice) ? invoice : null);
        var accounts = new Mock<ISponsorBankAccountSource>();
        accounts.Setup(a => a.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SponsorBankAccountLookup.Found(new SponsorBankAccount
            {
                EftEnabled = true, PreferredMethod = EftMethod.Nacha, RoutingNumber = "091000019", AccountNumber = "123456789",
                RoutingNumberLast4 = "0019", AccountNumberLast4 = "6789", AccountHolderName = "ACME"
            }));
        var config = Config();
        return new EftDraftService(
            new EftDraftRepositoryMongo(_database, accessor),
            invoices.Object, Mock.Of<IBillingRunRepository>(),
            new NachaFileService(config, Mock.Of<ILogger<NachaFileService>>()),
            Mock.Of<IStripeAchService>(), accounts.Object, bank, actor.Object, accessor, config,
            Mock.Of<ILogger<EftDraftService>>());
    }

    private async Task<List<EftDraft>> AllDraftsAsync()
        => await _database.GetCollection<EftDraft>("eftDrafts").Find(_ => true).ToListAsync();

    [Fact]
    public async Task BatchRunTwice_SecondRunDraftsNothing_AndNoInvoiceIsInTwoFiles()
    {
        SeedInvoices(4);
        var bank = new Bank();
        var ids = _invoices.Keys.ToList();

        var first = await Approver("approver-1", bank).InitiateBatchDraftAsync(new InitiateBatchEftRequest { InvoiceIds = ids });
        var second = await Approver("approver-2", bank).InitiateBatchDraftAsync(new InitiateBatchEftRequest { InvoiceIds = ids });

        first.DraftsInitiated.Should().Be(4);
        second.DraftsInitiated.Should().Be(0);
        second.Errors.Should().Be(4);
        second.NachaFile.Should().BeNull();
        bank.Files.Should().ContainSingle();
        (await AllDraftsAsync()).Should().HaveCount(4).And.OnlyContain(d => d.Status == EftDraftStatus.Submitted);
    }

    [Fact]
    public async Task ConcurrentBatchesAndSingleDrafts_EachInvoiceHasOneDraft_AndIsInAtMostOneFile()
    {
        SeedInvoices(6);
        var bank = new Bank();
        var ids = _invoices.Keys.ToList();

        var batches = Enumerable.Range(0, 3).Select(i => Task.Run(async () =>
        {
            await Approver($"approver-b{i}", bank).InitiateBatchDraftAsync(new InitiateBatchEftRequest { InvoiceIds = ids });
        }));
        var singles = ids.Select((id, i) => Task.Run(async () =>
        {
            try
            {
                await Approver($"approver-s{i}", bank).InitiateDraftAsync(new InitiateEftDraftRequest { InvoiceId = id, Method = EftMethod.Nacha });
            }
            catch (InvoiceDraftConflictException)
            {
                // The invoice was already taken by a batch.
            }
        }));
        await Task.WhenAll(batches.Concat(singles));

        var drafts = await AllDraftsAsync();
        drafts.GroupBy(d => d.InvoiceId).Should().HaveCount(6).And.OnlyContain(g => g.Count() == 1,
            "an invoice has at most one active draft, however the releases race");
        var debited = bank.Files.SelectMany(f => drafts.Where(d => d.NachaFileReference == f.FileReference)).ToList();
        debited.Select(d => d.InvoiceId).Should().OnlyHaveUniqueItems();
        bank.Files.Sum(f => NachaFileFacts.From(f.Content).EntryCount).Should().Be(debited.Count);
        drafts.Should().OnlyContain(d => d.Status == EftDraftStatus.Submitted || d.Status == EftDraftStatus.Pending);
    }

    [Fact]
    public async Task CreateAsync_ConcurrentDraftsOfOneInvoice_ExactlyOneIsCreated()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            try
            {
                await Drafts().CreateAsync(new EftDraft { InvoiceId = "inv-1", GroupNumber = "G", Amount = 10, Status = EftDraftStatus.Pending });
                return true;
            }
            catch (InvoiceDraftConflictException)
            {
                return false;
            }
        })));

        results.Count(r => r).Should().Be(1);
        (await AllDraftsAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Cancel_IsConditional_AndFreesTheInvoice()
    {
        var repository = Drafts();
        var draft = await repository.CreateAsync(new EftDraft { InvoiceId = "inv-1", GroupNumber = "G", Amount = 10, Status = EftDraftStatus.Pending });

        var cancels = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Drafts().TryCancelPendingAsync(draft.Id, "approver-2")));
        cancels.Count(c => c).Should().Be(1);
        var cancelled = (await repository.GetByIdAsync(draft.Id))!;
        cancelled.Status.Should().Be(EftDraftStatus.Cancelled);
        cancelled.ActiveInvoiceKey.Should().BeNull();
        cancelled.LastUpdatedBy.Should().Be("approver-2");

        // The invoice can be drafted again.
        await repository.CreateAsync(new EftDraft { InvoiceId = "inv-1", GroupNumber = "G", Amount = 10, Status = EftDraftStatus.Pending });
    }

    [Fact]
    public async Task Cancel_OfADraftNoLongerPending_ChangesNothing()
    {
        var repository = Drafts();
        var draft = await repository.CreateAsync(new EftDraft { InvoiceId = "inv-1", GroupNumber = "G", Amount = 10, Status = EftDraftStatus.Pending });
        (await repository.TryClaimForReleaseAsync(draft.Id, "claim-1", DateTime.UtcNow)).Should().BeTrue();

        (await repository.TryCancelPendingAsync(draft.Id, "approver-2")).Should().BeFalse();

        (await repository.GetByIdAsync(draft.Id))!.Status.Should().Be(EftDraftStatus.Releasing);
        await FluentActions.Awaiting(() => repository.CreateAsync(new EftDraft { InvoiceId = "inv-1", GroupNumber = "G", Amount = 10 }))
            .Should().ThrowAsync<InvoiceDraftConflictException>();
    }

    [Fact]
    public async Task CancelDraftAsync_ThroughTheService_FreesTheInvoiceForANewDraft()
    {
        SeedInvoices(1);
        var service = Approver("approver-1", new Bank());
        var draft = await service.InitiateDraftAsync(new InitiateEftDraftRequest { InvoiceId = "inv-0", Method = EftMethod.Nacha });
        await FluentActions.Awaiting(() => service.InitiateDraftAsync(new InitiateEftDraftRequest { InvoiceId = "inv-0", Method = EftMethod.Nacha }))
            .Should().ThrowAsync<InvoiceDraftConflictException>();

        (await service.CancelDraftAsync(draft.Id)).Status.Should().Be(EftDraftStatus.Cancelled);

        (await service.InitiateDraftAsync(new InitiateEftDraftRequest { InvoiceId = "inv-0", Method = EftMethod.Nacha }))
            .Status.Should().Be(EftDraftStatus.Pending);
    }

    [Fact]
    public async Task ADraftThatIsNoLongerActive_FreesTheInvoice()
    {
        var repository = Drafts();
        var draft = await repository.CreateAsync(new EftDraft { InvoiceId = "inv-1", GroupNumber = "G", Amount = 10, Status = EftDraftStatus.Submitted });
        draft.Status = EftDraftStatus.Settled;
        await repository.UpdateAsync(draft);

        await repository.CreateAsync(new EftDraft { InvoiceId = "inv-1", GroupNumber = "G", Amount = 10, Status = EftDraftStatus.Pending });
    }

    [Fact]
    public async Task ActiveDraftWrittenBeforeTheGuard_StillHoldsItsInvoice()
    {
        // A draft from before the guard existed: no ActiveInvoiceKey.
        var legacy = new EftDraft { TenantId = Tenant, InvoiceId = "inv-1", GroupNumber = "G", Amount = 10, Status = EftDraftStatus.Submitted };
        await _database.GetCollection<EftDraft>("eftDrafts").InsertOneAsync(legacy);
        await _database.GetCollection<EftDraft>("eftDrafts").UpdateOneAsync(d => d.Id == legacy.Id,
            Builders<EftDraft>.Update.Unset(d => d.ActiveInvoiceKey));

        await FluentActions.Awaiting(() => Drafts().CreateAsync(new EftDraft { InvoiceId = "inv-1", GroupNumber = "G", Amount = 10 }))
            .Should().ThrowAsync<InvoiceDraftConflictException>();
    }
}
