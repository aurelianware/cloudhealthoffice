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
/// Two approvers releasing the same Pending NACHA drafts at once must never put
/// a draft in two files at the bank (a double debit). The drafts are claimed
/// (Pending to Releasing, one conditional write each) before any file is
/// built. Against a real mongod and the real Mongo repository.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class NachaReleaseConcurrencyTests : IAsyncLifetime
{
    private const string Tenant = "tenant-race";

    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;

    public NachaReleaseConcurrencyTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("pb_nacha_race");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);

    /// <summary>The bank: records every file; can hold a send open until released.</summary>
    private sealed class Bank : INachaDispatcher
    {
        public ConcurrentBag<NachaTransmissionRequest> Files { get; } = new();
        public TaskCompletionSource InDispatch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Gate { get; init; }

        public async Task<NachaDispatchOutcome> DispatchAsync(NachaTransmissionRequest request, CancellationToken cancellationToken = default)
        {
            Files.Add(request);
            InDispatch.TrySetResult();
            if (Gate != null) await Gate.Task;
            else await Task.Delay(20, cancellationToken);
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

    private static HttpContextAccessor Request()
    {
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = Tenant;
        return new HttpContextAccessor { HttpContext = http };
    }

    /// <summary>One approver's request: its own request context and repository instance over the shared database.</summary>
    private EftDraftService Approver(string userId, INachaDispatcher bank)
    {
        var accessor = Request();
        var actor = new Mock<ICurrentActor>();
        actor.SetupGet(a => a.UserId).Returns(userId);
        actor.SetupGet(a => a.TenantId).Returns(Tenant);
        actor.SetupGet(a => a.IsAuthenticated).Returns(true);
        var accounts = new Mock<ISponsorBankAccountSource>();
        accounts.Setup(a => a.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SponsorBankAccountLookup.Found(new SponsorBankAccount
            {
                EftEnabled = true, RoutingNumber = "091000019", AccountNumber = "123456789",
                RoutingNumberLast4 = "0019", AccountNumberLast4 = "6789", AccountHolderName = "ACME"
            }));
        var config = Config();
        return new EftDraftService(
            new EftDraftRepositoryMongo(_database, accessor),
            Mock.Of<IPremiumInvoiceRepository>(), Mock.Of<IBillingRunRepository>(),
            new NachaFileService(config, Mock.Of<ILogger<NachaFileService>>()),
            Mock.Of<IStripeAchService>(), accounts.Object, bank, actor.Object, accessor, config,
            Mock.Of<ILogger<EftDraftService>>());
    }

    private async Task<List<string>> SeedPendingDraftsAsync(int count)
    {
        var repository = new EftDraftRepositoryMongo(_database, Request());
        var ids = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var draft = await repository.CreateAsync(new EftDraft
            {
                InvoiceId = $"inv-{i}", InvoiceNumber = $"INV-{i}", GroupNumber = $"GRP{i:000}", Amount = 100m + i,
                Method = EftMethod.Nacha, Status = EftDraftStatus.Pending, InitiatedBy = "maker-1",
            });
            ids.Add(draft.Id);
        }
        return ids;
    }

    private async Task<List<EftDraft>> DraftsAsync()
        => await _database.GetCollection<EftDraft>("eftDrafts").Find(_ => true).ToListAsync();

    [Fact]
    public async Task SecondRelease_WhileTheFirstIsSending_GetsNothing_AndOnlyOneFileReachesTheBank()
    {
        await SeedPendingDraftsAsync(3);
        var bank = new Bank { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };

        var first = Approver("approver-1", bank).GenerateNachaFileForPendingDraftsAsync();
        await bank.InDispatch.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // The first release is at the bank with all three drafts; the second sees none to send.
        var second = Approver("approver-2", bank).GenerateNachaFileForPendingDraftsAsync();
        var finished = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(10)));
        bank.Gate!.SetResult(); // never leave a send hanging, whatever happened
        finished.Should().BeSameAs(second, "the second release must not reach the bank while the first holds the drafts");
        // Nothing Pending left to it (400), or every draft it read claimed first (409): either way nothing sent.
        await FluentActions.Awaiting(() => second).Should().ThrowAsync<Exception>()
            .Where(e => e is NachaReleaseConflictException || e is InvalidOperationException);

        var result = await first;

        bank.Files.Should().ContainSingle();
        NachaFileFacts.From(bank.Files.Single().Content).EntryCount.Should().Be(3);
        result.Entries.Should().HaveCount(3);
        (await DraftsAsync()).Should().OnlyContain(d => d.Status == EftDraftStatus.Submitted && d.NachaFileReference == result.FileReference);
    }

    [Fact]
    public async Task ConcurrentReleases_PutEveryDraftInExactlyOneFile()
    {
        var ids = await SeedPendingDraftsAsync(6);
        var bank = new Bank();

        var releases = Enumerable.Range(0, 4)
            .Select(i => Task.Run(async () =>
            {
                try { return await Approver($"approver-{i}", bank).GenerateNachaFileForPendingDraftsAsync(); }
                catch (NachaReleaseConflictException) { return null; }
                catch (InvalidOperationException) { return null; } // none left Pending by the time it read
            }))
            .ToList();
        var results = (await Task.WhenAll(releases)).Where(r => r != null).ToList();

        // Six debits, six entries at the bank: no draft went out twice.
        bank.Files.Sum(f => NachaFileFacts.From(f.Content).EntryCount).Should().Be(6);
        results.SelectMany(r => r!.Entries.Select(e => e.DraftId)).Should().BeEquivalentTo(ids);
        (await DraftsAsync()).Should().OnlyContain(d => d.Status == EftDraftStatus.Submitted);
    }

    [Fact]
    public async Task Claim_IsExclusive_AndReleaseReturnsOnlyTheClaimHoldersDraft()
    {
        var id = (await SeedPendingDraftsAsync(1)).Single();
        var repository = new EftDraftRepositoryMongo(_database, Request());

        var claims = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(i => repository.TryClaimForReleaseAsync(id, $"claim-{i}", DateTime.UtcNow)));
        claims.Count(c => c).Should().Be(1);
        var holder = $"claim-{Array.IndexOf(claims, true)}";

        await repository.ReleaseClaimAsync(id, "someone-else", "not mine");
        (await repository.GetByIdAsync(id))!.Status.Should().Be(EftDraftStatus.Releasing);

        await repository.ReleaseClaimAsync(id, holder, "nothing was sent");
        var draft = (await repository.GetByIdAsync(id))!;
        draft.Status.Should().Be(EftDraftStatus.Pending);
        draft.ReleaseClaimId.Should().BeNull();
        draft.ErrorMessage.Should().Be("nothing was sent");
    }
}
