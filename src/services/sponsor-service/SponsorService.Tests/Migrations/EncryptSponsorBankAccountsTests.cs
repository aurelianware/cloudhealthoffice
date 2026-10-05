using System.Runtime.CompilerServices;
using System.Text.Json;
using CloudHealthOffice.FieldProtection;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using SponsorService.Migrations;
using SponsorService.Models;
using SponsorService.Repositories;

namespace SponsorService.Tests.Migrations;

/// <summary>
/// sponsor-service --encrypt-bank-accounts: every sponsor billing account
/// number and bank-account record number stored in plaintext or as enc:v1 is
/// stored as enc:v2 bound to its record; idempotent, dry-run, conflicts left
/// for the next run. Store-neutral core against in-memory stores, and the
/// Mongo stores against a real mongod.
/// </summary>
public class EncryptSponsorBankAccountsTests
{
    internal const string Tenant = "tenant-sp";
    internal const string OtherTenant = "tenant-sp-2";
    internal const string Routing = "091000019";
    internal const string Account = "111122223333";
    internal const string Billing = "BILL-998877";

    private readonly EphemeralDataProtectionProvider _keys = new();
    private readonly IFieldProtector _protector;

    public EncryptSponsorBankAccountsTests() => _protector = new DataProtectionFieldProtector(_keys, "sponsor-service");

    // ── in-memory stores (what Mongo or Cosmos hold) ─────────────────────

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal sealed class InMemorySponsorRows : ISponsorRowStore
    {
        public Dictionary<string, (string Json, int Version)> Rows { get; } = new();
        public Action<string>? BeforeWrite { get; set; }

        public void Put(Sponsor s) => Rows[s.Id] = (JsonSerializer.Serialize(s, Json), Rows.TryGetValue(s.Id, out var r) ? r.Version + 1 : 1);
        public Sponsor Get(string id) => JsonSerializer.Deserialize<Sponsor>(Rows[id].Json, Json)!;

        public async IAsyncEnumerable<StoredSponsor> ListWithBillingAccountAsync(string? tenant, [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var (id, (json, version)) in Rows.OrderBy(r => r.Key).ToList())
            {
                var s = JsonSerializer.Deserialize<Sponsor>(json, Json)!;
                if (string.IsNullOrEmpty(s.BillingInfo?.BillingAccountNumber)) continue;
                if (tenant != null && s.TenantId != tenant) continue;
                await Task.Yield();
                yield return new StoredSponsor(s, version.ToString());
            }
        }

        public Task<bool> SetBillingAccountIfUnchangedAsync(StoredSponsor row, string stored, CancellationToken ct)
        {
            BeforeWrite?.Invoke(row.Sponsor.Id);
            if (Rows[row.Sponsor.Id].Version.ToString() != row.ETag) return Task.FromResult(false);
            var current = Get(row.Sponsor.Id);
            current.BillingInfo!.BillingAccountNumber = stored;
            Put(current);
            return Task.FromResult(true);
        }
    }

    internal sealed class InMemoryRecords : ISponsorBankAccountRecordStore
    {
        public Dictionary<string, string> Docs { get; } = new();
        public Action<string>? BeforeWrite { get; set; }

        public void Put(SponsorBankAccountRecord r)
        {
            r.Id = SponsorBankAccountRecord.KeyFor(r.TenantId, r.GroupNumber);
            Docs[r.Id] = JsonSerializer.Serialize(r, Json);
        }

        public SponsorBankAccountRecord Get(string tenant, string group)
            => JsonSerializer.Deserialize<SponsorBankAccountRecord>(Docs[SponsorBankAccountRecord.KeyFor(tenant, group)], Json)!;

        public async IAsyncEnumerable<SponsorBankAccountRecord> ListAsync(string? tenant, [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var json in Docs.OrderBy(d => d.Key).Select(d => d.Value).ToList())
            {
                var r = JsonSerializer.Deserialize<SponsorBankAccountRecord>(json, Json)!;
                if (tenant != null && r.TenantId != tenant) continue;
                await Task.Yield();
                yield return r;
            }
        }

        public Task<bool> SaveAsync(SponsorBankAccountRecord record, long expectedRevision, CancellationToken ct)
        {
            BeforeWrite?.Invoke(record.Id);
            if (Get(record.TenantId, record.GroupNumber).Revision != expectedRevision) return Task.FromResult(false);
            record.Revision = expectedRevision + 1;
            Put(record);
            return Task.FromResult(true);
        }
    }

    // ── sample data ──────────────────────────────────────────────────────

    internal static Sponsor SponsorWith(string id, string? billing, string tenant = Tenant) => new()
    {
        Id = id,
        TenantId = tenant,
        GroupNumber = "G-" + id,
        EmployerName = "Acme " + id,
        BillingInfo = new BillingInfo { BillingAccountNumber = billing },
    };

    internal static SponsorBankAccountRecord RecordWith(string group, string? routing, string? account, string tenant = Tenant)
    {
        var record = new SponsorBankAccountRecord
        {
            TenantId = tenant,
            GroupNumber = group,
            SponsorId = "s-" + group,
            Active = new SponsorBankAccountDetails { EftEnabled = true, RoutingNumber = routing, AccountNumber = account },
            Revision = 3,
        };
        record.Changes.Add(new SponsorBankAccountChange
        {
            TenantId = tenant,
            GroupNumber = group,
            Status = SponsorBankAccountChangeStatus.Pending,
            Proposed = new SponsorBankAccountDetails { RoutingNumber = routing, AccountNumber = account },
        });
        return record;
    }

    internal static FieldProtectionContext BillingContext(Sponsor s) => ProtectedSponsorRepository.Context(s);

    internal static FieldProtectionContext RecordContext(SponsorBankAccountRecord r, string field)
        => new(r.TenantId, "group:" + r.GroupNumber, field);

    /// <summary>Plaintext and enc:v1 sponsors and records, in two tenants, plus one already bound.</summary>
    private (InMemorySponsorRows Rows, InMemoryRecords Records) Seeded()
    {
        var rows = new InMemorySponsorRows();
        rows.Put(SponsorWith("s-plain", Billing));
        rows.Put(SponsorWith("s-v1", _protector.Protect(Billing)));
        var bound = SponsorWith("s-v2", null);
        bound.BillingInfo!.BillingAccountNumber = _protector.Protect(Billing, BillingContext(bound));
        rows.Put(bound);
        rows.Put(SponsorWith("s-none", null));
        rows.Put(SponsorWith("s-other", Billing, OtherTenant));

        var records = new InMemoryRecords();
        records.Put(RecordWith("g-plain", Routing, Account));
        records.Put(RecordWith("g-v1", _protector.Protect(Routing), _protector.Protect(Account)));
        records.Put(RecordWith("g-other", Routing, Account, OtherTenant));
        return (rows, records);
    }

    private void AssertBound(InMemorySponsorRows rows, string id)
    {
        var s = rows.Get(id);
        s.BillingInfo!.BillingAccountNumber.Should().StartWith("enc:v2:").And.NotContain(Billing);
        _protector.Unprotect(s.BillingInfo.BillingAccountNumber, BillingContext(s)).Should().Be(Billing);
    }

    private void AssertBound(SponsorBankAccountRecord r)
    {
        foreach (var account in new[] { r.Active!, r.Changes[0].Proposed! })
        {
            account.RoutingNumber.Should().StartWith("enc:v2:");
            account.AccountNumber.Should().StartWith("enc:v2:");
            _protector.Unprotect(account.RoutingNumber, RecordContext(r, "routingNumber")).Should().Be(Routing);
            _protector.Unprotect(account.AccountNumber, RecordContext(r, "accountNumber")).Should().Be(Account);
            account.RoutingNumberLast4.Should().Be("0019");
            account.AccountNumberLast4.Should().Be("3333");
        }
        JsonSerializer.Serialize(r).Should().NotContain(Routing).And.NotContain(Account);
    }

    // ── store-neutral core ───────────────────────────────────────────────

    [Fact]
    public async Task Binds_plaintext_and_v1_everywhere_and_a_second_run_changes_nothing()
    {
        var (rows, records) = Seeded();
        var output = new StringWriter();

        var first = await EncryptSponsorBankAccounts.MigrateStoresAsync(rows, records, _protector, null, dryRun: false, output);

        first.Complete.Should().BeTrue();
        first.SponsorsScanned.Should().Be(4, "s-none has no billing account number");
        first.SponsorsEncrypted.Should().Be(3);
        first.SponsorsAlreadyEncrypted.Should().Be(1);
        first.RecordsEncrypted.Should().Be(3);
        foreach (var id in new[] { "s-plain", "s-v1", "s-v2", "s-other" }) AssertBound(rows, id);
        AssertBound(records.Get(Tenant, "g-plain"));
        AssertBound(records.Get(Tenant, "g-v1"));
        AssertBound(records.Get(OtherTenant, "g-other"));
        output.ToString().Should().Contain($"tenant {Tenant}:").And.Contain($"tenant {OtherTenant}:")
            .And.NotContain(Billing).And.NotContain(Account).And.NotContain(Routing);

        var second = await EncryptSponsorBankAccounts.MigrateStoresAsync(rows, records, _protector, null, dryRun: false, new StringWriter());
        second.SponsorsEncrypted.Should().Be(0);
        second.RecordsEncrypted.Should().Be(0);
        second.SponsorsAlreadyEncrypted.Should().Be(4);
        second.RecordsAlreadyEncrypted.Should().Be(3);
    }

    [Fact]
    public async Task Dry_run_counts_and_writes_nothing()
    {
        var (rows, records) = Seeded();
        var before = (rows.Rows.ToDictionary(r => r.Key, r => r.Value), records.Docs.ToDictionary(d => d.Key, d => d.Value));

        var counts = await EncryptSponsorBankAccounts.MigrateStoresAsync(rows, records, _protector, null, dryRun: true, new StringWriter());

        counts.SponsorsEncrypted.Should().Be(3);
        counts.RecordsEncrypted.Should().Be(3);
        rows.Rows.Should().BeEquivalentTo(before.Item1);
        records.Docs.Should().BeEquivalentTo(before.Item2);
    }

    [Fact]
    public async Task Only_the_named_tenant_is_processed()
    {
        var (rows, records) = Seeded();

        var counts = await EncryptSponsorBankAccounts.MigrateStoresAsync(rows, records, _protector, new[] { OtherTenant }, false, new StringWriter());

        counts.SponsorsEncrypted.Should().Be(1);
        counts.RecordsEncrypted.Should().Be(1);
        AssertBound(rows, "s-other");
        rows.Get("s-plain").BillingInfo!.BillingAccountNumber.Should().Be(Billing, "another tenant's row is left alone");
        records.Get(Tenant, "g-plain").Active!.AccountNumber.Should().Be(Account);
    }

    [Fact]
    public async Task A_document_changed_meanwhile_is_a_conflict_left_for_the_next_run()
    {
        var (rows, records) = Seeded();
        rows.BeforeWrite = id => { if (id == "s-plain") { var s = rows.Get(id); s.EmployerName = "changed"; rows.Put(s); } };
        records.BeforeWrite = id =>
        {
            if (id != SponsorBankAccountRecord.KeyFor(Tenant, "g-plain")) return;
            var r = records.Get(Tenant, "g-plain");
            r.Revision++;
            records.Put(r);
        };

        var raced = await EncryptSponsorBankAccounts.MigrateStoresAsync(rows, records, _protector, new[] { Tenant }, false, new StringWriter());

        raced.Complete.Should().BeFalse();
        raced.SponsorConflicts.Should().Be(1);
        raced.RecordConflicts.Should().Be(1);
        rows.Get("s-plain").BillingInfo!.BillingAccountNumber.Should().Be(Billing, "the conflicting write did not happen");

        rows.BeforeWrite = null;
        records.BeforeWrite = null;
        var again = await EncryptSponsorBankAccounts.MigrateStoresAsync(rows, records, _protector, new[] { Tenant }, false, new StringWriter());
        again.Complete.Should().BeTrue();
        again.SponsorsEncrypted.Should().Be(1);
        again.RecordsEncrypted.Should().Be(1);
        AssertBound(rows, "s-plain");
        AssertBound(records.Get(Tenant, "g-plain"));
    }

    [Fact]
    public async Task Works_with_RejectPlaintext_and_RejectUnbound_on_and_a_value_that_does_not_decrypt_is_a_failure()
    {
        var (rows, records) = Seeded();
        var strict = new DataProtectionFieldProtector(_keys, "sponsor-service", rejectPlaintext: true, rejectUnbound: true);
        rows.Put(SponsorWith("s-foreign", new DataProtectionFieldProtector(_keys, "provider-service").Protect(Billing)));

        var counts = await EncryptSponsorBankAccounts.MigrateStoresAsync(rows, records, strict, new[] { Tenant }, false, new StringWriter());

        counts.Failures.Should().Be(1, "another service's ciphertext does not decrypt here");
        counts.Complete.Should().BeFalse();
        counts.SponsorsEncrypted.Should().Be(2);
        counts.RecordsEncrypted.Should().Be(2);
        AssertBound(rows, "s-plain");
        AssertBound(rows, "s-v1");
        AssertBound(records.Get(Tenant, "g-v1"));
    }

    // ── Mongo stores, real mongod ────────────────────────────────────────

    [Collection(MongoRunnerFixture.CollectionName)]
    public class Mongo : IAsyncLifetime
    {
        private readonly MongoRunnerFixture _mongo;
        private readonly EphemeralDataProtectionProvider _keys = new();
        private readonly IFieldProtector _protector;
        private readonly IConfiguration _config = new ConfigurationBuilder().Build();
        private IMongoDatabase _database = null!;

        public Mongo(MongoRunnerFixture mongo)
        {
            _mongo = mongo;
            _protector = new DataProtectionFieldProtector(_keys, "sponsor-service");
        }

        public Task InitializeAsync()
        {
            _database = _mongo.CreateDatabase("sponsor_bank_enc");
            return Task.CompletedTask;
        }

        public Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);

        private async Task SeedAsync()
        {
            var sponsors = _database.GetCollection<Sponsor>("Sponsors");
            await sponsors.InsertManyAsync(new[]
            {
                SponsorWith("s-plain", Billing),
                SponsorWith("s-v1", _protector.Protect(Billing)),
                SponsorWith("s-none", null),
                SponsorWith("s-other", Billing, OtherTenant),
            });
            var records = _database.GetCollection<SponsorBankAccountRecord>("SponsorBankAccounts");
            foreach (var r in new[] { RecordWith("g-plain", Routing, Account), RecordWith("g-v1", _protector.Protect(Routing), _protector.Protect(Account)) })
            {
                r.Id = SponsorBankAccountRecord.KeyFor(r.TenantId, r.GroupNumber);
                await records.InsertOneAsync(r);
            }
        }

        private Task<EncryptSponsorBankAccounts.Counts> MigrateAsync(IFieldProtector? protector = null, IReadOnlyCollection<string>? tenants = null)
            => EncryptSponsorBankAccounts.MigrateStoresAsync(
                new MongoSponsorRowStore(_database, _config), new MongoSponsorBankAccountRecordStore(_database, _config),
                protector ?? _protector, tenants, dryRun: false, new StringWriter());

        [Fact]
        public async Task Binds_stored_values_and_the_service_reads_them_back()
        {
            await SeedAsync();

            var first = await MigrateAsync();

            first.Complete.Should().BeTrue();
            first.SponsorsEncrypted.Should().Be(3);
            first.RecordsEncrypted.Should().Be(2);

            // Raw documents: enc:v2 only, no number anywhere.
            var rawSponsors = await _database.GetCollection<BsonDocument>("Sponsors").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
            foreach (var doc in rawSponsors.Where(d => d["_id"] != "s-none"))
            {
                doc["BillingInfo"]["BillingAccountNumber"].AsString.Should().StartWith("enc:v2:");
                doc.ToJson().Should().NotContain(Billing);
            }
            var rawRecords = await _database.GetCollection<BsonDocument>("SponsorBankAccounts").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
            foreach (var doc in rawRecords)
            {
                doc["Active"]["AccountNumber"].AsString.Should().StartWith("enc:v2:");
                doc["Changes"][0]["Proposed"]["RoutingNumber"].AsString.Should().StartWith("enc:v2:");
                doc["Revision"].ToInt64().Should().Be(4, "saved through the service's revision-checked save");
                doc.ToJson().Should().NotContain(Account).And.NotContain(Routing);
            }

            // The service's own repositories, with RejectPlaintext and RejectUnbound on, read them.
            var strict = new DataProtectionFieldProtector(_keys, "sponsor-service", rejectPlaintext: true, rejectUnbound: true);
            var sponsors = new ProtectedSponsorRepository(
                new SponsorRepositoryMongo(_database, _config, NullLogger<SponsorRepositoryMongo>.Instance),
                strict, NullLogger<ProtectedSponsorRepository>.Instance);
            (await sponsors.GetByIdAsync(Tenant, "s-plain"))!.BillingInfo!.BillingAccountNumber.Should().Be(Billing);
            (await sponsors.GetByIdAsync(Tenant, "s-v1"))!.BillingInfo!.BillingAccountNumber.Should().Be(Billing);
            var record = (await new MongoSponsorBankAccountRepository(_database, _config).GetAsync(Tenant, "g-v1"))!;
            strict.Unprotect(record.Active!.AccountNumber, RecordContext(record, "accountNumber")).Should().Be(Account);
            record.Active.AccountNumberLast4.Should().Be("3333");

            var second = await MigrateAsync();
            second.SponsorsEncrypted.Should().Be(0);
            second.RecordsEncrypted.Should().Be(0);
            second.SponsorsAlreadyEncrypted.Should().Be(3);
            second.RecordsAlreadyEncrypted.Should().Be(2);
        }

        [Fact]
        public async Task A_sponsor_changed_since_it_was_read_is_not_overwritten()
        {
            await SeedAsync();
            var store = new MongoSponsorRowStore(_database, _config);
            var listed = new List<StoredSponsor>();
            await foreach (var row in store.ListWithBillingAccountAsync(Tenant, default)) listed.Add(row);
            var plain = listed.Single(r => r.Sponsor.Id == "s-plain");

            // The service stores a new number meanwhile.
            await _database.GetCollection<Sponsor>("Sponsors").UpdateOneAsync(
                Builders<Sponsor>.Filter.Eq(s => s.Id, "s-plain"),
                Builders<Sponsor>.Update.Set(s => s.BillingInfo!.BillingAccountNumber, "NEWER"));

            (await store.SetBillingAccountIfUnchangedAsync(plain, "enc:v2:stale", default)).Should().BeFalse();
            (await _database.GetCollection<Sponsor>("Sponsors").Find(s => s.Id == "s-plain").FirstAsync())
                .BillingInfo!.BillingAccountNumber.Should().Be("NEWER");
        }

        private sealed class Env(string name) : IHostEnvironment
        {
            public string EnvironmentName { get; set; } = name;
            public string ApplicationName { get; set; } = "tests";
            public string ContentRootPath { get; set; } = Path.GetTempPath();
            public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        }

        [Fact]
        public async Task The_command_runs_against_Mongo_and_exits_0_when_done()
        {
            var keyDir = Path.Combine(Path.GetTempPath(), "cho-sp-mig-" + Guid.NewGuid().ToString("N"));
            try
            {
                var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MongoDb:ConnectionString"] = _mongo.ConnectionString,
                    ["MongoDb:DatabaseName"] = _database.DatabaseNamespace.DatabaseName,
                    ["FieldProtection:KeyRing:LocalDirectory"] = keyDir,
                }).Build();
                await _database.GetCollection<Sponsor>("Sponsors").InsertOneAsync(SponsorWith("s-cli", Billing));
                var env = new Env("Development");

                (await EncryptSponsorBankAccounts.RunAsync(new[] { EncryptSponsorBankAccounts.Switch, "--dry-run" }, config, env)).Should().Be(0);
                (await _database.GetCollection<Sponsor>("Sponsors").Find(s => s.Id == "s-cli").FirstAsync())
                    .BillingInfo!.BillingAccountNumber.Should().Be(Billing, "dry run");

                (await EncryptSponsorBankAccounts.RunAsync(new[] { EncryptSponsorBankAccounts.Switch }, config, env)).Should().Be(0);
                (await _database.GetCollection<Sponsor>("Sponsors").Find(s => s.Id == "s-cli").FirstAsync())
                    .BillingInfo!.BillingAccountNumber.Should().StartWith("enc:v2:");

                // Scoped databases need --tenant; no database configured cannot start.
                var scoped = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MongoDb:ConnectionString"] = _mongo.ConnectionString,
                    ["MongoDb:UseTenantScoping"] = "true",
                    ["FieldProtection:KeyRing:LocalDirectory"] = keyDir,
                }).Build();
                (await EncryptSponsorBankAccounts.RunAsync(new[] { EncryptSponsorBankAccounts.Switch }, scoped, env)).Should().Be(1);
                var none = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["FieldProtection:KeyRing:LocalDirectory"] = keyDir,
                }).Build();
                (await EncryptSponsorBankAccounts.RunAsync(new[] { EncryptSponsorBankAccounts.Switch }, none, env)).Should().Be(1);
                // Outside Development without a key ring: cannot start.
                (await EncryptSponsorBankAccounts.RunAsync(new[] { EncryptSponsorBankAccounts.Switch }, config, new Env("Production"))).Should().Be(1);
            }
            finally
            {
                try { Directory.Delete(keyDir, true); } catch { }
            }
        }
    }
}
