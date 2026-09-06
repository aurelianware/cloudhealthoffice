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
/// The migration that makes clinical data CHO ALREADY HOLDS readable, without
/// asking an operator to re-run a prior payer exchange for data the archive
/// already contains.
///
/// Each test sets up the pre-PAT-02 world honestly: an exchange ingested while
/// Condition and Observation were still classified Unsupported, so they exist in
/// the ledger's archived package and nowhere else.
/// </summary>
public class ClinicalBackfillServiceTests
{
    private const string Tenant = "t1";
    private const string Member = "pat-001";
    private const string Payer = "PRIOR-PLAN";

    private static readonly FhirJsonSerializer Serializer = new(new SerializerSettings { Pretty = false });

    [Fact]
    public async Task ClinicalDataHeldOnlyInAnArchivedPackageBecomesReadable()
    {
        var store = new InMemoryPayerToPayerImportRepository();
        await SeedLegacyExchangeAsync(store, "exchange-legacy");

        // Precondition: the archive has it, the read path does not.
        (await Search(store, "Condition")).Total.Should().Be(0);

        var report = await Backfill(store).RunAsync();

        report.ExchangesBackfilled.Should().Be(1);
        report.ResourcesStaged.Should().Be(2);

        (await Search(store, "Condition")).Total.Should().Be(1);
        (await Search(store, "Observation")).Total.Should().Be(1);
    }

    [Fact]
    public async Task ABackfilledResourceGetsTheSameIdentityARealImportWouldHaveGivenIt()
    {
        // This is what makes the backfill safe to combine with live exchanges: a
        // later package carrying an updated Condition supersedes the backfilled
        // one at the same URL instead of appearing as a second resource.
        var store = new InMemoryPayerToPayerImportRepository();
        await SeedLegacyExchangeAsync(store, "exchange-legacy");

        await Backfill(store).RunAsync();

        var expected = ClinicalResourceIdentity.ForImported(
            PayerToPayerImportPolicy.ImportKey(Tenant, Member, Payer, "Condition", "CND-1"));

        (await store.GetAsync(Tenant, Member, "Condition", expected)).Should().NotBeNull();
    }

    [Fact]
    public async Task RunningItTwiceChangesNothingTheSecondTime()
    {
        var store = new InMemoryPayerToPayerImportRepository();
        await SeedLegacyExchangeAsync(store, "exchange-legacy");

        await Backfill(store).RunAsync();
        var before = await Search(store, "Condition");

        await Backfill(store).RunAsync();
        var after = await Search(store, "Condition");

        after.Total.Should().Be(before.Total);
        after.Items[0].ClinicalId.Should().Be(before.Items[0].ClinicalId);
        after.Items[0].ContentHash.Should().Be(before.Items[0].ContentHash);
    }

    [Fact]
    public async Task ALaterRealExchangeSupersedesABackfilledResourceRatherThanDuplicatingIt()
    {
        var store = new InMemoryPayerToPayerImportRepository();
        await SeedLegacyExchangeAsync(store, "exchange-legacy");
        await Backfill(store).RunAsync();

        // The payer sends the Condition again, changed.
        var updated = new Condition
        {
            Id = "CND-1",
            ClinicalStatus = new CodeableConcept(
                "http://terminology.hl7.org/CodeSystem/condition-clinical", "resolved"),
            Code = new CodeableConcept("http://hl7.org/fhir/sid/icd-10-cm", "E11.9"),
            Subject = new ResourceReference("Patient/remote-1"),
        };

        await new PayerToPayerPackageIngestionService(
                store, NullLogger<PayerToPayerPackageIngestionService>.Instance,
                new ClinicalPayloadValidator())
            .IngestAsync(Context("exchange-new"), Package(updated));

        var page = await Search(store, "Condition");
        page.Total.Should().Be(1);
        page.Items[0].ResourceJson.Should().Contain("resolved");
    }

    [Fact]
    public async Task AnExchangeThatNeverCommittedIsNotPublishedByTheBackfill()
    {
        // The backfill must not become a way to make a package visible that the
        // original ingestion refused.
        var store = new InMemoryPayerToPayerImportRepository();

        var ledger = await store.OpenLedgerAsync(new PayerToPayerImportLedgerEntry
        {
            ExchangeId = "exchange-failed",
            TenantId = Tenant,
            MemberId = Member,
            SourcePayerId = Payer,
            ArchivedPackageJson = Serializer.SerializeToString(Package(LegacyCondition(), LegacyObservation()).Bundle),
        });
        await store.FailAsync(ledger, PayerToPayerIngestionFailure.CommitFailed);

        var report = await Backfill(store).RunAsync();

        report.ExchangesExamined.Should().Be(0);
        (await Search(store, "Condition")).Total.Should().Be(0);
    }

    [Fact]
    public async Task TheBindingComesFromTheLedgerEntry_NotFromThePackage()
    {
        // The archived package is the peer's own bytes and may name anyone. The
        // exchange's own tenant/member binding is what a backfilled row is filed
        // under, exactly as at ingestion time.
        var store = new InMemoryPayerToPayerImportRepository();

        var hostile = new Condition
        {
            Id = "CND-1",
            Code = new CodeableConcept("http://hl7.org/fhir/sid/icd-10-cm", "E11.9"),
            Subject = new ResourceReference("Patient/pat-999"),
        };
        await SeedLegacyExchangeAsync(store, "exchange-legacy", hostile);

        await Backfill(store).RunAsync();

        (await Search(store, "Condition")).Items.Should()
            .OnlyContain(i => i.MemberId == Member && i.TenantId == Tenant);
        (await store.SearchAsync(new ClinicalResourceQuery
        {
            TenantId = Tenant, MemberId = "pat-999", ResourceType = "Condition",
        })).Total.Should().Be(0);
    }

    [Fact]
    public async Task MemberHistoryAndAdministrativeRowsAreLeftExactlyAsTheExchangeCommittedThem()
    {
        // Non-destructive: the backfill only adds clinical rows.
        var store = new InMemoryPayerToPayerImportRepository();

        await new PayerToPayerPackageIngestionService(
                store, NullLogger<PayerToPayerPackageIngestionService>.Instance,
                new ClinicalPayloadValidator())
            .IngestAsync(Context("exchange-1"), Package(
                new Encounter { Id = "ENC-1", Status = Encounter.EncounterStatus.Finished },
                new Patient { Id = "REMOTE-PAT" }));

        var before = await store.GetImportedResourcesAsync(Tenant, Member);

        await Backfill(store).RunAsync();

        var after = await store.GetImportedResourcesAsync(Tenant, Member);
        after.Select(r => r.ImportKey).Should().BeEquivalentTo(before.Select(r => r.ImportKey));
        after.Select(r => r.ContentHash).Should().BeEquivalentTo(before.Select(r => r.ContentHash));
    }

    [Fact]
    public async Task ADryRunReportsWhatItWouldDoAndWritesNothing()
    {
        var store = new InMemoryPayerToPayerImportRepository();
        await SeedLegacyExchangeAsync(store, "exchange-legacy");

        var report = await Backfill(store, dryRun: true).RunAsync();

        report.ResourcesStaged.Should().Be(2);
        (await Search(store, "Condition")).Total.Should().Be(0);
    }

    [Fact]
    public async Task TheLedgerRecordsThatTheseClinicalRowsCameFromTheBackfill()
    {
        // The counts are brought up to date — leaving "unsupported: Condition" on
        // an exchange whose Conditions CHO now serves would make the record
        // untrue — and the marker keeps "these arrived later" answerable.
        var store = new InMemoryPayerToPayerImportRepository();
        await SeedLegacyExchangeAsync(store, "exchange-legacy");

        await Backfill(store).RunAsync();

        var ledger = await store.GetLedgerAsync(Tenant, "exchange-legacy");
        ledger!.Counts.Clinical.Should().Be(2);
        ledger.Counts.Unsupported.Should().Be(0);
        ledger.Counts.UnsupportedTypes.Should().BeEmpty();
        ledger.ClinicalBackfilledAtUtc.Should().NotBeNull();
        ledger.ArchivedPackageJson.Should().NotBeNullOrEmpty("the archive is never discarded");
    }

    [Fact]
    public async Task ATypeStillOutsideTheInventoryStaysUnsupportedAfterTheBackfill()
    {
        var store = new InMemoryPayerToPayerImportRepository();
        await SeedLegacyExchangeAsync(store, "exchange-legacy",
            LegacyCondition(),
            new RiskAssessment { Id = "RSK-1", Status = ObservationStatus.Final });

        await Backfill(store).RunAsync();

        var ledger = await store.GetLedgerAsync(Tenant, "exchange-legacy");
        ledger!.Counts.UnsupportedTypes.Should().BeEquivalentTo(new[] { "RiskAssessment" });
        (await Search(store, "Condition")).Total.Should().Be(1);
    }

    [Fact]
    public async Task AResourceTheValidatorRefusesIsCountedAndNamed_NotSilentlyDropped()
    {
        var store = new InMemoryPayerToPayerImportRepository();
        await SeedLegacyExchangeAsync(store, "exchange-legacy",
            new Condition
            {
                Id = "CND-1",
                Note = [new Annotation { Text = new Markdown(new string('n', 4096)) }],
            });

        var report = await Backfill(store, maxBytes: 256).RunAsync();

        report.ResourcesRejected.Should().Be(1);
        var ledger = await store.GetLedgerAsync(Tenant, "exchange-legacy");
        ledger!.Counts.RejectedReasons.Should()
            .ContainSingle().Which.Should().Be($"Condition:{ClinicalPayloadRejection.Oversized}");
    }

    [Fact]
    public async Task AnArchivedPackageThatCannotBeReadIsSkippedRatherThanFailingTheRun()
    {
        var store = new InMemoryPayerToPayerImportRepository();

        var ledger = await store.OpenLedgerAsync(new PayerToPayerImportLedgerEntry
        {
            ExchangeId = "exchange-corrupt",
            TenantId = Tenant,
            MemberId = Member,
            SourcePayerId = Payer,
            ArchivedPackageJson = "{ not a bundle",
        });
        await store.CommitAsync(ledger);
        await SeedLegacyExchangeAsync(store, "exchange-good");

        var report = await Backfill(store).RunAsync();

        report.ExchangesExamined.Should().Be(2);
        report.ExchangesBackfilled.Should().Be(1, "one bad archive must not stop the rest");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// An exchange as it would have been committed BEFORE clinical serving
    /// existed: the package archived verbatim, and no clinical rows staged.
    /// </summary>
    private static async Task SeedLegacyExchangeAsync(
        InMemoryPayerToPayerImportRepository store, string exchangeId, params Resource[] resources)
    {
        var package = Package(resources.Length > 0
            ? resources
            : [LegacyCondition(), LegacyObservation()]);

        var ledger = await store.OpenLedgerAsync(new PayerToPayerImportLedgerEntry
        {
            ExchangeId = exchangeId,
            TenantId = Tenant,
            MemberId = Member,
            SourcePayerId = Payer,
            ArchivedPackageJson = Serializer.SerializeToString(package.Bundle),
            Counts = new PayerToPayerIngestionCounts
            {
                Received = package.Bundle.Entry.Count,
                Unsupported = package.Bundle.Entry.Count,
                UnsupportedTypes = [.. package.Bundle.Entry
                    .Select(e => e.Resource.TypeName).Distinct().OrderBy(t => t, StringComparer.Ordinal)],
            },
        });

        await store.CommitAsync(ledger);
    }

    private static ClinicalBackfillService Backfill(
        InMemoryPayerToPayerImportRepository store, bool dryRun = false, int maxBytes = 1024 * 1024)
        => new(
            store,
            store,
            new ClinicalPayloadValidator(new ClinicalPayloadLimits { MaxResourceBytes = maxBytes }),
            Options.Create(new ClinicalBackfillOptions { Enabled = true, DryRun = dryRun }),
            NullLogger<ClinicalBackfillService>.Instance);

    private static Task<ClinicalResourcePage> Search(IClinicalResourceStore store, string resourceType)
        => store.SearchAsync(new ClinicalResourceQuery
        {
            TenantId = Tenant,
            MemberId = Member,
            ResourceType = resourceType,
        });

    private static PayerToPayerIngestionContext Context(string exchangeId) => new()
    {
        TenantId = Tenant,
        MemberId = Member,
        SourcePayerId = Payer,
        ExchangeId = exchangeId,
        RemoteMemberId = "remote-1",
    };

    private static PayerToPayerReceivedPackage Package(params Resource[] resources) => new()
    {
        RemoteMemberId = "remote-1",
        Bundle = new Bundle
        {
            Type = Bundle.BundleType.Collection,
            Entry = [.. resources.Select(r => new Bundle.EntryComponent { Resource = r })],
        },
    };

    private static Condition LegacyCondition() => new()
    {
        Id = "CND-1",
        Code = new CodeableConcept("http://hl7.org/fhir/sid/icd-10-cm", "E11.9"),
        Subject = new ResourceReference("Patient/remote-1"),
    };

    private static Observation LegacyObservation() => new()
    {
        Id = "OBS-1",
        Status = ObservationStatus.Final,
        Code = new CodeableConcept("http://loinc.org", "8867-4"),
        Subject = new ResourceReference("Patient/remote-1"),
    };
}
