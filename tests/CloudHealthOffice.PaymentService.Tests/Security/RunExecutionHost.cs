using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using PaymentService.Models;
using PaymentService.Repositories;
using PaymentService.Services;

namespace CloudHealthOffice.PaymentService.Tests.Security;

/// <summary>
/// The real payment-service pipeline (authentication, controllers, run services,
/// separation of duties, 835 generator, run-execution clients) with in-memory
/// stores, a stand-in claims-service and a stand-in trading-partner-service.
/// Both stand-ins validate the bearer token against the development issuers and
/// enforce permissions the way the real services do: a user token needs
/// claims:read / claims:work / claims:void (or trading-partners:read); a service
/// token from the service issuer passes.
/// </summary>
public sealed class RunExecutionHost : WebApplicationFactory<Program>
{
    public const string Tenant = "tenant-pay";
    public const string ServiceClientId = "payment-service";

    public InMemoryPaymentRepository Payments { get; } = new(Tenant);
    public InMemoryPaymentRunRepository Runs { get; } = new(Tenant);
    public InMemoryReversalRunRepository ReversalRuns { get; } = new(Tenant);
    public RecordingEnvelopeRepository Envelopes { get; } = new(Tenant);
    public InMemoryClaimReservationRepository Reservations { get; } = new();
    public StandInClaimsService Claims { get; } = new();
    public StandInTradingPartnerService TradingPartners { get; } = new();
    public InMemoryReservationAuditLog Audit { get; } = new();
    public ManualClock Clock { get; } = new();

    /// <summary>
    /// Records the payment a payment run made for a claim the stand-in holds
    /// (once): a reversal recoups that recorded amount, so a reversal needs it.
    /// </summary>
    public Payment SeedOriginalPayment(string claimId)
    {
        var existing = Payments.All.FirstOrDefault(p => !p.IsReversal && p.ClaimPayments.Any(cp => cp.ClaimId == claimId));
        if (existing != null)
            return existing;

        var claim = Claims.Get(claimId).Dto;
        var paid = claim.PlanPaidAmount ?? 0m;
        return Payments.CreateAsync(new Payment
        {
            CheckNumber = "0000999999",
            PaymentMethod = "ACH",
            TotalPaymentAmount = paid,
            Status = PaymentStatus.Posted,
            ClaimPayments =
            {
                new ClaimPayment
                {
                    ClaimId = claimId,
                    PatientControlNumber = claim.ClaimNumber,
                    ClaimStatusCode = "1",
                    ChargeAmount = claim.TotalChargeAmount,
                    PaymentAmount = paid,
                    ServiceLines = (claim.ServiceLines ?? new List<ClaimServiceLineDto>())
                        .Select(sl => new ServiceLinePayment
                        {
                            LineNumber = sl.LineNumber, ProcedureCode = sl.ProcedureCode,
                            ChargeAmount = sl.ChargeAmount, PaymentAmount = sl.LinePaidAmount ?? 0m, Units = sl.Units,
                        })
                        .ToList(),
                },
            },
        }).GetAwaiter().GetResult();
    }

    /// <summary>The reconciliation job (its timer is off here; tests call RunOnceAsync).</summary>
    public ReservationReconciliationJob ReconciliationJob => Services.GetRequiredService<ReservationReconciliationJob>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("PaymentRuns:ReservationReconciliationEnabled", "false");
        // TRN03 (and BPR10 on ACH): required for every 835.
        builder.UseSetting("Era:OriginatingCompanyId", "1123456789");
        builder.ConfigureServices(services =>
        {
            var remove = services
                .Where(d => d.ServiceType == typeof(IPaymentRepository)
                         || d.ServiceType == typeof(IPaymentRunRepository)
                         || d.ServiceType == typeof(IReversalRunRepository)
                         || d.ServiceType == typeof(IEraEnvelopeRepository)
                         || d.ServiceType.FullName?.Contains("Cosmos") == true
                         || d.ServiceType.FullName?.Contains("Mongo") == true
                         || d.ImplementationType?.FullName?.Contains("Cosmos") == true
                         || d.ImplementationType?.FullName?.Contains("Mongo") == true)
                .ToList();
            foreach (var d in remove)
                services.Remove(d);

            services.AddSingleton<IPaymentRepository>(Payments);
            services.AddSingleton<IPaymentRunRepository>(Runs);
            services.AddSingleton<IReversalRunRepository>(ReversalRuns);
            services.AddSingleton<IEraEnvelopeRepository>(Envelopes);
            services.AddSingleton<IClaimReservationRepository>(Reservations);
            services.AddSingleton<IReservationAuditLog>(Audit);
            services.AddSingleton<IProviderReceivableRepository>(new InMemoryProviderReceivableRepository());
            services.AddSingleton<TimeProvider>(Clock);
            services.AddHttpClient(ClaimsServiceClient.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => Claims);
            services.AddHttpClient(TradingPartnersClient.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => TradingPartners);
        });
    }

    /// <summary>A caller with a development user token for <see cref="Tenant"/>.</summary>
    public HttpClient As(string subject, params string[] roles)
    {
        var client = CreateDefaultClient(new ChoDevelopmentTokenHandler(subject, roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        return client;
    }

    public HttpClient AsService(string clientId = "payment-scheduler")
    {
        var client = CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken(clientId, Tenant));
        return client;
    }
}

/// <summary>A clock tests move forward (real time plus an offset).</summary>
public sealed class ManualClock : TimeProvider
{
    private long _offsetTicks;

    public void Advance(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref _offsetTicks));
}

/// <summary>What a stand-in saw on one request, after validating its token.</summary>
public sealed record RecordedCall(
    HttpMethod Method,
    string Path,
    HttpStatusCode Status,
    string? Subject,
    string? AuthorizedParty,
    string? TokenTenant,
    bool IsService,
    string Body);

/// <summary>Token validation and permission checks shared by the stand-ins.</summary>
public abstract class StandInService : HttpMessageHandler
{
    private readonly List<RecordedCall> _calls = new();

    public IReadOnlyList<RecordedCall> Calls
    {
        get { lock (_calls) return _calls.ToList(); }
    }

    protected sealed record Caller(string? Subject, string? Azp, string? Tenant, bool IsService, ISet<string> Roles, IReadOnlyList<string> Permissions)
    {
        public bool Has(string permission)
        {
            if (IsService)
                return !ChoRolePermissions.IsReserved(permission);
            var granted = Permissions.Count > 0 ? Permissions : ChoRolePermissions.Expand(Roles).ToList();
            return ChoRolePermissions.Satisfies(granted, permission);
        }
    }

    protected abstract string RequiredPermission(HttpRequestMessage request);

    protected abstract Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, string body, Caller caller);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var caller = await AuthenticateAsync(request);

        HttpResponseMessage response;
        if (caller == null)
            response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        else if (request.Headers.TryGetValues("X-Tenant-ID", out var named)
                 && !string.Equals(named.FirstOrDefault(), caller.Tenant, StringComparison.Ordinal))
            response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        else if (!caller.Has(RequiredPermission(request)))
            response = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent($"missing {RequiredPermission(request)}"),
            };
        else
            response = await HandleAsync(request, body, caller);

        lock (_calls)
        {
            _calls.Add(new RecordedCall(request.Method, request.RequestUri!.AbsolutePath, response.StatusCode,
                caller?.Subject, caller?.Azp, caller?.Tenant, caller?.IsService == true, body));
        }
        return response;
    }

    private static async Task<Caller?> AuthenticateAsync(HttpRequestMessage request)
    {
        var auth = request.Headers.Authorization;
        if (auth == null || !string.Equals(auth.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
                         || string.IsNullOrEmpty(auth.Parameter))
            return null;

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(auth.Parameter, new TokenValidationParameters
        {
            ValidIssuers = new[] { ChoDevelopmentAuth.UserIssuer, ChoDevelopmentAuth.ServiceIssuer },
            ValidAudience = ChoDevelopmentAuth.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(ChoDevelopmentAuth.SymmetricKey)),
        });
        if (!result.IsValid)
            return null;

        var token = (JsonWebToken)result.SecurityToken;
        var claims = result.ClaimsIdentity.Claims.ToList();
        var roles = claims.Where(c => c.Type == ChoClaimTypes.Role).Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var permissions = claims.Where(c => c.Type == ChoClaimTypes.Permission).Select(c => c.Value).ToList();
        // A service only when the reserved role comes from the service issuer.
        var isService = roles.Contains(ChoServiceRole.Name) && token.Issuer == ChoDevelopmentAuth.ServiceIssuer;
        return new Caller(
            token.Subject,
            claims.FirstOrDefault(c => c.Type == ChoClaimTypes.AuthorizedParty)?.Value,
            claims.FirstOrDefault(c => c.Type == ChoClaimTypes.TenantId)?.Value,
            isService, roles, permissions);
    }

    // Shared by a WebApplicationFactory; the client factory must not dispose it.
    protected override void Dispose(bool disposing) { }
}

/// <summary>
/// claims-service as payment-service sees it: search (claims:read), read
/// (claims:read), remittance (claims:work; idempotent for the same check
/// number, 409 for another, 422 unless Approved), void (claims:void; idempotent,
/// 422 unless Paid), adjustments (claims:read).
/// </summary>
public sealed class StandInClaimsService : StandInService
{
    public sealed class StoredClaim
    {
        public ClaimDto Dto { get; init; } = new();
        public string Status { get; set; } = "Approved";
        public string? CheckNumber { get; set; }
        public int FinalizeCount { get; set; }
        public int VoidCount { get; set; }
        public string? VoidReason { get; set; }
    }

    private readonly object _lock = new();
    public Dictionary<string, StoredClaim> Store { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ClaimAdjustmentDto> Adjustments { get; } = new(StringComparer.Ordinal);

    /// <summary>Claim ids whose remittance answers 503.</summary>
    public HashSet<string> FailRemittance { get; } = new(StringComparer.Ordinal);

    /// <summary>Claim ids whose void answers 503.</summary>
    public HashSet<string> FailVoid { get; } = new(StringComparer.Ordinal);

    /// <summary>When true, search returns every Approved claim twice.</summary>
    public bool DuplicateSearchResults { get; set; }

    public StoredClaim Add(string id, string status = "Approved", decimal approved = 100m, string npi = "1234567893")
    {
        var claim = new StoredClaim
        {
            Status = status,
            Dto = new ClaimDto
            {
                Id = id,
                ClaimNumber = "CN-" + id,
                MemberId = "M-" + id,
                BillingProviderNPI = npi,
                ProviderName = "Clinic " + npi,
                TotalChargeAmount = approved + 20m,
                AdjudicationResult = new ClaimAdjudicationDto { PayerPayment = approved },
                Status = ClaimStatus.Approved,
                ServiceDateFrom = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
                ServiceLines = new List<ClaimServiceLineDto>
                {
                    new() { LineNumber = 1, ProcedureCode = "99213", ChargeAmount = approved + 20m, PaidAmount = approved, Units = 1 },
                },
            },
        };
        lock (_lock) Store[id] = claim;
        return claim;
    }

    public ClaimAdjustmentDto AddPendingReversal(string adjustmentId, string predecessorClaimId)
    {
        var adj = new ClaimAdjustmentDto
        {
            Id = adjustmentId,
            PredecessorClaimId = predecessorClaimId,
            NewClaimId = predecessorClaimId + "-v2",
            AdjustmentReason = "corrected claim",
            Status = ClaimAdjustmentDtoStatus.PendingReversal,
            CreatedBy = "examiner-1",
            CreatedAt = DateTime.UtcNow,
        };
        lock (_lock) Adjustments[adjustmentId] = adj;
        return adj;
    }

    public StoredClaim Get(string id)
    {
        lock (_lock) return Store[id];
    }

    protected override string RequiredPermission(HttpRequestMessage request)
    {
        if (request.Method == HttpMethod.Get) return "claims:read";
        return request.RequestUri!.AbsolutePath.EndsWith("/void", StringComparison.Ordinal) ? "claims:void" : "claims:work";
    }

    protected override Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, string body, Caller caller)
    {
        var path = request.RequestUri!.AbsolutePath;
        lock (_lock)
        {
            if (request.Method == HttpMethod.Get && path == "/api/claims/search")
            {
                var approved = Store.Values.Where(c => c.Status == "Approved").Select(c => c.Dto).ToList();
                if (DuplicateSearchResults)
                    approved = approved.Concat(approved).ToList();
                return Json(approved);
            }

            if (request.Method == HttpMethod.Get && path == "/api/v1/adjustments")
            {
                var items = Adjustments.Values.Where(a => a.Status == ClaimAdjustmentDtoStatus.PendingReversal).ToList();
                return Json(new ClaimAdjustmentListResponseDto { Total = items.Count, Page = 1, PageSize = 200, Items = items });
            }

            if (request.Method == HttpMethod.Get && path.StartsWith("/api/v1/adjustments/", StringComparison.Ordinal))
            {
                var id = Uri.UnescapeDataString(path["/api/v1/adjustments/".Length..]);
                return Adjustments.TryGetValue(id, out var adj) ? Json(adj) : Status(HttpStatusCode.NotFound);
            }

            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 3 && segments[0] == "api" && segments[1] == "claims")
            {
                var id = Uri.UnescapeDataString(segments[2]);
                if (!Store.TryGetValue(id, out var claim))
                    return Status(HttpStatusCode.NotFound);

                if (request.Method == HttpMethod.Get && segments.Length == 3)
                    return Json(claim.Dto);

                if (request.Method == HttpMethod.Post && segments.Length == 4 && segments[3] == "remittance")
                {
                    if (FailRemittance.Contains(id))
                        return Status(HttpStatusCode.ServiceUnavailable);
                    var check = JsonNode.Parse(body)?["checkNumber"]?.GetValue<string>();
                    if (claim.Status == "Paid")
                        return Status(string.Equals(claim.CheckNumber, check, StringComparison.Ordinal)
                            ? HttpStatusCode.OK : HttpStatusCode.Conflict);
                    if (claim.Status != "Approved")
                        return Status(HttpStatusCode.UnprocessableEntity);
                    claim.Status = "Paid";
                    claim.CheckNumber = check;
                    claim.FinalizeCount++;
                    return Json(claim.Dto);
                }

                if (request.Method == HttpMethod.Post && segments.Length == 4 && segments[3] == "void")
                {
                    if (FailVoid.Contains(id))
                        return Status(HttpStatusCode.ServiceUnavailable);
                    if (claim.Status == "Voided")
                        return Json(claim.Dto);
                    if (claim.Status != "Paid")
                        return Status(HttpStatusCode.UnprocessableEntity);
                    var node = JsonNode.Parse(body);
                    claim.Status = "Voided";
                    claim.VoidCount++;
                    claim.VoidReason = node?["reason"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(node?["reversalRunId"]?.GetValue<string>()))
                    {
                        foreach (var adj in Adjustments.Values.Where(a =>
                                     a.PredecessorClaimId == id && a.Status == ClaimAdjustmentDtoStatus.PendingReversal))
                            adj.Status = ClaimAdjustmentDtoStatus.Active;
                    }
                    return Json(claim.Dto);
                }
            }
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static Task<HttpResponseMessage> Json<T>(T value)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(value) });

    private static Task<HttpResponseMessage> Status(HttpStatusCode status)
        => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
}

/// <summary>trading-partner-service: every NPI maps to TP-1 (needs trading-partners:read).</summary>
public sealed class StandInTradingPartnerService : StandInService
{
    /// <summary>NPIs with no trading partner (404).</summary>
    public HashSet<string> MissingNpis { get; } = new(StringComparer.Ordinal);

    protected override string RequiredPermission(HttpRequestMessage request) => "trading-partners:read";

    protected override Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, string body, Caller caller)
    {
        var segments = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var npi = segments.Length >= 5 ? segments[4] : "unknown";
        lock (MissingNpis)
        {
            if (MissingNpis.Contains(npi))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
        var partner = new TradingPartnerSummary
        {
            Id = "tp-doc-1",
            TenantId = caller.Tenant ?? string.Empty,
            TradingPartnerId = "TP-1",
            Environment = "Production",
            PartnerName = "Clearinghouse",
            X12Config = new X12ConfigDto { SenderId = "CHOPAYER", ReceiverId = "CLEARING" },
            BillingProviderNpis = new List<string> { npi },
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(partner) });
    }
}

/// <summary>Envelope store that keeps every record for assertions.</summary>
public sealed class RecordingEnvelopeRepository : IEraEnvelopeRepository
{
    private readonly List<EraEnvelopeRecord> _records = new();
    private readonly string _tenant;

    public RecordingEnvelopeRepository(string tenant) => _tenant = tenant;

    public IReadOnlyList<EraEnvelopeRecord> All
    {
        get { lock (_records) return _records.ToList(); }
    }

    public Task<EraEnvelopeRecord?> GetByIdAsync(string id) => Task.FromResult(All.FirstOrDefault(r => r.Id == id));
    public Task<IEnumerable<EraEnvelopeRecord>> GetByPaymentRunIdAsync(string paymentRunId)
        => Task.FromResult<IEnumerable<EraEnvelopeRecord>>(All.Where(r => r.PaymentRunId == paymentRunId).ToList());
    public Task<IEnumerable<EraEnvelopeRecord>> GetByReversalRunIdAsync(string reversalRunId)
        => Task.FromResult<IEnumerable<EraEnvelopeRecord>>(All.Where(r => r.ReversalRunId == reversalRunId).ToList());
    public Task<IEnumerable<EraEnvelopeRecord>> SearchAsync(string? paymentRunId, string? tradingPartnerId, string? reversalRunId = null)
        => Task.FromResult<IEnumerable<EraEnvelopeRecord>>(All);

    public Task<IReadOnlyCollection<string>> GetClaimIdsWithEnvelopeAsync(IReadOnlyCollection<string> claimIds, bool reversal)
    {
        var wanted = new HashSet<string>(claimIds, StringComparer.Ordinal);
        IReadOnlyCollection<string> found = All
            .Where(r => !string.IsNullOrEmpty(r.ReversalRunId) == reversal)
            .SelectMany(r => r.ClaimIds)
            .Where(wanted.Contains)
            .ToHashSet(StringComparer.Ordinal);
        return Task.FromResult(found);
    }

    public Task<EraEnvelopeRecord> CreateAsync(EraEnvelopeRecord record)
    {
        record.TenantId = _tenant;
        lock (_records) _records.Add(record);
        return Task.FromResult(record);
    }
}
