using System.Net;
using System.Net.Http.Json;
using CloudHealthOffice.PaymentService.Tests.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PaymentService.Models;
using PaymentService.Repositories;
using PaymentService.Services;

namespace CloudHealthOffice.PaymentService.Tests;

/// <summary>
/// Payment runs executed end to end in memory: real payment and run stores,
/// the real batched 835 generator, the real receivable ledger, a stubbed
/// claims-service and trading-partner lookup, and a scripted provider-service
/// payee-account source.
/// </summary>
internal sealed class FfsRunHarness
{
    public const string Tenant = "test-tenant";
    public const string CompanyId = "1123456789";

    public InMemoryPaymentRepository Payments { get; } = new(Tenant);
    public InMemoryPaymentRunRepository Runs { get; } = new(Tenant);
    public InMemoryClaimReservationRepository Reservations { get; } = new();
    public InMemoryNachaFileIdModifierAllocator Modifiers { get; } = new();
    public InMemoryProviderReceivableRepository LedgerStore { get; } = new();
    public ScriptedPayeeAccounts Accounts { get; } = new();
    public List<EraEnvelopeRecord> Envelopes { get; } = new();
    public StubHttpHandler Claims { get; } = new();
    public IConfiguration Configuration { get; }
    public ProviderReceivableLedger Ledger { get; }
    public TestActor Approver { get; } = TestActor.Approver();

    private readonly IEraEnvelopeRepository _envelopes = Substitute.For<IEraEnvelopeRepository>();
    private readonly ITradingPartnersClient _partners = Substitute.For<ITradingPartnersClient>();
    private readonly ICarcRarcMappingService _mapper = Substitute.For<ICarcRarcMappingService>();
    private readonly IHttpClientFactory _http = Substitute.For<IHttpClientFactory>();
    private int _runSequence;
    private int _nextCheck = 1000000;

    public FfsRunHarness(IProviderReceivableRepository? ledgerStore = null)
    {
        Configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Era:InterchangeSenderId"] = "SENDER",
                ["Era:InterchangeReceiverId"] = "RECEIVER",
                ["Payer:Name"] = "Cloud Health Office",
                ["Payer:Id"] = "CHO",
                ["Era:OriginatingCompanyId"] = CompanyId,
                ["Era:PayerRoutingNumber"] = "091000019",
                ["Era:PayerAccountNumber"] = "123456789",
                ["Era:PayeeRoutingNumber"] = "021000021",
                ["Era:PayeeAccountNumber"] = "555566667777",
                ["TradingPartners:Environment"] = "Production",
                ["Nacha:ImmediateDestination"] = "091000019",
                ["Nacha:ImmediateDestinationName"] = "FIRST BANK",
                ["Nacha:CompanyName"] = "CLOUD HEALTH OFC",
            })
            .Build();
        Ledger = new ProviderReceivableLedger(ledgerStore ?? LedgerStore, Configuration, NullLogger<ProviderReceivableLedger>.Instance);

        _http.CreateClient("ClaimsService").Returns(_ => new HttpClient(Claims, disposeHandler: false) { BaseAddress = new Uri("http://claims-service") });
        _mapper.MapClaimAdjustments(Arg.Any<ClaimAdjudicationSnapshot>()).Returns(Array.Empty<ClaimAdjustment>());
        _mapper.MapLineAdjustments(Arg.Any<ClaimAdjudicationSnapshot>())
            .Returns(new Dictionary<int, IReadOnlyList<ServiceLineAdjustment>>());
        _envelopes.CreateAsync(Arg.Do<EraEnvelopeRecord>(Envelopes.Add)).Returns(call =>
        {
            var rec = call.Arg<EraEnvelopeRecord>();
            rec.Id = "env-" + Envelopes.Count;
            return rec;
        });
        _envelopes.GetClaimIdsWithEnvelopeAsync(default!, default).ReturnsForAnyArgs(Array.Empty<string>());
    }

    /// <summary>Routes a billing NPI to a trading partner.</summary>
    public void Partner(string npi, string tradingPartnerId)
        => _partners.GetByBillingProviderNpiAsync(Tenant, npi, "Production")
            .Returns(new TradingPartnerSummary { TradingPartnerId = tradingPartnerId, X12Config = new X12ConfigDto() });

    public static ClaimDto Claim(string id, string npi, decimal paid) => new()
    {
        Id = id,
        ClaimNumber = "CLM-" + id,
        BillingProviderNPI = npi,
        ProviderName = "Provider " + npi,
        TotalChargeAmount = paid + 20m,
        MemberId = "m-" + id,
        Status = ClaimStatus.Approved,
        AdjudicationResult = new ClaimAdjudicationDto { PayerPayment = paid },
        ServiceLines = new List<ClaimServiceLineDto>
        {
            new() { LineNumber = 1, ProcedureCode = "99213", ChargeAmount = paid + 20m, PaidAmount = paid, Units = 1 },
        },
    };

    public PaymentRunService Service(IProviderPayeeAccountSource? accounts = null) => new(
        Payments, Runs, new BatchEraGeneratorService(NullLogger<BatchEraGeneratorService>.Instance),
        _mapper, _envelopes, _partners, _http, NullLogger<PaymentRunService>.Instance, Configuration,
        Approver, Approver.SeparationOfDuties(), Reservations, Ledger, accounts ?? Accounts);

    public FfsEftFileService EftFiles(IProviderPayeeAccountSource? accounts = null)
        => new(Runs, Payments, accounts ?? Accounts, Configuration, Modifiers, NullLogger<FfsEftFileService>.Instance);

    /// <summary>Creates and executes one ACH payment run paying <paramref name="claims"/>.</summary>
    public async Task<PaymentRun> ExecuteRunAsync(params ClaimDto[] claims)
    {
        Claims.NextResponse = req =>
            req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.StartsWith("/api/claims/search")
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(req.RequestUri.Query.Contains("status=5") ? claims.ToList() : new List<ClaimDto>()),
                }
                : new HttpResponseMessage(HttpStatusCode.OK);

        var sequence = ++_runSequence;
        var run = await Runs.CreateAsync(new PaymentRun
        {
            Id = "run-" + sequence,
            TenantId = Tenant,
            PaymentRunNumber = $"PR-2026050{sequence}-RUN{sequence}",
            Status = PaymentRunStatus.Pending,
            CreatedBy = "maker-1",
            Criteria = new PaymentRunCriteria { GroupByProvider = true },
            NextCheckNumber = _nextCheck,
            PaymentMethod = "ACH",
            PaymentDate = new DateTime(2026, 5, 4 + sequence, 0, 0, 0, DateTimeKind.Utc),
        });
        var executed = await Service().ExecutePaymentRunAsync(run.Id);
        _nextCheck = executed.NextCheckNumber;
        return executed;
    }

    public static List<string[]> Segments(EraEnvelopeRecord envelope)
        => envelope.EdiContent.Split('~', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Split('*')).ToList();

    public async Task<ProviderReceivableRecord> SeedReceivableAsync(string npi, decimal owed, string trace = "R-ABC12345", string envelopeId = "env-rev-1")
        => await Ledger.RecordForwardBalanceAsync(
            Tenant, npi, "TP-A",
            new ReversalRun { Id = "rr-" + envelopeId, ReversalRunNumber = "RR-" + envelopeId, TenantId = Tenant },
            envelopeId, trace, owed, "approver-0");
}

/// <summary>provider-service's payee read, scripted per NPI.</summary>
internal sealed class ScriptedPayeeAccounts : IProviderPayeeAccountSource
{
    private readonly Dictionary<string, PayeeAccountLookup> _answers = new(StringComparer.Ordinal);

    public bool IsConfigured => true;

    public List<string> Requests { get; } = new();

    public void Eft(string npi, string routing = "021000021", string account = "111122223333", string tin = "12-3456789", bool savings = false, string? holder = "Sunrise Clinic")
        => _answers[npi] = PayeeAccountLookup.Eft(new PayeeEftAccount
        {
            RoutingNumber = routing, AccountNumber = account, TaxId = tin, IsSavings = savings, AccountHolderName = holder,
        });

    public void NoEft(string npi, string reason = "no approved bank account") => _answers[npi] = PayeeAccountLookup.NoEft(reason);

    public void Unavailable(string npi) => _answers[npi] = PayeeAccountLookup.Unavailable("provider-service answered 503");

    public Task<PayeeAccountLookup> GetAsync(string tenantId, string providerNpi, CancellationToken cancellationToken = default)
    {
        Requests.Add(providerNpi);
        return Task.FromResult(_answers.TryGetValue(providerNpi, out var answer)
            ? answer
            : PayeeAccountLookup.NoEft("no approved bank account"));
    }
}
