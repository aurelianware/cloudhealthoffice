using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.NachaTransmission;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using PremiumBillingService.Clients;
using PremiumBillingService.Models;
using PremiumBillingService.Repositories;
using PremiumBillingService.Services;

namespace PremiumBillingService.Tests.Repositories;

/// <summary>
/// Optimistic concurrency on the MongoDB EFT draft repository (EphemeralMongo).
/// The claim for release and the cancel were conditional, but the writes that
/// record an outcome (Stripe, NACHA transmission, settlement) replaced the whole
/// draft from an earlier read, so they could undo a cancel or a claim made in
/// between. Every write now bumps <see cref="EftDraft.Revision"/> and a replace
/// is conditional on it.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class EftDraftRepositoryMongoConcurrencyTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "tenant-draft-concurrency";
    private IMongoDatabase _db = null!;

    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("pb_eft_draft_concurrency");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

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

    private EftDraftRepositoryMongo Repo() => new(_db, Request());

    private IMongoCollection<EftDraft> Collection => _db.GetCollection<EftDraft>("eftDrafts");

    private async Task<EftDraft> SeedAsync(string invoiceId = "inv-1", EftMethod method = EftMethod.Nacha) => await Repo().CreateAsync(new EftDraft
    {
        InvoiceId = invoiceId, InvoiceNumber = "INV-" + invoiceId, GroupNumber = "GRP001", Amount = 100m,
        Method = method, Status = EftDraftStatus.Pending, InitiatedBy = "maker-1",
    });

    private async Task<EftDraft> ReadAsync(string id) => (await Repo().GetByIdAsync(id))!;

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

    [Fact]
    public async Task TwoConcurrentUpdates_FromTheSameRead_ExactlyOneWins_TheOtherConflicts()
    {
        var draft = await SeedAsync();
        var a = await ReadAsync(draft.Id);
        var b = await ReadAsync(draft.Id);
        a.ErrorMessage = "writer A";
        b.ErrorMessage = "writer B";

        var outcomes = await Task.WhenAll(Try(() => Repo().UpdateAsync(a)), Try(() => Repo().UpdateAsync(b)));

        outcomes.Count(o => o is null).Should().Be(1, "exactly one write wins");
        outcomes.Count(o => o is EftDraftConcurrencyException).Should().Be(1, "the other gets a conflict, not a silent overwrite");
        outcomes.OfType<EftDraftConcurrencyException>().Single()
            .Should().BeAssignableTo<CloudHealthOffice.Infrastructure.Data.ConcurrencyConflictException>("the shared middleware answers it with 409");
        var stored = await ReadAsync(draft.Id);
        stored.ErrorMessage.Should().BeOneOf("writer A", "writer B");
        stored.Revision.Should().Be(1);
    }

    [Fact]
    public async Task AReplaceBasedOnAReadBeforeACancel_Conflicts_InsteadOfUndoingIt()
    {
        var draft = await SeedAsync();
        var stale = await ReadAsync(draft.Id);

        (await Repo().TryCancelPendingAsync(draft.Id, "approver-2")).Should().BeTrue();

        stale.Status = EftDraftStatus.Submitted;
        var act = () => Repo().UpdateAsync(stale);

        await act.Should().ThrowAsync<EftDraftConcurrencyException>();
        stale.Revision.Should().Be(0, "a failed write leaves the copy at the revision it was read at");
        var stored = await ReadAsync(draft.Id);
        stored.Status.Should().Be(EftDraftStatus.Cancelled, "the cancel is not undone");
        stored.ActiveInvoiceKey.Should().BeNull("the invoice stays freed");
    }

    [Fact]
    public async Task AReplaceBasedOnAReadBeforeAClaimForRelease_Conflicts_InsteadOfUndoingIt()
    {
        var draft = await SeedAsync();
        var stale = await ReadAsync(draft.Id);

        (await Repo().TryClaimForReleaseAsync(draft.Id, "claim-1", DateTime.UtcNow, "approver-1")).Should().BeTrue();

        stale.ErrorMessage = "edited from a stale read";
        var act = () => Repo().UpdateAsync(stale);

        await act.Should().ThrowAsync<EftDraftConcurrencyException>();
        var stored = await ReadAsync(draft.Id);
        stored.Status.Should().Be(EftDraftStatus.Releasing, "the claim is not undone");
        stored.ReleaseClaimId.Should().Be("claim-1");
    }

    [Fact]
    public async Task AReplaceBasedOnAReadBeforeAClaimIsReleased_Conflicts_InsteadOfUndoingIt()
    {
        var draft = await SeedAsync();
        await Repo().TryClaimForReleaseAsync(draft.Id, "claim-1", DateTime.UtcNow, "approver-1");
        var stale = await ReadAsync(draft.Id);

        await Repo().ReleaseClaimAsync(draft.Id, "claim-1", "not sent");

        stale.Status = EftDraftStatus.Submitted;
        await ((Func<Task>)(() => Repo().UpdateAsync(stale))).Should().ThrowAsync<EftDraftConcurrencyException>();
        (await ReadAsync(draft.Id)).Status.Should().Be(EftDraftStatus.Pending);
    }

    [Fact]
    public async Task EveryTargetedWrite_BumpsTheRevision()
    {
        var draft = await SeedAsync();
        (await ReadAsync(draft.Id)).Revision.Should().Be(0, "a new draft starts at the revision the conditional matches");

        await Repo().TryClaimForReleaseAsync(draft.Id, "claim-1", DateTime.UtcNow, "approver-1");
        (await ReadAsync(draft.Id)).Revision.Should().Be(1);

        await Repo().ReleaseClaimAsync(draft.Id, "claim-1", "back");
        (await ReadAsync(draft.Id)).Revision.Should().Be(2);

        await Repo().TryCancelPendingAsync(draft.Id, "approver-2");
        (await ReadAsync(draft.Id)).Revision.Should().Be(3);
    }

    [Fact]
    public async Task ADraftWrittenBeforeTheRevisionExisted_UpdatesOnce_ThenCarriesARevision()
    {
        var draft = await SeedAsync();
        await _db.GetCollection<BsonDocument>("eftDrafts").UpdateOneAsync(
            new BsonDocument("_id", draft.Id), new BsonDocument("$unset", new BsonDocument(nameof(EftDraft.Revision), "")));
        var legacy = await ReadAsync(draft.Id);
        var legacyAgain = await ReadAsync(draft.Id);
        legacy.Revision.Should().Be(0);
        legacy.ErrorMessage = "first write after the upgrade";

        await Repo().UpdateAsync(legacy);

        (await ReadAsync(draft.Id)).Revision.Should().Be(1);
        legacyAgain.ErrorMessage = "second write from the same legacy read";
        await ((Func<Task>)(() => Repo().UpdateAsync(legacyAgain))).Should().ThrowAsync<EftDraftConcurrencyException>();
        (await ReadAsync(draft.Id)).ErrorMessage.Should().Be("first write after the upgrade");
    }

    [Fact]
    public async Task ALegacyDraftWithoutRevision_CanBeClaimed_AndTheClaimStartsItsRevision()
    {
        var draft = await SeedAsync();
        await _db.GetCollection<BsonDocument>("eftDrafts").UpdateOneAsync(
            new BsonDocument("_id", draft.Id), new BsonDocument("$unset", new BsonDocument(nameof(EftDraft.Revision), "")));

        (await Repo().TryClaimForReleaseAsync(draft.Id, "claim-1", DateTime.UtcNow, "approver-1")).Should().BeTrue();

        (await ReadAsync(draft.Id)).Revision.Should().Be(1);
    }

    [Fact]
    public async Task UpdatingADraftThatDoesNotExist_WritesNothing_AndDoesNotThrow()
    {
        var ghost = new EftDraft { TenantId = Tenant, InvoiceId = "inv-ghost", Status = EftDraftStatus.Pending };

        await Repo().UpdateAsync(ghost);

        (await Repo().GetByIdAsync(ghost.Id)).Should().BeNull();
    }

    // --- EftDraftService over the real repository ---

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

    private static PremiumInvoice Invoice() => new()
    {
        Id = "inv-1", TenantId = Tenant, InvoiceNumber = "INV-1", GroupNumber = "GRP001", SponsorName = "ACME",
        Status = InvoiceStatus.Sent, TotalAmount = 100m, BalanceDue = 100m, CreatedBy = "maker-1",
    };

    private EftDraftService Service(string userId, IStripeAchService stripe, INachaDispatcher? bank = null)
    {
        var accessor = Request();
        var actor = new Mock<ICurrentActor>();
        actor.SetupGet(a => a.UserId).Returns(userId);
        actor.SetupGet(a => a.TenantId).Returns(Tenant);
        actor.SetupGet(a => a.IsAuthenticated).Returns(true);
        var invoices = new Mock<IPremiumInvoiceRepository>();
        invoices.Setup(r => r.GetByIdAsync(It.IsAny<string>())).ReturnsAsync(Invoice());
        var accounts = new Mock<ISponsorBankAccountSource>();
        accounts.Setup(a => a.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SponsorBankAccountLookup.Found(new SponsorBankAccount
            {
                EftEnabled = true, PreferredMethod = EftMethod.StripeAch, RoutingNumber = "091000019", AccountNumber = "123456789",
                StripeCustomerId = "cus_1", StripePaymentMethodId = "pm_1",
                RoutingNumberLast4 = "0019", AccountNumberLast4 = "6789", AccountHolderName = "ACME"
            }));
        var config = Config();
        return new EftDraftService(
            new EftDraftRepositoryMongo(_db, accessor),
            invoices.Object, Mock.Of<IBillingRunRepository>(),
            new NachaFileService(config, Mock.Of<ILogger<NachaFileService>>()),
            stripe, accounts.Object, bank ?? Mock.Of<INachaDispatcher>(), actor.Object, accessor, config,
            Mock.Of<ILogger<EftDraftService>>());
    }

    /// <summary>Stripe debits; while it is being called, <paramref name="meanwhile"/> writes the draft from another request.</summary>
    private static IStripeAchService StripeThatSucceedsAfter(Func<string, Task> meanwhile)
    {
        var stripe = new Mock<IStripeAchService>();
        stripe.Setup(s => s.CreateAchDraftAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(async (string _, string _, decimal amount, string invoiceNumber, string _, string draftId) =>
            {
                await meanwhile(draftId);
                return new StripeAchDraftResult { PaymentIntentId = "pi_1", Status = "processing", Amount = amount, InvoiceNumber = invoiceNumber };
            });
        return stripe.Object;
    }

    [Fact]
    public async Task AStripeOutcome_FromTheDraftAsCreated_CannotUndoAConcurrentCancel()
    {
        var stripe = StripeThatSucceedsAfter(async draftId =>
            (await Repo().TryCancelPendingAsync(draftId, "approver-2")).Should().BeTrue());

        var act = () => Service("approver-1", stripe).InitiateDraftAsync(new InitiateEftDraftRequest { InvoiceId = "inv-1", Method = EftMethod.StripeAch });

        await act.Should().ThrowAsync<EftDraftConcurrencyException>("the outcome write is from the copy read before the cancel (409)");
        var stored = (await Collection.Find(_ => true).ToListAsync()).Single();
        stored.Status.Should().Be(EftDraftStatus.Cancelled, "the cancel is not undone");
        stored.StripePaymentIntentId.Should().BeNull();
        stored.ActiveInvoiceKey.Should().BeNull();
    }

    [Fact]
    public async Task AStripeOutcome_FromTheDraftAsCreated_CannotUndoAConcurrentClaimForRelease()
    {
        var stripe = StripeThatSucceedsAfter(async draftId =>
            (await Repo().TryClaimForReleaseAsync(draftId, "claim-1", DateTime.UtcNow, "approver-2")).Should().BeTrue());

        var act = () => Service("approver-1", stripe).InitiateDraftAsync(new InitiateEftDraftRequest { InvoiceId = "inv-1", Method = EftMethod.StripeAch });

        await act.Should().ThrowAsync<EftDraftConcurrencyException>();
        var stored = (await Collection.Find(_ => true).ToListAsync()).Single();
        stored.Status.Should().Be(EftDraftStatus.Releasing, "the claim is not undone");
        stored.ReleaseClaimId.Should().Be("claim-1");
    }

    /// <summary>The bank: while a file is being sent, runs <see cref="Meanwhile"/> (another request writing the drafts).</summary>
    private sealed class Bank : INachaDispatcher
    {
        public Func<Task> Meanwhile { get; init; } = () => Task.CompletedTask;

        public async Task<NachaDispatchOutcome> DispatchAsync(NachaTransmissionRequest request, CancellationToken cancellationToken = default)
        {
            await Meanwhile();
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

    [Fact]
    public async Task ANachaOutcome_CannotUndoAConcurrentReleaseOfTheClaim()
    {
        var draft = await SeedAsync();
        var bank = new Bank
        {
            Meanwhile = async () =>
            {
                // Someone resolved the draft as not sent while the file was out.
                var held = await ReadAsync(draft.Id);
                await Repo().ReleaseClaimAsync(draft.Id, held.ReleaseClaimId!, "resolved meanwhile");
            }
        };

        var act = () => Service("approver-1", Mock.Of<IStripeAchService>(), bank).GenerateNachaFileForPendingDraftsAsync();

        await act.Should().ThrowAsync<EftDraftConcurrencyException>("the draft is no longer held by this release");
        (await ReadAsync(draft.Id)).Status.Should().Be(EftDraftStatus.Pending, "the other decision is not undone");
    }

    [Fact]
    public async Task ANachaOutcome_IsReappliedOverAConcurrentWrite_WhileTheReleaseStillHoldsTheDraft()
    {
        var draft = await SeedAsync();
        var bank = new Bank
        {
            // Another write that leaves the draft held by this release.
            Meanwhile = () => Collection.UpdateOneAsync(d => d.Id == draft.Id, Builders<EftDraft>.Update.Inc(d => d.Revision, 1L))
        };

        var result = await Service("approver-1", Mock.Of<IStripeAchService>(), bank).GenerateNachaFileForPendingDraftsAsync();

        result.Entries.Should().ContainSingle();
        var stored = await ReadAsync(draft.Id);
        stored.Status.Should().Be(EftDraftStatus.Submitted);
        stored.NachaFileReference.Should().Be(result.FileReference);
        stored.AccountNumberLast4.Should().Be("6789");
    }
}
