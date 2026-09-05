using System.Text.Json;
using Cms0057Acceptance.Tests.TestSupport;
using FhirService.Models;
using FhirService.Models.PayerToPayer;
using FhirService.Services;
using FhirService.Services.PayerToPayer;
using FhirService.Services.PayerToPayer.Ingestion;
using FhirService.Services.PayerToPayer.Outbound;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.Options;

namespace Cms0057Acceptance.Tests.Scenarios;

/// <summary>
/// P2P-02 — durable ingestion of a validated Payer-to-Payer package. The
/// outbound exchange (PayerToPayerOutboundTests) proved CHO can obtain another
/// payer's member-scoped package; these scenarios prove the package becomes a
/// durable, tenant-safe, member-scoped, provenance-preserving CHO record rather
/// than a validated Bundle that evaporates.
///
/// They drive the SAME production classes the running service binds —
/// <see cref="PayerToPayerOutboundService"/> orchestrating
/// <see cref="PayerToPayerPackageIngestionService"/> over an
/// <see cref="IPayerToPayerImportRepository"/> — with only the far side of the
/// wire faked. Synthetic data only; no PHI.
///
/// Traceability:
///   ingestion   src/services/fhir-service/Services/PayerToPayer/Ingestion/PayerToPayerPackageIngestionService.cs
///   policy      src/services/fhir-service/Services/PayerToPayer/Ingestion/PayerToPayerImportPolicy.cs
///   references  src/services/fhir-service/Services/PayerToPayer/Ingestion/PayerToPayerReferenceNormalizer.cs
///   store       src/services/fhir-service/Services/PayerToPayer/Ingestion/PayerToPayerImportRepository.cs
/// </summary>
public class PayerToPayerIngestionTests
{
    private const string TargetPayer = "PRIOR-PLAN";
    private const string OtherPayer = "OTHER-PLAN";
    private const string RemoteMemberId = "prior-1001";

    // ── Harness ─────────────────────────────────────────────────────────────────

    private sealed record Harness(
        PayerToPayerOutboundService Service,
        IPayerToPayerImportRepository Imports,
        InMemoryPayerToPayerOutboundExchangeStore Exchanges);

    private static Harness Build(
        IPayerToPayerRemoteClient peer,
        IPayerToPayerImportRepository? imports = null,
        string targetPayer = TargetPayer,
        string tenant = AcceptanceContext.TenantId)
    {
        var provider = new MockPatientAccessDataProvider();
        var adapterOptions = Options.Create(new FhirAdapterOptions { TenantId = AcceptanceContext.TenantId });

        var directory = Options.Create(new PayerToPayerDirectoryOptions
        {
            LocalPayerId = "cloud-health-office",
            PayersByTenant = new()
            {
                [tenant] =
                [
                    new PayerToPayerEndpointEntry
                    {
                        PayerId = targetPayer,
                        EndpointKey = $"{targetPayer.ToLowerInvariant()}-fhir",
                        BaseUrl = "https://prior-payer.example/fhir/r4",
                    },
                ],
            },
        });

        var repository = imports ?? new InMemoryPayerToPayerImportRepository();
        var exchanges = new InMemoryPayerToPayerOutboundExchangeStore();

        var service = new PayerToPayerOutboundService(
            new PatientAccessPayerToPayerMemberSource(provider, adapterOptions),
            new PatientAccessPayerToPayerMemberMatchSource(provider, adapterOptions),
            new ConfiguredPayerToPayerConsentGate(Options.Create(new PayerToPayerConsentOptions
            {
                OptedInMembersByTenant = new() { [AcceptanceContext.TenantId] = ["pat-001"] },
            })),
            new ConfiguredPayerToPayerEndpointResolver(
                directory, AcceptanceContext.Logger<ConfiguredPayerToPayerEndpointResolver>()),
            peer,
            exchanges,
            new PayerToPayerPackageIngestionService(
                repository, AcceptanceContext.Logger<PayerToPayerPackageIngestionService>()),
            directory,
            AcceptanceContext.Logger<PayerToPayerOutboundService>());

        return new Harness(service, repository, exchanges);
    }

    private static PayerToPayerOutboundRequest Request(
        string memberId = "pat-001",
        string tenant = AcceptanceContext.TenantId,
        string targetPayer = TargetPayer,
        string? transitionKey = "transition-2026-01") => new()
    {
        TenantId = tenant,
        MemberId = memberId,
        TargetPayerId = targetPayer,
        TransitionKey = transitionKey,
        InitiatedBy = "enrollment:coverage-transition",
        ExchangeDateUtc = new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc),
    };

    private static async Task<IReadOnlyList<ImportedFhirResource>> ImportedAsync(
        Harness harness, string memberId = "pat-001", string tenant = AcceptanceContext.TenantId) =>
        await harness.Imports.GetImportedResourcesAsync(tenant, memberId);

    // ── Happy path ──────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task P2P02_Replace_ValidatedPackage_BecomesDurableMemberScopedRecord()
    {
        var harness = Build(new StaticPriorPayer(PriorPayerPackages.Full()));

        var result = await harness.Service.InitiateAsync(Request());

        result.Succeeded.Should().BeTrue();
        result.Exchange.Status.Should().Be(PayerToPayerOutboundStatus.Completed);
        result.Exchange.IngestionStatus.Should().Be(PayerToPayerIngestionStatus.Completed);
        result.Exchange.IngestionFailure.Should().Be(PayerToPayerIngestionFailure.None);

        var imported = await ImportedAsync(harness);
        imported.Should().NotBeEmpty();

        // Every row is bound to the exchange's tenant, member, payer, and exchange —
        // the identity CHO established, not anything the peer asserted.
        imported.Should().OnlyContain(r =>
            r.TenantId == AcceptanceContext.TenantId
            && r.MemberId == "pat-001"
            && r.SourcePayerId == TargetPayer
            && r.ExchangeId == result.Exchange.ExchangeId
            && r.RemoteMemberId == RemoteMemberId);

        // Member history is stored: the EOBs and the Encounter the peer sent.
        imported.Where(r => r.Classification == ImportedResourceClass.MemberHistory)
            .Select(r => r.ResourceType).Should().BeEquivalentTo(
                new[] { "Encounter", "ExplanationOfBenefit", "ExplanationOfBenefit" });

        // Administrative context is stored as reference-only.
        imported.Where(r => r.Classification == ImportedResourceClass.AdministrativeReference)
            .Select(r => r.ResourceType).Should().Contain(["Patient", "Coverage", "Provenance"]);

        // Counts are structured on the exchange, not prose.
        result.Exchange.PersistedResourceCount.Should().Be(3);
        result.Exchange.AdministrativeResourceCount.Should().Be(3);   // Patient + Coverage + Provenance
        result.Exchange.DuplicateResourceCount.Should().Be(0);
        result.Exchange.IngestionStartedAtUtc.Should().NotBeNull();
        result.Exchange.IngestionCompletedAtUtc.Should().NotBeNull();

        // Auditable without content.
        result.Audit.IngestionStatus.Should().Be("Completed");
        result.Audit.PersistedResourceCount.Should().Be(3);
    }

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task P2P02_Replace_ImportedData_KeepsItsSourceProvenance()
    {
        var harness = Build(new StaticPriorPayer(PriorPayerPackages.Full()));

        var result = await harness.Service.InitiateAsync(Request());
        var imported = await ImportedAsync(harness);

        // Originating payer, endpoint identity, exchange, receipt time, source
        // resource identity, local member, tenant — all recoverable per resource.
        var eob = imported.Single(r => r.ResourceType == "ExplanationOfBenefit" && r.SourceResourceId == "PRIOR-EOB-1");
        eob.SourcePayerId.Should().Be(TargetPayer);
        eob.SourceEndpointKey.Should().Be("prior-plan-fhir");
        eob.ExchangeId.Should().Be(result.Exchange.ExchangeId);
        eob.ReceivedAtUtc.Should().NotBe(default);
        eob.IngestedAtUtc.Should().NotBe(default);
        eob.RemoteMemberId.Should().Be(RemoteMemberId);

        // The source Provenance the exchange stamped is itself retained.
        imported.Should().Contain(r => r.ResourceType == "Provenance");

        // Imported data is distinguishable from CHO-originated data: it lives in
        // the import store, never in CHO's own member/claims data provider.
        var choMember = await new MockPatientAccessDataProvider().GetMemberAsync("pat-001");
        choMember!.LastName.Should().Be("Smith", "CHO's authoritative member record is untouched by an import");
    }

    // ── Administrative ownership ────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task P2P02_Replace_RemotePatientAndCoverage_DoNotBecomeChoAuthoritativeRecords()
    {
        // The peer sends its own Patient and a prior Coverage. Both are stored as
        // reference-only context; neither may become CHO's member identity or the
        // member's current enrollment.
        var harness = Build(new StaticPriorPayer(PriorPayerPackages.Full()));

        await harness.Service.InitiateAsync(Request());
        var imported = await ImportedAsync(harness);

        var patient = imported.Single(r => r.ResourceType == "Patient");
        patient.Classification.Should().Be(ImportedResourceClass.AdministrativeReference);
        patient.SourceResourceId.Should().Be(RemoteMemberId, "the row keeps the PEER's identity for the Patient");
        patient.MemberId.Should().Be("pat-001", "but it is filed under CHO's own member");

        var coverage = imported.Single(r => r.ResourceType == "Coverage");
        coverage.Classification.Should().Be(ImportedResourceClass.AdministrativeReference);

        // CHO's authoritative coverage for the member is unchanged: the member
        // still holds their CHO-PLAN coverage, and the prior payer's coverage did
        // not overwrite it.
        var choCoverages = await new MockPatientAccessDataProvider()
            .GetCoveragesByMemberIdAsync("pat-001");
        choCoverages.Should().Contain(c => c.PayerId == "CHO-PLAN" && c.Status == "active");
        choCoverages.Should().HaveCount(2, "an import must not add or replace CHO enrollment records");
    }

    // ── Unsupported resource types ──────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task P2P02_Replace_UnsupportedResourceTypes_AreNamedAndArchivedNotSilentlyDropped()
    {
        // The peer sends a Condition and an Observation — types CHO's FHIR surface
        // does not serve today. They must be reported explicitly, and the package
        // must still be archived so nothing is lost.
        var harness = Build(new StaticPriorPayer(PriorPayerPackages.WithUnsupportedTypes()));

        var result = await harness.Service.InitiateAsync(Request());

        result.Succeeded.Should().BeTrue();
        result.Exchange.UnsupportedResourceCount.Should().Be(2);
        result.Exchange.UnsupportedResourceTypes.Should().BeEquivalentTo(new[] { "Condition", "Observation" });

        // Not ingested as member history...
        var imported = await ImportedAsync(harness);
        imported.Should().NotContain(r => r.ResourceType == "Condition" || r.ResourceType == "Observation");

        // ...but preserved verbatim in the archived package.
        var ledger = await harness.Imports.GetLedgerAsync(AcceptanceContext.TenantId, result.Exchange.ExchangeId);
        ledger!.ArchivedPackageJson.Should().NotBeNull();
        ledger.ArchivedPackageJson.Should().Contain("Condition").And.Contain("Observation");
    }

    // ── Idempotency / replay ────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task P2P02_Replace_SamePackageIngestedTwice_DoesNotDuplicateHistory()
    {
        // A second exchange for a NEW transition carrying the same package must
        // land on the same import keys — the member's history does not double.
        var imports = new InMemoryPayerToPayerImportRepository();
        var first = Build(new StaticPriorPayer(PriorPayerPackages.Full()), imports);
        var firstResult = await first.Service.InitiateAsync(Request(transitionKey: "transition-A"));
        var afterFirst = await ImportedAsync(first);

        var second = Build(new StaticPriorPayer(PriorPayerPackages.Full()), imports);
        var secondResult = await second.Service.InitiateAsync(Request(transitionKey: "transition-B"));
        var afterSecond = await ImportedAsync(second);

        firstResult.Succeeded.Should().BeTrue();
        secondResult.Succeeded.Should().BeTrue();
        secondResult.Exchange.ExchangeId.Should().NotBe(firstResult.Exchange.ExchangeId,
            "a different coverage transition is a different exchange");

        // The peer's own resources resolve to the same import keys, so the
        // member's history does not double.
        static IEnumerable<string> PeerResources(IEnumerable<ImportedFhirResource> rows) =>
            rows.Where(r => r.ResourceType != "Provenance").Select(r => r.ImportKey).OrderBy(k => k);

        PeerResources(afterSecond).Should().Equal(PeerResources(afterFirst),
            "the same resources from the same payer must not be stored twice");
        secondResult.Exchange.DuplicateResourceCount.Should().Be(afterFirst.Count - 1,
            "every peer resource was already held with identical content");

        // The one legitimate addition is the second exchange's own source
        // Provenance stamp: each exchange is its own receipt, and collapsing them
        // would lose which exchange delivered what.
        afterSecond.Should().HaveCount(afterFirst.Count + 1);
        afterSecond.Where(r => r.ResourceType == "Provenance").Select(r => r.ExchangeId)
            .Should().BeEquivalentTo(new[] { firstResult.Exchange.ExchangeId, secondResult.Exchange.ExchangeId });
    }

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task P2P02_Replace_ReplayedExchange_ReportsTheCommittedImportWithoutRestaging()
    {
        var harness = Build(new StaticPriorPayer(PriorPayerPackages.Full()));

        var first = await harness.Service.InitiateAsync(Request());
        var replay = await harness.Service.InitiateAsync(Request());

        replay.IsReplay.Should().BeTrue();
        replay.Exchange.ExchangeId.Should().Be(first.Exchange.ExchangeId);
        replay.Exchange.IngestionStatus.Should().Be(PayerToPayerIngestionStatus.Completed);

        // Replaying an already-committed exchange re-stages nothing: the same
        // rows, under the same exchange, with no second copy of anything.
        var imported = await ImportedAsync(harness);
        imported.Should().OnlyContain(r => r.ExchangeId == first.Exchange.ExchangeId);
        imported.Select(r => r.ImportKey).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task P2P02_Replace_SameSourceResourceIdFromTwoPayers_IsNeverMerged()
    {
        // Two payers can legitimately use the same resource id. Deduplicating
        // across payers would fuse two different members' histories.
        var imports = new InMemoryPayerToPayerImportRepository();

        var fromPrior = Build(new StaticPriorPayer(PriorPayerPackages.Full()), imports);
        await fromPrior.Service.InitiateAsync(Request(transitionKey: "t-1"));
        var afterPrior = await ImportedAsync(fromPrior);

        var fromOther = Build(
            new StaticPriorPayer(PriorPayerPackages.Full()), imports, targetPayer: OtherPayer);
        await fromOther.Service.InitiateAsync(Request(targetPayer: OtherPayer, transitionKey: "t-2"));
        var afterBoth = await ImportedAsync(fromOther);

        afterBoth.Should().HaveCount(afterPrior.Count * 2, "each payer's copy is its own record");
        afterBoth.Select(r => r.SourcePayerId).Distinct().Should().BeEquivalentTo(new[] { TargetPayer, OtherPayer });
        afterBoth.Select(r => r.ImportKey).Should().OnlyHaveUniqueItems();
    }

    // ── Failure behaviour ───────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task P2P02_Replace_PersistenceFailure_DoesNotReportACompletedExchange()
    {
        var harness = Build(
            new StaticPriorPayer(PriorPayerPackages.Full()), new FailingImportRepository(failOnStage: true));

        var result = await harness.Service.InitiateAsync(Request());

        result.Succeeded.Should().BeFalse("retrieval alone is not a completed exchange");
        result.Exchange.Status.Should().Be(PayerToPayerOutboundStatus.Failed);
        result.Exchange.Failure.Should().Be(PayerToPayerOutboundFailure.IngestionFailed);
        result.Exchange.IngestionStatus.Should().Be(PayerToPayerIngestionStatus.Failed);
        result.Exchange.IngestionFailure.Should().Be(PayerToPayerIngestionFailure.StagingFailed);

        // The package was received — that much is true and recorded — but nothing
        // is claimed as persisted.
        result.Exchange.ReceivedResourceCount.Should().BeGreaterThan(0);
        result.Exchange.PersistedResourceCount.Should().Be(0);
    }

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task P2P02_Replace_CommitFailure_LeavesTheMemberRecordUntouched()
    {
        // Staging succeeded but the commit did not land: the import must be
        // invisible, because a half-imported package is worse than none.
        var imports = new FailingImportRepository(failOnCommit: true);
        var harness = Build(new StaticPriorPayer(PriorPayerPackages.Full()), imports);

        var result = await harness.Service.InitiateAsync(Request());

        result.Exchange.IngestionFailure.Should().Be(PayerToPayerIngestionFailure.CommitFailed);
        (await ImportedAsync(harness)).Should().BeEmpty("staged rows are not visible without a committed ledger");
    }

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task P2P02_Replace_RetryAfterAnIngestionFailure_CompletesWithoutDuplicating()
    {
        var imports = new FailingImportRepository(failOnCommit: true);
        var harness = Build(new StaticPriorPayer(PriorPayerPackages.Full()), imports);

        var failed = await harness.Service.InitiateAsync(Request());
        failed.Exchange.IngestionFailure.Should().Be(PayerToPayerIngestionFailure.CommitFailed);

        imports.Recover();
        var retried = await harness.Service.InitiateAsync(Request());

        retried.Succeeded.Should().BeTrue();
        retried.Exchange.ExchangeId.Should().Be(failed.Exchange.ExchangeId, "a retry resumes the same exchange");
        retried.Exchange.IngestionStatus.Should().Be(PayerToPayerIngestionStatus.Completed);

        // The re-staged rows landed on the same deterministic keys.
        var imported = await ImportedAsync(harness);
        imported.Select(r => r.ImportKey).Should().OnlyHaveUniqueItems();
        imported.Should().OnlyContain(r => r.ExchangeId == failed.Exchange.ExchangeId);
    }

    // ── Tenant / member safety ──────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task P2P02_Replace_ImportIsFiledUnderTheExchangeContextNotTheBundlesClaims()
    {
        // The peer's Bundle names its own tenant and a different member in the
        // resources it sends. The exchange context is authoritative; the Bundle's
        // claims must not redirect a single row.
        var harness = Build(new StaticPriorPayer(PriorPayerPackages.WithMisleadingIdentifiers()));

        var result = await harness.Service.InitiateAsync(Request());

        result.Succeeded.Should().BeTrue();
        var imported = await ImportedAsync(harness);
        imported.Should().OnlyContain(r =>
            r.TenantId == AcceptanceContext.TenantId && r.MemberId == "pat-001");

        // Nothing was filed under the tenant or member the peer named.
        (await harness.Imports.GetImportedResourcesAsync("attacker-tenant", "pat-001")).Should().BeEmpty();
        (await harness.Imports.GetImportedResourcesAsync(AcceptanceContext.TenantId, "pat-002")).Should().BeEmpty();
    }

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task P2P02_Replace_AnotherTenantsRequest_WritesNothing()
    {
        var harness = Build(new StaticPriorPayer(PriorPayerPackages.Full()), tenant: "other-tenant");

        var result = await harness.Service.InitiateAsync(Request(tenant: "other-tenant"));

        result.Exchange.Failure.Should().Be(PayerToPayerOutboundFailure.TenantMismatch);
        (await harness.Imports.GetImportedResourcesAsync("other-tenant", "pat-001")).Should().BeEmpty();
        (await ImportedAsync(harness)).Should().BeEmpty();
    }

    // ── References between imported resources ───────────────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task P2P02_Replace_IntraPackageReferences_ResolveToTheImportedCopies()
    {
        // The peer's EOB references its own Encounter, once relatively and once as
        // an absolute URL on its own server. Both must resolve to CHO's imported
        // copy — an absolute URL must not survive as a live pointer at the peer.
        var harness = Build(new StaticPriorPayer(PriorPayerPackages.Full()));

        var result = await harness.Service.InitiateAsync(Request());
        var imported = await ImportedAsync(harness);

        var encounter = imported.Single(r => r.ResourceType == "Encounter");
        var expectedReference =
            $"{PayerToPayerReferenceNormalizer.ImportedPrefix}/{encounter.ImportKey}";

        var eobWithRelativeRef = imported.Single(r => r.SourceResourceId == "PRIOR-EOB-1");
        eobWithRelativeRef.ResourceJson.Should().Contain(expectedReference);
        eobWithRelativeRef.ReferencesNormalized.Should().BeTrue();

        var eobWithAbsoluteRef = imported.Single(r => r.SourceResourceId == "PRIOR-EOB-2");
        eobWithAbsoluteRef.ResourceJson.Should().Contain(expectedReference);
        eobWithAbsoluteRef.ResourceJson.Should().NotContain("https://prior-payer.example",
            "an imported resource must not keep pointing at the source payer's server");

        result.Exchange.ReceivedResourceCount.Should().BeGreaterThan(0);
    }

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task P2P02_Replace_ReferencesToResourcesThePeerDidNotSend_AreLeftAlone()
    {
        // The peer's Encounter references a Practitioner it did not include. CHO
        // must not invent a link to a resource it never received.
        var harness = Build(new StaticPriorPayer(PriorPayerPackages.Full()));

        await harness.Service.InitiateAsync(Request());
        var imported = await ImportedAsync(harness);

        var encounter = imported.Single(r => r.ResourceType == "Encounter");
        encounter.ResourceJson.Should().Contain("Practitioner/not-in-package",
            "an unresolvable reference is preserved verbatim, not rewritten or dropped");
    }

    // ── Audit hygiene ───────────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public void P2P02_Replace_IngestionAuditCarriesCountsNotContent()
    {
        var fields = typeof(PayerToPayerOutboundAuditEntry).GetProperties().Select(p => p.Name).ToList();

        fields.Should().Contain(["IngestionStatus", "PersistedResourceCount", "DuplicateResourceCount",
            "UnsupportedResourceCount"]);
        fields.Should().NotContain(n =>
            n.Contains("json", StringComparison.OrdinalIgnoreCase)
            || n.Contains("bundle", StringComparison.OrdinalIgnoreCase)
            || n.Contains("payload", StringComparison.OrdinalIgnoreCase)
            || n.Contains("name", StringComparison.OrdinalIgnoreCase)
            || n.Contains("birth", StringComparison.OrdinalIgnoreCase));
    }

    // ── Test doubles ────────────────────────────────────────────────────────────

    /// <summary>A prior payer that answers both operations with fixed payloads.</summary>
    private sealed class StaticPriorPayer : IPayerToPayerRemoteClient
    {
        private readonly string _exportPayload;

        public StaticPriorPayer(string exportPayload) => _exportPayload = exportPayload;

        public Task<RemoteCallResponse> MatchMemberAsync(
            PayerToPayerEndpoint endpoint, RemoteMemberMatchRequest request, CancellationToken ct = default)
            => Task.FromResult(RemoteCallResponse.Success(PriorPayerPackages.MatchBundle()));

        public Task<RemoteCallResponse> RequestMemberDataAsync(
            PayerToPayerEndpoint endpoint, RemoteMemberDataRequest request, CancellationToken ct = default)
            => Task.FromResult(RemoteCallResponse.Success(_exportPayload));
    }

    /// <summary>An import store whose writes fail, to prove failure is not reported as success.</summary>
    private sealed class FailingImportRepository : IPayerToPayerImportRepository
    {
        private readonly InMemoryPayerToPayerImportRepository _inner = new();
        private bool _failOnStage;
        private bool _failOnCommit;

        public FailingImportRepository(bool failOnStage = false, bool failOnCommit = false)
        {
            _failOnStage = failOnStage;
            _failOnCommit = failOnCommit;
        }

        /// <summary>The store starts working again (retry scenarios).</summary>
        public void Recover()
        {
            _failOnStage = false;
            _failOnCommit = false;
        }

        public Task<PayerToPayerImportLedgerEntry?> GetLedgerAsync(
            string tenantId, string exchangeId, CancellationToken ct = default)
            => _inner.GetLedgerAsync(tenantId, exchangeId, ct);

        public Task<PayerToPayerImportLedgerEntry> OpenLedgerAsync(
            PayerToPayerImportLedgerEntry entry, CancellationToken ct = default)
            => _inner.OpenLedgerAsync(entry, ct);

        public Task<StageOutcome> StageAsync(
            IReadOnlyList<ImportedFhirResource> resources, CancellationToken ct = default)
            => _failOnStage
                ? throw new InvalidOperationException("import store unavailable")
                : _inner.StageAsync(resources, ct);

        public Task CommitAsync(PayerToPayerImportLedgerEntry entry, CancellationToken ct = default)
            => _failOnCommit
                ? throw new InvalidOperationException("import store unavailable")
                : _inner.CommitAsync(entry, ct);

        public Task FailAsync(
            PayerToPayerImportLedgerEntry entry, PayerToPayerIngestionFailure failure, CancellationToken ct = default)
            => _inner.FailAsync(entry, failure, ct);

        public Task<IReadOnlyList<ImportedFhirResource>> GetImportedResourcesAsync(
            string tenantId, string memberId, CancellationToken ct = default)
            => _inner.GetImportedResourcesAsync(tenantId, memberId, ct);
    }

    /// <summary>Real FHIR R4 payloads a conformant prior payer would return.</summary>
    private static class PriorPayerPackages
    {
        private static readonly JsonSerializerOptions FhirJson =
            new JsonSerializerOptions().ForFhir(ModelInfo.ModelInspector);

        public static string MatchBundle() => Serialize(new Bundle
        {
            Type = Bundle.BundleType.Collection,
            Entry = [Entry(new Patient { Id = RemoteMemberId, BirthDate = "1955-07-14" })],
        });

        /// <summary>Patient + Coverage + Encounter + two EOBs (relative and absolute references).</summary>
        public static string Full() => Serialize(new Bundle
        {
            Type = Bundle.BundleType.Collection,
            Entry =
            [
                Entry(new Patient { Id = RemoteMemberId, BirthDate = "1955-07-14" }),
                Entry(new Coverage
                {
                    Id = "PRIOR-COV-1",
                    Status = FinancialResourceStatusCodes.Cancelled,
                    Beneficiary = new ResourceReference($"Patient/{RemoteMemberId}"),
                }),
                Entry(new Encounter
                {
                    Id = "PRIOR-ENC-1",
                    Status = Encounter.EncounterStatus.Finished,
                    Subject = new ResourceReference($"Patient/{RemoteMemberId}"),
                    // A participant the peer did NOT include in the package.
                    Participant =
                    [
                        new Encounter.ParticipantComponent
                        {
                            Individual = new ResourceReference("Practitioner/not-in-package"),
                        },
                    ],
                }),
                Entry(Eob("PRIOR-EOB-1", "Encounter/PRIOR-ENC-1")),
                Entry(Eob("PRIOR-EOB-2", "https://prior-payer.example/fhir/r4/Encounter/PRIOR-ENC-1")),
            ],
        });

        /// <summary>Adds resource types CHO's FHIR surface does not serve.</summary>
        public static string WithUnsupportedTypes() => Serialize(new Bundle
        {
            Type = Bundle.BundleType.Collection,
            Entry =
            [
                Entry(new Patient { Id = RemoteMemberId, BirthDate = "1955-07-14" }),
                Entry(Eob("PRIOR-EOB-1", null)),
                Entry(new Condition
                {
                    Id = "PRIOR-CND-1",
                    Subject = new ResourceReference($"Patient/{RemoteMemberId}"),
                }),
                Entry(new Observation
                {
                    Id = "PRIOR-OBS-1",
                    Status = ObservationStatus.Final,
                    Code = new CodeableConcept("http://loinc.org", "8867-4"),
                    Subject = new ResourceReference($"Patient/{RemoteMemberId}"),
                }),
            ],
        });

        /// <summary>A package whose resources claim another tenant and another member.</summary>
        public static string WithMisleadingIdentifiers() => Serialize(new Bundle
        {
            Type = Bundle.BundleType.Collection,
            Entry =
            [
                Entry(new Patient
                {
                    Id = RemoteMemberId,
                    BirthDate = "1955-07-14",
                    Meta = new Meta { Source = "urn:tenant:attacker-tenant" },
                    Identifier =
                    [
                        new Identifier("urn:cho:tenant", "attacker-tenant"),
                        new Identifier("urn:cho:member", "pat-002"),
                    ],
                }),
                Entry(Eob("PRIOR-EOB-1", null)),
            ],
        });

        private static ExplanationOfBenefit Eob(string id, string? encounterReference)
        {
            var eob = new ExplanationOfBenefit
            {
                Id = id,
                Status = ExplanationOfBenefit.ExplanationOfBenefitStatus.Active,
                Patient = new ResourceReference($"Patient/{RemoteMemberId}"),
            };

            if (encounterReference is not null)
            {
                eob.Item =
                [
                    new ExplanationOfBenefit.ItemComponent
                    {
                        Sequence = 1,
                        Encounter = [new ResourceReference(encounterReference)],
                    },
                ];
            }

            return eob;
        }

        private static Bundle.EntryComponent Entry(Resource resource) =>
            new() { FullUrl = $"{resource.TypeName}/{resource.Id}", Resource = resource };

        private static string Serialize(Bundle bundle) => JsonSerializer.Serialize(bundle, FhirJson);
    }
}
