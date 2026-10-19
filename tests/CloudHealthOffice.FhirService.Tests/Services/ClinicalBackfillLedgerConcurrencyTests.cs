using CloudHealthOffice.Testing.Mongo;
using FhirService.Models.PayerToPayer;
using FhirService.Services.Clinical;
using FhirService.Services.PayerToPayer.Ingestion;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CloudHealthOffice.FhirService.Tests.Services;

/// <summary>
/// The clinical backfill's ledger write on MongoDB (EphemeralMongo).
/// <see cref="MongoPayerToPayerImportRepository.UpdateBackfilledLedgerAsync"/>
/// replaced the whole ledger entry with the copy the backfill's cursor had read,
/// so an ingestion that re-opened, committed or failed the exchange in between
/// was overwritten: a Staging or Failed exchange was turned back to Completed,
/// and a newer commit's archived package and counts were replaced with the old
/// ones. The backfill now sets only the counts and marker it owns, and only on
/// the commit it read.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class ClinicalBackfillLedgerConcurrencyTests : IAsyncLifetime
{
    private const string Tenant = "t1";
    private const string Member = "pat-001";
    private const string Payer = "PRIOR-PLAN";
    private const string Exchange = "exchange-legacy";

    private static readonly FhirJsonSerializer Serializer = new(new SerializerSettings { Pretty = false });

    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;
    private MongoPayerToPayerImportRepository _store = null!;

    public ClinicalBackfillLedgerConcurrencyTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("p2p_backfill_ledger");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MongoDb:DatabaseName"] = _database.DatabaseNamespace.DatabaseName,
            })
            .Build();
        _store = new MongoPayerToPayerImportRepository(
            _mongo.Client, configuration, NullLogger<MongoPayerToPayerImportRepository>.Instance);
        return System.Threading.Tasks.Task.CompletedTask;
    }

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);

    [Fact]
    public async Task A_backfill_does_not_turn_an_exchange_reopened_meanwhile_back_to_completed()
    {
        await SeedLegacyExchangeAsync();
        var store = new InterleavingBackfillStore(_store, async () =>
        {
            // A concurrent ingestion of the same exchange re-opens it.
            var current = await _store.GetLedgerAsync(Tenant, Exchange);
            await _store.OpenLedgerAsync(current!);
        });

        await Backfill(store).RunAsync();

        var ledger = await _store.GetLedgerAsync(Tenant, Exchange);
        ledger!.Status.Should().Be(PayerToPayerIngestionStatus.Staging, "the backfill's stale copy is not written back");
        ledger.ClinicalBackfilledAtUtc.Should().BeNull("the counts belong to a commit that is no longer current");
    }

    [Fact]
    public async Task A_backfill_does_not_overwrite_a_newer_commit_with_the_archive_and_counts_it_read()
    {
        await SeedLegacyExchangeAsync();
        var store = new InterleavingBackfillStore(_store, async () =>
        {
            var current = await _store.GetLedgerAsync(Tenant, Exchange);
            await _store.OpenLedgerAsync(current!);
            current!.ArchivedPackageJson = "{\"resourceType\":\"Bundle\",\"type\":\"collection\"}";
            current.Counts = new PayerToPayerIngestionCounts { Received = 7, Persisted = 7 };
            await System.Threading.Tasks.Task.Delay(5);
            await _store.CommitAsync(current);
        });

        await Backfill(store).RunAsync();

        var ledger = await _store.GetLedgerAsync(Tenant, Exchange);
        ledger!.Status.Should().Be(PayerToPayerIngestionStatus.Completed);
        ledger.ArchivedPackageJson.Should().Contain("\"type\":\"collection\"").And.NotContain("CND-1");
        ledger.Counts.Received.Should().Be(7);
        ledger.Counts.Persisted.Should().Be(7);
        ledger.ClinicalBackfilledAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task A_backfill_with_no_concurrent_writer_records_its_counts_and_keeps_everything_else()
    {
        await SeedLegacyExchangeAsync();
        var before = await _database.GetCollection<BsonDocument>(MongoPayerToPayerImportRepository.LedgerCollectionName)
            .Find(new BsonDocument("exchangeId", Exchange)).FirstAsync();

        await Backfill(_store).RunAsync();

        var ledger = await _store.GetLedgerAsync(Tenant, Exchange);
        ledger!.Status.Should().Be(PayerToPayerIngestionStatus.Completed);
        ledger.ClinicalBackfilledAtUtc.Should().NotBeNull();
        ledger.Counts.Clinical.Should().Be(2);
        ledger.Counts.Unsupported.Should().Be(0);
        ledger.Counts.UnsupportedTypes.Should().BeEmpty();
        ledger.Counts.Received.Should().Be(2, "counts the backfill does not own are kept");
        ledger.ArchivedPackageJson.Should().Be(before["archivedPackageJson"].AsString);
        ledger.CompletedAtUtc.Should().Be(before["completedAtUtc"].ToUniversalTime());
        ledger.StartedAtUtc.Should().Be(before["startedAtUtc"].ToUniversalTime());
    }

    [Fact]
    public async Task Backfilling_a_missing_ledger_entry_writes_nothing()
    {
        await _store.UpdateBackfilledLedgerAsync(new PayerToPayerImportLedgerEntry
        {
            TenantId = Tenant, ExchangeId = "nope", MemberId = Member, SourcePayerId = Payer,
            Status = PayerToPayerIngestionStatus.Completed,
            ClinicalBackfilledAtUtc = DateTime.UtcNow,
        });

        (await _store.GetLedgerAsync(Tenant, "nope")).Should().BeNull();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>An exchange committed before clinical serving existed: archive only, no clinical rows.</summary>
    private async Task SeedLegacyExchangeAsync()
    {
        var bundle = new Bundle
        {
            Type = Bundle.BundleType.Collection,
            Entry =
            [
                new Bundle.EntryComponent { Resource = new Condition
                {
                    Id = "CND-1",
                    Code = new CodeableConcept("http://hl7.org/fhir/sid/icd-10-cm", "E11.9"),
                    Subject = new ResourceReference("Patient/remote-1"),
                } },
                new Bundle.EntryComponent { Resource = new Observation
                {
                    Id = "OBS-1",
                    Status = ObservationStatus.Final,
                    Code = new CodeableConcept("http://loinc.org", "8867-4"),
                    Subject = new ResourceReference("Patient/remote-1"),
                } },
            ],
        };

        var ledger = await _store.OpenLedgerAsync(new PayerToPayerImportLedgerEntry
        {
            ExchangeId = Exchange,
            TenantId = Tenant,
            MemberId = Member,
            SourcePayerId = Payer,
            StartedAtUtc = DateTime.UtcNow,
            ArchivedPackageJson = Serializer.SerializeToString(bundle),
            Counts = new PayerToPayerIngestionCounts
            {
                Received = 2,
                Unsupported = 2,
                UnsupportedTypes = ["Condition", "Observation"],
            },
        });
        await _store.CommitAsync(ledger);
    }

    private ClinicalBackfillService Backfill(IClinicalBackfillStore backfillStore) => new(
        backfillStore,
        _store,
        new ClinicalPayloadValidator(new ClinicalPayloadLimits { MaxResourceBytes = 1024 * 1024 }),
        Options.Create(new ClinicalBackfillOptions { Enabled = true }),
        NullLogger<ClinicalBackfillService>.Instance);

    /// <summary>
    /// Runs <paramref name="interleave"/> (a concurrent ingestion) after the
    /// backfill read the ledger entry and before it writes it back.
    /// </summary>
    private sealed class InterleavingBackfillStore(IClinicalBackfillStore inner, Func<System.Threading.Tasks.Task> interleave)
        : IClinicalBackfillStore
    {
        public IAsyncEnumerable<PayerToPayerImportLedgerEntry> CommittedLedgerEntriesAsync(
            int batchSize, CancellationToken ct = default) => inner.CommittedLedgerEntriesAsync(batchSize, ct);

        public async System.Threading.Tasks.Task UpdateBackfilledLedgerAsync(
            PayerToPayerImportLedgerEntry entry, CancellationToken ct = default)
        {
            await interleave();
            await inner.UpdateBackfilledLedgerAsync(entry, ct);
        }
    }
}
