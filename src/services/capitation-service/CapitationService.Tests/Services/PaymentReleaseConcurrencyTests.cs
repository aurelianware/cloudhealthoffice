using System.Collections.Concurrent;
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
/// Two approvers releasing the same capitation payments at once must never pay
/// a provider twice: a statement goes Approved to PaymentInitiated, and a
/// Pending NACHA disbursement goes to Releasing, each by one conditional write
/// before any money moves or any file is built. Against a real mongod and the
/// real Mongo repositories.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class PaymentReleaseConcurrencyTests : IAsyncLifetime
{
    private const string Tenant = "tenant-race";
    private const string Npi = "1234567890";

    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;

    public PaymentReleaseConcurrencyTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("cap_release_race");
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

    /// <summary>
    /// One request's context. Not <see cref="HttpContextAccessor"/>: its AsyncLocal
    /// holder is cleared for every flow when another request sets a context, and
    /// these tests run several requests at once.
    /// </summary>
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

    private CapitationStatementRepositoryMongo Statements(IHttpContextAccessor accessor)
        => new(_database, accessor, Mock.Of<ILogger<CapitationStatementRepositoryMongo>>());

    /// <summary>One approver's request: its own request context and repositories over the shared database.</summary>
    private CapitationDisbursementService Approver(INachaDispatcher bank, IStripeConnectService? stripe = null, string method = "NachaCredit")
    {
        var accessor = Request();
        var handler = new MockHttpMessageHandler<ProviderBankAccountDto>(_ => new ProviderBankAccountDto
        {
            EftEnabled = true, PreferredDisbursementMethod = method, RoutingNumber = "091000019", AccountNumber = "123456789",
            AccountType = "Checking", AccountHolderName = "Dr. Smith", StripeConnectedAccountId = "acct_1",
            RoutingNumberLast4 = "0019", AccountNumberLast4 = "6789",
        });
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("ProviderService"))
            .Returns(() => new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://provider-service") });
        var config = Config();
        return new CapitationDisbursementService(
            new CapitationDisbursementRepositoryMongo(_database, accessor),
            Statements(accessor),
            Mock.Of<ICapitationRunRepository>(),
            new NachaCreditFileService(config, Mock.Of<ILogger<NachaCreditFileService>>()),
            stripe ?? Mock.Of<IStripeConnectService>(),
            factory.Object, config,
            TestSeparationOfDuties.Create(tenantId: Tenant),
            new FactoryBackedProviderBankAccountSource(factory.Object),
            bank, Mock.Of<ILogger<CapitationDisbursementService>>(), accessor);
    }

    private async Task SeedStatementAsync(string id, CapitationStatementStatus status)
        => await Statements(Request()).CreateAsync(new CapitationStatement
        {
            Id = id, TenantId = Tenant, StatementNumber = $"CAPSTMT-{id}", CapitationRunId = "run-1",
            ProviderNPI = Npi, ProviderName = "Dr. Smith", Status = status, NetPayable = 500m, GrossCapitation = 500m,
            CreatedBy = "maker-1", RunCreatedBy = "maker-1",
        });

    private async Task<List<string>> SeedPendingNachaDisbursementsAsync(int count)
    {
        var repository = new CapitationDisbursementRepositoryMongo(_database, Request());
        var ids = new List<string>();
        for (var i = 0; i < count; i++)
        {
            await SeedStatementAsync($"s-{i}", CapitationStatementStatus.PaymentInitiated);
            var d = await repository.CreateAsync(new CapitationDisbursement
            {
                StatementId = $"s-{i}", StatementNumber = $"CAPSTMT-s-{i}", ProviderNPI = Npi, ProviderName = "Dr. Smith",
                Amount = 100m + i, Method = DisbursementMethod.NachaCredit, Status = DisbursementStatus.Pending,
            });
            ids.Add(d.Id);
        }
        return ids;
    }

    private async Task<List<CapitationDisbursement>> DisbursementsAsync()
        => await _database.GetCollection<CapitationDisbursement>("capitation-disbursements").Find(_ => true).ToListAsync();

    [Fact]
    public async Task SecondNachaRelease_WhileTheFirstIsSending_GetsNothing_AndOnlyOneFileReachesTheBank()
    {
        await SeedPendingNachaDisbursementsAsync(3);
        var bank = new Bank { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };

        var first = Approver(bank).GenerateNachaCreditFileAsync("approver-1");
        await bank.InDispatch.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var second = Approver(bank).GenerateNachaCreditFileAsync("approver-2");
        var finished = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(10)));
        bank.Gate!.SetResult(); // never leave a send hanging, whatever happened
        finished.Should().BeSameAs(second, "the second release must not reach the bank while the first holds the disbursements");
        // Nothing Pending left to it (400), or every one it read claimed first (409): either way nothing sent.
        await FluentActions.Awaiting(() => second).Should().ThrowAsync<Exception>()
            .Where(e => e is PaymentReleaseConflictException || e is InvalidOperationException);

        var result = await first;
        bank.Files.Should().ContainSingle();
        NachaFileFacts.From(bank.Files.Single().Content).EntryCount.Should().Be(3);
        result.Entries.Should().HaveCount(3);
        (await DisbursementsAsync()).Should().OnlyContain(d => d.Status == DisbursementStatus.Submitted);
    }

    [Fact]
    public async Task ConcurrentNachaReleases_PutEveryDisbursementInExactlyOneFile()
    {
        var ids = await SeedPendingNachaDisbursementsAsync(6);
        var bank = new Bank();

        var results = (await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Task.Run(async () =>
        {
            try { return await Approver(bank).GenerateNachaCreditFileAsync($"approver-{i}"); }
            catch (PaymentReleaseConflictException) { return null; }
            catch (InvalidOperationException) { return null; } // none left Pending by the time it read
        })))).Where(r => r != null).ToList();

        bank.Files.Sum(f => NachaFileFacts.From(f.Content).EntryCount).Should().Be(6);
        results.SelectMany(r => r!.Entries.Select(e => e.DisbursementId)).Should().BeEquivalentTo(ids);
        (await DisbursementsAsync()).Should().OnlyContain(d => d.Status == DisbursementStatus.Submitted);
    }

    [Fact]
    public async Task ConcurrentDisbursementsOfOneStatement_TransferTheMoneyOnce()
    {
        await SeedStatementAsync("s-1", CapitationStatementStatus.Approved);
        var transfers = 0;
        var stripe = new Mock<IStripeConnectService>();
        stripe.Setup(s => s.CreateTransferAsync(It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(async () =>
            {
                Interlocked.Increment(ref transfers);
                await Task.Delay(20);
                return new StripeTransferResult { TransferId = $"tr_{Guid.NewGuid():N}", Status = "created" };
            });

        var attempts = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => Task.Run(async () =>
        {
            try
            {
                await Approver(new Bank(), stripe.Object, "StripeConnect").InitiateDisbursementAsync(new InitiateDisbursementRequest
                {
                    StatementId = "s-1", Method = DisbursementMethod.StripeConnect, InitiatedBy = $"approver-{i}",
                });
                return "paid";
            }
            catch (PaymentReleaseConflictException) { return "conflict"; }
            catch (InvalidOperationException) { return "not-approved"; } // read it after another release took it
        })));

        transfers.Should().Be(1);
        attempts.Count(a => a == "paid").Should().Be(1);
        var statement = await Statements(Request()).GetByIdAsync("s-1");
        statement!.Status.Should().Be(CapitationStatementStatus.PaymentInitiated);
        (await DisbursementsAsync()).Should().ContainSingle().Which.Id.Should().Be(statement.EftDisbursementId);
    }

    [Fact]
    public async Task StartPayment_IsExclusive_AndOnlyItsHolderCanUndoIt()
    {
        await SeedStatementAsync("s-1", CapitationStatementStatus.Approved);
        var statements = Statements(Request());

        var starts = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => statements.TryStartPaymentAsync("s-1", $"d-{i}")));
        starts.Count(s => s).Should().Be(1);
        var holder = $"d-{Array.IndexOf(starts, true)}";

        await statements.UndoStartPaymentAsync("s-1", "someone-else");
        (await statements.GetByIdAsync("s-1"))!.Status.Should().Be(CapitationStatementStatus.PaymentInitiated);

        await statements.UndoStartPaymentAsync("s-1", holder);
        var statement = (await statements.GetByIdAsync("s-1"))!;
        statement.Status.Should().Be(CapitationStatementStatus.Approved);
        statement.EftDisbursementId.Should().BeNull();
    }

    private Mock<IStripeConnectService> StripeThat(Func<Task<StripeTransferResult>> answer, List<string>? disbursementIds = null)
    {
        var stripe = new Mock<IStripeConnectService>();
        stripe.Setup(s => s.CreateTransferAsync(It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, decimal, string, string, string, string>((_, _, _, _, _, id) => disbursementIds?.Add(id))
            .Returns(answer);
        return stripe;
    }

    public static TheoryData<string> UnknownOutcomes() => new() { "http", "timeout", "io", "stripe-5xx" };

    private static Exception UnknownOutcome(string kind) => kind switch
    {
        "http" => new HttpRequestException("connection reset after the request was sent"),
        "timeout" => new TaskCanceledException("Stripe did not answer in time"),
        "io" => new IOException("broken pipe"),
        _ => new Stripe.StripeException(System.Net.HttpStatusCode.InternalServerError, new Stripe.StripeError { Type = "api_error" }, "Stripe error"),
    };

    [Theory]
    [MemberData(nameof(UnknownOutcomes))]
    public async Task StripeTransferWhoseOutcomeIsUnknown_LeavesTheStatementNonPayable_AndIsNeverPaidAgain(string kind)
    {
        await SeedStatementAsync("s-1", CapitationStatementStatus.Approved);
        var ids = new List<string>();
        var stripe = StripeThat(() => throw UnknownOutcome(kind), ids);

        var disbursement = await Approver(new Bank(), stripe.Object, "StripeConnect").InitiateDisbursementAsync(new InitiateDisbursementRequest
        {
            StatementId = "s-1", Method = DisbursementMethod.StripeConnect, InitiatedBy = "approver-1",
        });

        disbursement.Status.Should().Be(DisbursementStatus.PaymentUnknown);
        ids.Should().ContainSingle().Which.Should().Be(disbursement.Id, "the idempotency key is derived from the disbursement id");
        var statement = (await Statements(Request()).GetByIdAsync("s-1"))!;
        statement.Status.Should().Be(CapitationStatementStatus.PaymentUnknown, "the transfer may exist: the statement must not be payable");
        statement.EftDisbursementId.Should().Be(disbursement.Id);
        (await DisbursementsAsync()).Should().ContainSingle().Which.Status.Should().Be(DisbursementStatus.PaymentUnknown);

        // Neither a second release nor the batch path pays it again.
        await FluentActions.Awaiting(() => Approver(new Bank(), stripe.Object, "StripeConnect").InitiateDisbursementAsync(new InitiateDisbursementRequest
        {
            StatementId = "s-1", Method = DisbursementMethod.StripeConnect, InitiatedBy = "approver-2",
        })).Should().ThrowAsync<InvalidOperationException>();
        var batch = await Approver(new Bank(), stripe.Object, "StripeConnect").InitiateBatchDisbursementAsync(new InitiateBatchDisbursementRequest
        {
            StatementIds = new List<string> { "s-1" }, Method = DisbursementMethod.StripeConnect, InitiatedBy = "approver-2",
        });
        batch.DisbursementsInitiated.Should().Be(0);
        stripe.Verify(s => s.CreateTransferAsync(It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        (await Statements(Request()).GetByIdAsync("s-1"))!.Status.Should().Be(CapitationStatementStatus.PaymentUnknown);
    }

    [Fact]
    public async Task BatchStripeTransferWhoseOutcomeIsUnknown_IsReportedForChecking_AndTheStatementStaysNonPayable()
    {
        await SeedStatementAsync("s-1", CapitationStatementStatus.Approved);
        var stripe = StripeThat(() => throw new HttpRequestException("reset"));

        var batch = await Approver(new Bank(), stripe.Object, "StripeConnect").InitiateBatchDisbursementAsync(new InitiateBatchDisbursementRequest
        {
            StatementIds = new List<string> { "s-1" }, Method = DisbursementMethod.StripeConnect, InitiatedBy = "approver-1",
        });

        batch.DisbursementsInitiated.Should().Be(0);
        batch.NeedsAttention.Should().ContainSingle();
        (await Statements(Request()).GetByIdAsync("s-1"))!.Status.Should().Be(CapitationStatementStatus.PaymentUnknown);
        (await DisbursementsAsync()).Should().ContainSingle().Which.Status.Should().Be(DisbursementStatus.PaymentUnknown);
    }

    [Fact]
    public async Task StripeRefusal_MakesTheStatementPayableAgain()
    {
        await SeedStatementAsync("s-1", CapitationStatementStatus.Approved);
        var stripe = StripeThat(() => Task.FromResult(new StripeTransferResult { Status = "failed", ErrorMessage = "No such destination" }));

        var disbursement = await Approver(new Bank(), stripe.Object, "StripeConnect").InitiateDisbursementAsync(new InitiateDisbursementRequest
        {
            StatementId = "s-1", Method = DisbursementMethod.StripeConnect, InitiatedBy = "approver-1",
        });

        disbursement.Status.Should().Be(DisbursementStatus.Failed);
        var statement = (await Statements(Request()).GetByIdAsync("s-1"))!;
        statement.Status.Should().Be(CapitationStatementStatus.Approved);
        statement.EftDisbursementId.Should().BeNull();
    }
}
