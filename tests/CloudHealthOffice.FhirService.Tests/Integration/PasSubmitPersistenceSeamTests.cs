extern alias authsvc;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CloudHealthOffice.FhirService.Tests.TestSupport;
using CloudHealthOffice.Testing.Mongo;
using FhirService.Models;
using FhirService.Services;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Moq;
using CloudHealthOffice.ProviderEnrollmentService.Abstractions;
using CloudHealthOffice.PriorAuthRuleEngine.Abstractions;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using CloudHealthOffice.ProviderEnrollmentService.Models;
using EnrollmentLob = CloudHealthOffice.ProviderEnrollmentService.Models.LineOfBusiness;
using StoredAuthorization = authsvc::AuthorizationService.Models.Authorization;
using StoredStatus = authsvc::AuthorizationService.Models.AuthorizationStatus;
using StoredLineOfBusiness = authsvc::AuthorizationService.Models.LineOfBusiness;

namespace CloudHealthOffice.FhirService.Tests.Integration;

/// <summary>
/// G-01 / G-03 (D-01, D-02, D-03, D-08). The PAS seam end to end, with no stub
/// between the services: fhir-service's real <c>Claim/$submit</c> (real
/// controller, real <see cref="PasAutoAdjudicator"/>) persists into the real
/// authorization-service (real pipeline: CHO token authentication, tenant from
/// the token, real controllers, real Mongo repository on an ephemeral mongod),
/// and <c>Claim/$inquire</c> reads the record back through the same seam.
///
/// Only things that are not under test are replaced: the compliance checker
/// (presence checks, covered elsewhere), the provider-verification service
/// (answers 404, so verification fails open as it does when unavailable), and
/// the rule engine (pends). The adjudicator is wrapped to record when it ran.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class PasSubmitPersistenceSeamTests : IAsyncLifetime
{
    private const string Tenant = "test-tenant";
    private const string ApproveCode = "27447";
    private const string DenyCode = "V2020";
    private const string PendCode = "70553";

    private static readonly JsonSerializerOptions FhirJson =
        new JsonSerializerOptions().ForFhir(typeof(Patient).Assembly);

    private readonly MongoRunnerFixture _mongo;
    private readonly string _databaseName = "pas_seam_" + Guid.NewGuid().ToString("N");
    private AuthorizationServiceHost _authorizationService = null!;
    private FhirServiceHost _fhirService = null!;

    public PasSubmitPersistenceSeamTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public Task InitializeAsync()
    {
        _authorizationService = new AuthorizationServiceHost(_mongo.ConnectionString, _databaseName);
        _fhirService = new FhirServiceHost(_authorizationService);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _fhirService.DisposeAsync();
        await _authorizationService.DisposeAsync();
        await _mongo.Client.DropDatabaseAsync(_databaseName);
    }

    // ── D-01: the record is stored, and a preAuthRef names a stored record ──

    [Fact]
    public async Task ApprovedSubmit_IsStoredWithTheDecisionAndTheRequestedPeriod()
    {
        var before = DateTime.UtcNow;
        var claimResponse = await SubmitAsync(PasBundles.Complete(procedureCode: ApproveCode));

        claimResponse.Disposition.Should().Be("approved");
        var stored = await StoredAsync(claimResponse.PreAuthRef);

        stored.Should().NotBeNull("a preAuthRef was issued, so the authorization it names must exist");
        stored!.TenantId.Should().Be(Tenant);
        stored.Status.Should().Be(StoredStatus.Approved);
        stored.ReviewDecision.Should().Be("A1");
        // The approved period is the requested one, not today..+1 year.
        stored.ApprovedServiceDateFrom.Should().Be(new DateTime(2026, 11, 2));
        stored.ApprovedServiceDateTo.Should().Be(new DateTime(2026, 11, 4));
        claimResponse.PreAuthPeriod!.Start.Should().Be("2026-11-02");
        claimResponse.PreAuthPeriod.End.Should().Be("2026-11-04");
        // Attributed to the adjudicator's service identity, with a history.
        stored.CreatedBy.Should().Be("fhir-service");
        stored.StatusHistory.Select(h => h.Status).Should()
            .Equal(StoredStatus.Submitted, StoredStatus.Approved);
        stored.StatusHistory.Should().OnlyContain(h => h.ChangedBy == "fhir-service");
        stored.ReviewedDate.Should().NotBeNull().And.BeOnOrAfter(stored.SubmittedDate);
        stored.SubmittedDate.Should().BeOnOrAfter(before.AddSeconds(-1));
    }

    [Fact]
    public async Task DeniedSubmit_IsStoredWithItsCodedReason()
    {
        var claimResponse = await SubmitAsync(PasBundles.Complete(procedureCode: DenyCode));

        claimResponse.Disposition.Should().Be("denied");
        var stored = await StoredAsync(claimResponse.PreAuthRef);

        stored.Should().NotBeNull();
        stored!.Status.Should().Be(StoredStatus.Denied);
        stored.ReviewDecision.Should().Be("A3");
        stored.DenialReasonCode.Should().Be("NOT_COVERED");
        stored.DenialReason.Should().Contain(DenyCode);
        stored.ApprovedServiceDateFrom.Should().BeNull();
        stored.StatusHistory.Last().Reason.Should().Contain(DenyCode);
    }

    [Fact]
    public async Task PendedSubmit_IsStoredAsPended()
    {
        var claimResponse = await SubmitAsync(PasBundles.Complete(procedureCode: PendCode));

        claimResponse.Disposition.Should().Be("pended");
        var stored = await StoredAsync(claimResponse.PreAuthRef);

        stored.Should().NotBeNull();
        stored!.Status.Should().Be(StoredStatus.Pended);
        stored.ReviewDecision.Should().Be("A4");
    }

    // ── $inquire reads what $submit stored ───────────────────────────────

    [Theory]
    [InlineData(ApproveCode, "approved", "A1")]
    [InlineData(DenyCode, "denied", "A3")]
    public async Task Inquire_AfterSubmit_ReturnsTheDecision(string code, string disposition, string reviewAction)
    {
        var submitted = await SubmitAsync(PasBundles.Complete(procedureCode: code));

        var (status, inquiry) = await InquireAsync(submitted.PreAuthRef);

        status.Should().Be(HttpStatusCode.OK, "the authorization $submit answered for must be inquirable");
        var claimResponse = inquiry!.Entry.Select(e => e.Resource).OfType<ClaimResponse>().Single();
        claimResponse.PreAuthRef.Should().Be(submitted.PreAuthRef);
        claimResponse.Disposition.Should().Be(disposition);
        claimResponse.Patient.Reference.Should().Be($"Patient/{PasBundles.PatientId}");
        JsonSerializer.Serialize(claimResponse, FhirJson).Should().Contain($"\"{reviewAction}\"");
    }

    // ── D-03: the request's own data, not placeholders ──────────────────

    [Fact]
    public async Task Submit_StoresTheBundlesPatientCoverageProviderAndCodes()
    {
        var claimResponse = await SubmitAsync(PasBundles.Complete(procedureCode: ApproveCode));
        var stored = await StoredAsync(claimResponse.PreAuthRef);

        stored.Should().NotBeNull();
        stored!.MemberId.Should().Be(PasBundles.MemberId);
        stored.PatientFirstName.Should().Be("Ana");
        stored.PatientLastName.Should().Be("Rivera");
        stored.PatientDateOfBirth.Should().Be(new DateTime(1984, 3, 7));
        stored.LineOfBusiness.Should().Be(StoredLineOfBusiness.Medicaid, "Coverage.type MC is Medicaid, never a Commercial default");
        stored.RequestingProviderNPI.Should().Be(PasBundles.RequestingNpi);
        stored.ServiceTypeCode.Should().Be("2", "the X12 service type of the request, not the claim type");
        stored.RequestedServiceDateFrom.Should().Be(new DateTime(2026, 11, 2));
        stored.RequestedServiceDateTo.Should().Be(new DateTime(2026, 11, 4));
        var service = stored.RequestedServices.Should().ContainSingle().Subject;
        service.ProcedureCode.Should().Be(ApproveCode);
        service.ProductOrServiceSystem.Should().Be(PasBundles.CptSystem);
        service.LineNumber.Should().Be(1);
        service.ServiceStatus.Should().Be("A1");
        service.ApprovedUnits.Should().Be(service.RequestedUnits);
        claimResponse.Item.Should().ContainSingle().Which.ItemSequence.Should().Be(1);
        claimResponse.Item[0].Adjudication.Single(a => a.Extension.Count > 0).Value.Should().Be(service.RequestedUnits);
        stored.DiagnosisCodes.Should().ContainSingle().Which.Code.Should().Be("M17.11");
    }

    // ── D-08: urgency and the receipt clock ──────────────────────────────

    [Theory]
    [InlineData("stat", "U")]
    [InlineData("normal", "E")]
    public async Task Submit_MapsClaimPriorityToLevelOfService(string priority, string levelOfService)
    {
        var claimResponse = await SubmitAsync(PasBundles.Complete(procedureCode: PendCode, priority: priority));
        var stored = await StoredAsync(claimResponse.PreAuthRef);

        stored.Should().NotBeNull();
        stored!.LevelOfService.Should().Be(levelOfService);
    }

    [Fact]
    public async Task Submit_StartsTheClockAtReceipt_BeforeAdjudication()
    {
        var claimResponse = await SubmitAsync(PasBundles.Complete(procedureCode: PendCode));
        var stored = await StoredAsync(claimResponse.PreAuthRef);

        stored.Should().NotBeNull();
        _fhirService.Adjudicator.StartedAt.Should().NotBeNull();
        stored!.SubmittedDate.Should().BeOnOrBefore(_fhirService.Adjudicator.StartedAt!.Value,
            "the decision clock starts when the request arrives, not after it is adjudicated");
        stored.StatusHistory.First().ChangedAt.Should().Be(stored.SubmittedDate);
    }

    // ── B1: the stored record states the basis the decision used ─────────

    [Fact]
    public async Task CommercialCoverage_IsDecidedAsCommercial_NotThroughTheMedicaidEnrollmentGate()
    {
        // X12 1032 CI is commercial insurance. The provider is not enrolled in
        // TX Medicaid (the gate denies anything it is asked about). Deciding
        // this request as Medicaid would deny it on a basis it does not have,
        // while the record says Commercial.
        var claimResponse = await SubmitAsync(WithProviderIdentifier(PasBundles.Complete(
            procedureCode: PendCode, coverageTypeCode: "CI", serviceState: "TX")));
        var stored = await StoredAsync(claimResponse.PreAuthRef);

        stored.Should().NotBeNull();
        stored!.LineOfBusiness.Should().Be(StoredLineOfBusiness.Commercial);
        _fhirService.Gate.Calls.Should().NotContain(c => c.Lob == EnrollmentLob.Medicaid,
            "a Commercial request is not decided as Medicaid");
        claimResponse.Disposition.Should().NotBe("denied");
        stored.Status.Should().NotBe(StoredStatus.Denied);
        var decided = stored.StatusHistory.Last();
        decided.Source.Should().NotContain("EnrollmentGate");
        decided.Source.Should().Contain("lob=Commercial", "the history states the line of business the decision used");
        var context = _fhirService.RuleEngine.Contexts.Should().ContainSingle().Subject;
        context.Lob.Should().Be(PaLineOfBusiness.Commercial, "the rule engine decided on the record's line of business");
        context.StateCode.Should().Be("TX");
        decided.Source.Should().Contain("state=TX");
    }

    [Fact]
    public async Task MedicaidCoverage_DeniedByTheEnrollmentGate_RecordsTheBasisItWasDecidedOn()
    {
        var claimResponse = await SubmitAsync(WithProviderIdentifier(PasBundles.Complete(
            procedureCode: PendCode, coverageTypeCode: "MC", serviceState: "TX")));
        var stored = await StoredAsync(claimResponse.PreAuthRef);

        stored.Should().NotBeNull();
        _fhirService.Gate.Calls.Should().ContainSingle()
            .Which.Should().Be((EnrollmentLob.Medicaid, "TX"));
        stored!.Status.Should().Be(StoredStatus.Denied);
        stored.LineOfBusiness.Should().Be(StoredLineOfBusiness.Medicaid);
        stored.StatusHistory.Last().Source.Should().Be("automated:EnrollmentGate;lob=Medicaid;state=TX");
    }

    [Fact]
    public async Task CommercialAutoApproval_WithAState_DoesNotClaimTheState()
    {
        // The approval list reads no state, and the gate does not run for
        // Commercial: the TX in the request played no part in the decision.
        var claimResponse = await SubmitAsync(WithProviderIdentifier(PasBundles.Complete(
            procedureCode: ApproveCode, coverageTypeCode: "CI", serviceState: "TX")));
        var stored = await StoredAsync(claimResponse.PreAuthRef);

        stored.Should().NotBeNull();
        stored!.Status.Should().Be(StoredStatus.Approved);
        stored.StatusHistory.Last().Source.Should().Be("automated:AutoApproveList;lob=Commercial;state=none");
    }

    [Fact]
    public async Task FactoryNoOpEngine_ItsPend_IsStoredEndToEnd_ClaimingNoBasis()
    {
        // The engine registered when no database is configured evaluates
        // nothing. Its pend reaches authorization-service as NoOp with no LOB
        // or state, and is accepted and stored as such.
        await using var host = new FhirServiceHost(_authorizationService, factoryNoOpEngine: true);

        var claimResponse = await SubmitAsync(WithProviderIdentifier(PasBundles.Complete(
            procedureCode: PendCode, coverageTypeCode: "CI", serviceState: "TX")), host);
        var stored = await StoredAsync(claimResponse.PreAuthRef, host);

        claimResponse.Disposition.Should().Be("pended");
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(StoredStatus.Pended);
        stored.LineOfBusiness.Should().Be(StoredLineOfBusiness.Commercial);
        stored.StatusHistory.Last().Source.Should().Be("automated:NoOp;lob=n/a;state=none");
        host.RuleEngine.Contexts.Should().BeEmpty("this host keeps the factory's NoOp engine");
    }

    [Fact]
    public async Task UnknownLineOfBusiness_IsPended_NotAutoDecided()
    {
        // A Coverage.type no mapping recognises: the line of business cannot
        // be determined, so no rule may decide on one.
        var claimResponse = await SubmitAsync(PasBundles.Complete(procedureCode: ApproveCode, coverageTypeCode: "ZZ"));
        var stored = await StoredAsync(claimResponse.PreAuthRef);

        claimResponse.Disposition.Should().Be("pended");
        stored.Should().NotBeNull();
        stored!.LineOfBusiness.Should().Be(StoredLineOfBusiness.Unknown);
        stored.Status.Should().Be(StoredStatus.Pended);
        stored.ReviewDecision.Should().Be("A4");
        stored.PendReason.Should().Contain("line of business");
        stored.StatusHistory.Last().Source.Should().Be("automated:LineOfBusinessUndetermined;lob=n/a;state=none",
            "no rule was evaluated, so no line of business or state is claimed");
        _fhirService.Adjudicator.StartedAt.Should().BeNull("nothing is auto-decided on an unknown line of business");
    }

    // ── helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// The requesting provider's NPI also on the Claim.provider reference, the
    /// one place the adjudicator used to read it from, so the request reaches
    /// the enrollment gate whichever source is used.
    /// </summary>
    private static Bundle WithProviderIdentifier(Bundle bundle)
    {
        PasBundles.ClaimOf(bundle).Provider.Identifier = new Identifier(PasBundles.NpiSystem, PasBundles.RequestingNpi);
        return bundle;
    }

    private async Task<ClaimResponse> SubmitAsync(Bundle bundle, FhirServiceHost? host = null)
    {
        host ??= _fhirService;
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/fhir/r4/Claim/$submit")
        {
            Content = new StringContent(JsonSerializer.Serialize(bundle, FhirJson), Encoding.UTF8, "application/fhir+json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", host.SmartToken());

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        var responseBundle = JsonSerializer.Deserialize<Bundle>(body, FhirJson)!;
        var claimResponse = responseBundle.Entry.Select(e => e.Resource).OfType<ClaimResponse>().Single();
        claimResponse.PreAuthRef.Should().NotBeNullOrWhiteSpace();
        return claimResponse;
    }

    private async Task<(HttpStatusCode Status, Bundle? Body)> InquireAsync(string authorizationNumber)
    {
        var claim = new Claim
        {
            Status = FinancialResourceStatusCodes.Active,
            Type = new CodeableConcept("http://terminology.hl7.org/CodeSystem/claim-type", "professional"),
            Use = ClaimUseCode.Preauthorization,
            Patient = new ResourceReference($"Patient/{PasBundles.PatientId}"),
            Created = "2026-10-11",
            Provider = new ResourceReference($"Practitioner/{PasBundles.PractitionerId}"),
            Priority = new CodeableConcept(PasBundles.ProcessPrioritySystem, "normal"),
            Insurance = [new() { Sequence = 1, Focal = true, Coverage = new ResourceReference($"Coverage/{PasBundles.CoverageId}") }],
            Identifier = [new Identifier("http://example.org/pas", authorizationNumber)],
        };
        var bundle = new Bundle { Type = Bundle.BundleType.Collection, Entry = [new() { Resource = claim }] };

        using var client = _fhirService.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/fhir/r4/Claim/$inquire")
        {
            Content = new StringContent(JsonSerializer.Serialize(bundle, FhirJson), Encoding.UTF8, "application/fhir+json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _fhirService.SmartToken());

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        return response.StatusCode == HttpStatusCode.OK
            ? (response.StatusCode, JsonSerializer.Deserialize<Bundle>(body, FhirJson))
            : (response.StatusCode, null);
    }

    /// <summary>The stored record, read straight from authorization-service's database.</summary>
    private async Task<StoredAuthorization?> StoredAsync(string authorizationNumber, FhirServiceHost? host = null)
    {
        host ??= _fhirService;
        var collection = _mongo.Client.GetDatabase(_databaseName)
            .GetCollection<StoredAuthorization>("Authorizations");
        var stored = await collection
            .Find(a => a.TenantId == Tenant && a.AuthorizationNumber == authorizationNumber)
            .FirstOrDefaultAsync();
        stored.Should().NotBeNull(
            "a preAuthRef was issued, so the authorization it names must exist. What crossed the seam:\n{0}",
            string.Join("\n", host.Exchanges));
        return stored;
    }

    /// <summary>authorization-service: the real host over a real (ephemeral) MongoDB.</summary>
    private sealed class AuthorizationServiceHost
        : WebApplicationFactory<authsvc::AuthorizationService.Controllers.AuthorizationsController>
    {
        private readonly string _connectionString;
        private readonly string _databaseName;

        public AuthorizationServiceHost(string connectionString, string databaseName)
        {
            _connectionString = connectionString;
            _databaseName = databaseName;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            // Host settings, not app configuration: Program reads the database
            // provider while it registers services, before app-configuration
            // callbacks from the factory apply.
            builder.UseSetting("Database:Provider", "MongoDb");
            builder.UseSetting("MongoDb:ConnectionString", _connectionString);
            builder.UseSetting("MongoDb:DatabaseName", _databaseName);
            builder.ConfigureServices(services =>
            {
                // Background workers are not part of this seam.
                services.RemoveAll<IHostedService>();
            });
        }
    }

    /// <summary>
    /// fhir-service: the real host, whose authorization-service client is wired
    /// to the in-process authorization-service above.
    /// </summary>
    private sealed class FhirServiceHost : FhirTestWebAppFactory
    {
        private readonly AuthorizationServiceHost _authorizationService;

        private readonly bool _factoryNoOpEngine;

        /// <param name="factoryNoOpEngine">Keep the factory's NoOp rule engine (evaluates nothing) instead of <see cref="ReviewRuleEngine"/>.</param>
        public FhirServiceHost(AuthorizationServiceHost authorizationService, bool factoryNoOpEngine = false)
        {
            _authorizationService = authorizationService;
            _factoryNoOpEngine = factoryNoOpEngine;
        }

        public ReviewRuleEngine RuleEngine { get; } = new();

        public TimingAdjudicator Adjudicator { get; } = new();

        public RecordingEnrollmentGate Gate { get; } = new();

        /// <summary>Every fhir-service → authorization-service exchange, for failure messages.</summary>
        public List<string> Exchanges { get; } = [];

        public string SmartToken() => IssueToken("system/Claim.write system/Claim.read", tenantId: Tenant);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Cms0057:PasAutoAdjudication:AutoApproveServiceTypes:0"] = ApproveCode,
                    ["Cms0057:PasAutoAdjudication:AutoDenyServiceTypes:0"] = DenyCode,
                }));

            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("AuthorizationService")
                    .ConfigurePrimaryHttpMessageHandler(() => _authorizationService.Server.CreateHandler())
                    .AddHttpMessageHandler(() => new CapturingHandler(Exchanges));
                services.AddHttpClient("ProviderVerificationService")
                    .ConfigurePrimaryHttpMessageHandler(() => new StatusHandler(HttpStatusCode.NotFound));

                var compliance = new Mock<ICms0057ComplianceChecker>();
                compliance.Setup(c => c.ValidateCompliance(It.IsAny<Resource>()))
                    .Returns(new ComplianceResult(true, [], [],
                        new ComplianceSummary("Claim", 10, 10, [], [], [], new TimelineCompliance(true))));
                services.RemoveAll<ICms0057ComplianceChecker>();
                services.AddSingleton(compliance.Object);

                services.RemoveAll<IEnrollmentDecisionGate>();
                services.AddSingleton<IEnrollmentDecisionGate>(Gate);

                // A rule engine that evaluates (and pends for review), so a
                // request reaching Rule 5 is decided on its LOB and state. The
                // factory's NoOp engine evaluates nothing, and its pend
                // rightly claims no basis.
                if (!_factoryNoOpEngine)
                {
                    services.RemoveAll<IPriorAuthRuleEngine>();
                    services.AddSingleton<IPriorAuthRuleEngine>(RuleEngine);
                }

                // Wrap the real adjudicator to see when it ran.
                services.AddScoped<PasAutoAdjudicator>();
                services.RemoveAll<IPasAutoAdjudicator>();
                services.AddScoped<IPasAutoAdjudicator>(sp =>
                {
                    Adjudicator.Inner = sp.GetRequiredService<PasAutoAdjudicator>();
                    return Adjudicator;
                });
            });
        }
    }

    internal sealed class TimingAdjudicator : IPasAutoAdjudicator
    {
        public IPasAutoAdjudicator Inner { get; set; } = null!;

        public DateTime? StartedAt { get; private set; }

        public async Task<PasDecisionResult> TryDecideAsync(
            Claim claim, Bundle context, string tenantId, PasDecisionBasis basis, int timeoutMs,
            CancellationToken ct = default)
        {
            StartedAt = DateTime.UtcNow;
            await Task.Delay(20, ct);
            return await Inner.TryDecideAsync(claim, context, tenantId, basis, timeoutMs, ct);
        }
    }

    /// <summary>A rule engine whose rules send everything to clinical review.</summary>
    internal sealed class ReviewRuleEngine : IPriorAuthRuleEngine
    {
        /// <summary>Every context the engine was asked to evaluate.</summary>
        public List<PaRuleContext> Contexts { get; } = [];

        public Task<PaRuleDecision> EvaluateAsync(PaRuleContext context, CancellationToken ct = default)
        {
            lock (Contexts) Contexts.Add(context);
            return Task.FromResult(new PaRuleDecision
            {
                Outcome = PaDecisionOutcome.Pend,
                FiringRuleId = "ClinicalReviewRequired",
                FiringRuleName = "ClinicalReviewRequired",
                ResolvedRuleSetKey = $"test/{context.StateCode}/{context.Lob}",
            });
        }

        public Task<IReadOnlyList<PaRuleDocument>> GetApplicableRulesAsync(RuleSetKey key, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PaRuleDocument>>([]);
    }

    /// <summary>
    /// A state Medicaid enrollment gate for a provider enrolled nowhere: it
    /// denies whatever it is asked about, and records what that was.
    /// </summary>
    internal sealed class RecordingEnrollmentGate : IEnrollmentDecisionGate
    {
        public List<(EnrollmentLob Lob, string State)> Calls { get; } = [];

        public Task<GateResult> EvaluateAsync(
            string npi, string taxonomy, string stateCode, DateOnly serviceDate, EnrollmentLob lob,
            CancellationToken ct = default)
        {
            lock (Calls) Calls.Add((lob, stateCode));
            return Task.FromResult(GateResult.Deny("PEMS-001", "Provider not enrolled in state Medicaid."));
        }
    }

    /// <summary>Records each call (method, path, status, request body) so a failure says what crossed the seam.</summary>
    private sealed class CapturingHandler(List<string> exchanges) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var response = await base.SendAsync(request, ct);
            var answer = await response.Content.ReadAsStringAsync(ct);
            lock (exchanges)
                exchanges.Add($"{request.Method} {request.RequestUri?.PathAndQuery} -> {(int)response.StatusCode}\n"
                    + $"  sent: {body}\n  got:  {answer}");
            return response;
        }
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(status));
    }
}
