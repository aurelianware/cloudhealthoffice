using FhirService.Models.PayerToPayer;
using FhirService.Services.Clinical;
using FhirService.Services.PayerToPayer.Ingestion;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.FhirService.Tests.Services;

/// <summary>
/// Persistence and identity for clinical resources: what the store keeps, which
/// version it serves, and what it refuses to let one member or tenant see of
/// another's.
///
/// The store under test is the SAME object the Payer-to-Payer ingestion commits
/// into, read through <see cref="IClinicalResourceStore"/> — so these are
/// assertions about one store's two faces, not about a projection that could
/// drift from its source.
/// </summary>
public class ClinicalResourceStoreTests
{
    private const string Tenant = "t1";
    private const string Member = "pat-001";
    private const string Payer = "PRIOR-PLAN";

    private static readonly FhirJsonSerializer Serializer = new(new SerializerSettings { Pretty = false });

    // ── Persistence ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ACommittedClinicalResourceIsReadableByItsChoLogicalId()
    {
        var store = new InMemoryPayerToPayerImportRepository();
        await IngestAsync(store, "exchange-1", [Observation("OBS-1")]);

        var stored = await store.GetAsync(Tenant, Member, "Observation", IdFor("Observation", "OBS-1"));

        stored.Should().NotBeNull();
        stored!.MemberId.Should().Be(Member);
        stored.SourcePayerId.Should().Be(Payer);
        stored.SourceResourceId.Should().Be("OBS-1");
        stored.Origin.Should().Be(ClinicalResourceOrigin.Imported);
        stored.ExchangeId.Should().Be("exchange-1");
    }

    [Fact]
    public async Task AStagedButUncommittedExchangeIsInvisible()
    {
        // Freshness rule: Patient and Provider Access read committed state only.
        // A package half-way through ingestion is not the member's record.
        var store = new InMemoryPayerToPayerImportRepository();

        var ledger = await store.OpenLedgerAsync(Ledger("exchange-uncommitted"));
        await store.StageAsync([Row("exchange-uncommitted", Observation("OBS-1"))]);
        ledger.Status.Should().Be(PayerToPayerIngestionStatus.Staging);

        (await store.GetAsync(Tenant, Member, "Observation", IdFor("Observation", "OBS-1")))
            .Should().BeNull();
        (await Search(store, "Observation")).Total.Should().Be(0);
    }

    [Fact]
    public async Task AFailedExchangeNeverBecomesVisible()
    {
        var store = new InMemoryPayerToPayerImportRepository();

        var ledger = await store.OpenLedgerAsync(Ledger("exchange-failed"));
        await store.StageAsync([Row("exchange-failed", Observation("OBS-1"))]);
        await store.FailAsync(ledger, PayerToPayerIngestionFailure.CommitFailed);

        (await Search(store, "Observation")).Total.Should().Be(0);
    }

    // ── Deduplication and versioning ──────────────────────────────────────────

    [Fact]
    public async Task AnExactReplayDoesNotDuplicateAVisibleResource()
    {
        var store = new InMemoryPayerToPayerImportRepository();

        await IngestAsync(store, "exchange-1", [Observation("OBS-1")]);
        await IngestAsync(store, "exchange-2", [Observation("OBS-1")]);

        var page = await Search(store, "Observation");
        page.Total.Should().Be(1, "the identity is the same, so it is one resource with two deliveries");
    }

    [Fact]
    public async Task AChangedVersionSupersedesTheOlderOneAtTheSameId()
    {
        var store = new InMemoryPayerToPayerImportRepository();

        await IngestAsync(store, "exchange-1", [Observation("OBS-1", ObservationStatus.Preliminary)]);
        var before = await store.GetAsync(Tenant, Member, "Observation", IdFor("Observation", "OBS-1"));

        await IngestAsync(store, "exchange-2", [Observation("OBS-1", ObservationStatus.Final)]);
        var after = await store.GetAsync(Tenant, Member, "Observation", IdFor("Observation", "OBS-1"));

        after!.ContentHash.Should().NotBe(before!.ContentHash, "the content changed, so the version changed");
        after.ResourceJson.Should().Contain("\"final\"");
        after.ClinicalId.Should().Be(before.ClinicalId, "and it is still the same resource, at the same URL");

        (await Search(store, "Observation")).Total.Should().Be(1);
    }

    [Fact]
    public async Task TheSameSourceIdFromADifferentPayerIsADifferentResource()
    {
        // Two payers both calling something "OBS-1" must not be merged into one
        // clinical record.
        var store = new InMemoryPayerToPayerImportRepository();

        await IngestAsync(store, "exchange-prior", [Observation("OBS-1")]);
        await IngestAsync(store, "exchange-other", [Observation("OBS-1")], payer: "OTHER-PLAN");

        var page = await Search(store, "Observation");
        page.Total.Should().Be(2);
        page.Items.Select(i => i.SourcePayerId).Should().BeEquivalentTo(new[] { Payer, "OTHER-PLAN" });
        page.Items.Select(i => i.ClinicalId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task TheSameSourceIdInADifferentTenantIsNotVisibleHere()
    {
        var store = new InMemoryPayerToPayerImportRepository();

        await IngestAsync(store, "exchange-t1", [Observation("OBS-1")]);
        await IngestAsync(store, "exchange-t2", [Observation("OBS-1")], tenant: "t2");

        (await Search(store, "Observation")).Total.Should().Be(1);
        (await store.GetAsync("t2", Member, "Observation", IdFor("Observation", "OBS-1")))
            .Should().BeNull("the tenant is inside the identity, so t1's id names nothing in t2");
    }

    [Fact]
    public async Task AnIdBelongingToAnotherMemberDoesNotResolve()
    {
        var store = new InMemoryPayerToPayerImportRepository();

        await IngestAsync(store, "exchange-1", [Observation("OBS-1")]);
        await IngestAsync(store, "exchange-2", [Observation("OBS-2")], member: "pat-002");

        var foreignId = IdFor("Observation", "OBS-2", member: "pat-002");

        (await store.GetAsync(Tenant, Member, "Observation", foreignId))
            .Should().BeNull("the member is a query term, not a check applied after the row is loaded");
    }

    [Fact]
    public async Task ASearchNeverCrossesAResourceTypeBoundary()
    {
        var store = new InMemoryPayerToPayerImportRepository();
        await IngestAsync(store, "exchange-1", [Observation("OBS-1"), Condition("CND-1")]);

        (await Search(store, "Observation")).Items.Should().OnlyContain(i => i.ResourceType == "Observation");
        (await Search(store, "Condition")).Items.Should().OnlyContain(i => i.ResourceType == "Condition");
        (await Search(store, "Procedure")).Total.Should().Be(0);
    }

    [Fact]
    public async Task NonClinicalImportedRowsAreNotServedThroughTheClinicalStore()
    {
        // Administrative context — the prior payer's Patient and Coverage — is
        // stored for reference resolution only. It must not appear as clinical
        // data, and CHO's Patient surface must stay CHO's own.
        var store = new InMemoryPayerToPayerImportRepository();
        await IngestAsync(store, "exchange-1",
        [
            new Patient { Id = "REMOTE-PAT" },
            new Coverage { Id = "REMOTE-COV", Status = FinancialResourceStatusCodes.Cancelled },
            Observation("OBS-1"),
        ]);

        (await Search(store, "Patient")).Total.Should().Be(0);
        (await Search(store, "Coverage")).Total.Should().Be(0);
        (await Search(store, "Observation")).Total.Should().Be(1);
    }

    // ── Paging ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchPagesWithoutLosingOrRepeatingAResource()
    {
        var store = new InMemoryPayerToPayerImportRepository();
        await IngestAsync(store, "exchange-1",
            [.. Enumerable.Range(1, 7).Select(i => (Resource)Observation($"OBS-{i}"))]);

        var first = await Search(store, "Observation", page: 1, count: 3);
        var second = await Search(store, "Observation", page: 2, count: 3);
        var third = await Search(store, "Observation", page: 3, count: 3);

        first.Total.Should().Be(7, "the total is across all pages, not within one");
        first.Items.Should().HaveCount(3);
        second.Items.Should().HaveCount(3);
        third.Items.Should().HaveCount(1);

        first.Items.Concat(second.Items).Concat(third.Items)
            .Select(i => i.ClinicalId).Should().OnlyHaveUniqueItems().And.HaveCount(7);
    }

    // ── Reference resolution ──────────────────────────────────────────────────

    [Fact]
    public async Task ReferenceTypeLookupIsScopedToTheTenantAndMember()
    {
        var store = new InMemoryPayerToPayerImportRepository();
        await IngestAsync(store, "exchange-1", [Observation("OBS-1")]);
        await IngestAsync(store, "exchange-2", [Observation("OBS-2")], member: "pat-002");

        var mine = ImportKey("Observation", "OBS-1", Member, Tenant, Payer);
        var theirs = ImportKey("Observation", "OBS-2", "pat-002", Tenant, Payer);

        var resolved = await store.GetResourceTypesAsync(Tenant, Member, [mine, theirs]);

        resolved.Should().ContainKey(mine);
        resolved.Should().NotContainKey(theirs,
            "resolving a reference must not confirm that another member holds the target");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Task<ClinicalResourcePage> Search(
        IClinicalResourceStore store, string resourceType, int page = 1, int count = 20)
        => store.SearchAsync(new ClinicalResourceQuery
        {
            TenantId = Tenant,
            MemberId = Member,
            ResourceType = resourceType,
            Page = page,
            Count = count,
        });

    /// <summary>
    /// Runs the PRODUCTION ingestion service against the store, so what these
    /// tests read is what a real exchange would have written.
    /// </summary>
    private static Task IngestAsync(
        IPayerToPayerImportRepository store,
        string exchangeId,
        Resource[] resources,
        string tenant = Tenant,
        string member = Member,
        string payer = Payer)
    {
        var ingestion = new PayerToPayerPackageIngestionService(
            store, NullLogger<PayerToPayerPackageIngestionService>.Instance, new ClinicalPayloadValidator());

        return ingestion.IngestAsync(
            new PayerToPayerIngestionContext
            {
                TenantId = tenant,
                MemberId = member,
                SourcePayerId = payer,
                ExchangeId = exchangeId,
                RemoteMemberId = "remote-1",
            },
            new PayerToPayerReceivedPackage
            {
                RemoteMemberId = "remote-1",
                Bundle = new Bundle
                {
                    Type = Bundle.BundleType.Collection,
                    Entry = [.. resources.Select(r => new Bundle.EntryComponent { Resource = r })],
                },
            });
    }

    private static Observation Observation(
        string id, ObservationStatus status = ObservationStatus.Final) => new()
    {
        Id = id,
        Status = status,
        Code = new CodeableConcept("http://loinc.org", "8867-4"),
        Subject = new ResourceReference("Patient/remote-1"),
    };

    private static Condition Condition(string id) => new()
    {
        Id = id,
        Code = new CodeableConcept("http://hl7.org/fhir/sid/icd-10-cm", "E11.9"),
        Subject = new ResourceReference("Patient/remote-1"),
    };

    private static string ImportKey(
        string type, string sourceId, string member, string tenant, string payer)
        => PayerToPayerImportPolicy.ImportKey(tenant, member, payer, type, sourceId);

    private static string IdFor(
        string type, string sourceId, string member = Member, string tenant = Tenant, string payer = Payer)
        => ClinicalResourceIdentity.ForImported(ImportKey(type, sourceId, member, tenant, payer));

    private static PayerToPayerImportLedgerEntry Ledger(string exchangeId) => new()
    {
        ExchangeId = exchangeId,
        TenantId = Tenant,
        MemberId = Member,
        SourcePayerId = Payer,
    };

    private static ImportedFhirResource Row(string exchangeId, Resource resource)
    {
        var json = Serializer.SerializeToString(resource);
        return new ImportedFhirResource
        {
            ImportKey = PayerToPayerImportPolicy.ImportKey(
                Tenant, Member, Payer, resource.TypeName, resource.Id!),
            TenantId = Tenant,
            MemberId = Member,
            SourcePayerId = Payer,
            ExchangeId = exchangeId,
            ResourceType = resource.TypeName,
            SourceResourceId = resource.Id!,
            RemoteMemberId = "remote-1",
            Classification = ImportedResourceClass.ClinicalRecord,
            ResourceJson = json,
            ContentHash = PayerToPayerImportPolicy.ContentHash(json),
        };
    }
}
