using CloudHealthOffice.FieldProtection;
using CloudHealthOffice.ProviderService.Tests.TestHelpers;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using ProviderService.Migrations;
using ProviderService.Models;
using ProviderService.Repositories;
using ProviderService.Security;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Security;

/// <summary>
/// Bank routing, account and tax numbers at rest in Mongo: the
/// <c>ProviderBankAccounts</c> records (active, pending, history) and the
/// legacy <see cref="Provider.BankAccount"/> copy on provider documents and
/// version rows are stored encrypted, legacy plaintext is read and re-encrypted
/// on the next write, the migration command is idempotent, and without a key
/// ring nothing is stored.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public class ProviderBankAccountEncryptionAtRestTests : IAsyncLifetime
{
    private const string Tenant = "tenant-enc";
    private const string OtherTenant = "tenant-enc-2";
    private const string Routing = "091000019";
    private const string Account = "111122223333";
    private const string TaxId = "12-3456789";
    private const string NewRouting = "021000089";
    private const string NewAccount = "999988887777";

    private readonly MongoRunnerFixture _mongo;
    private readonly EphemeralDataProtectionProvider _keys = new();
    private IMongoDatabase _database = null!;
    private DefaultHttpContext _ctx = null!;
    private IFieldProtector _protector = null!;

    public ProviderBankAccountEncryptionAtRestTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("provider_bank_enc");
        _ctx = new DefaultHttpContext();
        _ctx.Items["TenantId"] = Tenant;
        _protector = new DataProtectionFieldProtector(_keys, ProviderBankAccountProtection.Purpose);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);

    // ── wiring ─────────────────────────────────────────────────────────

    private IProviderRepository Providers(IFieldProtector? protector = null)
        => new ProtectedProviderRepository(
            new ProviderRepositoryMongo(_database, new HttpContextAccessor { HttpContext = _ctx }, NullLogger<ProviderRepositoryMongo>.Instance),
            protector ?? _protector, NullLogger<ProtectedProviderRepository>.Instance);

    private IProviderBankAccountRepository Records(IFieldProtector? protector = null, ListLogger<ProtectedProviderBankAccountRepository>? log = null)
        => new ProtectedProviderBankAccountRepository(
            new MongoProviderBankAccountRepository(_database, new ConfigurationBuilder().Build()),
            protector ?? _protector, (ILogger<ProtectedProviderBankAccountRepository>?)log ?? NullLogger<ProtectedProviderBankAccountRepository>.Instance);

    private IProviderBankAccountChangeService Changes(IProviderBankAccountRepository records)
        => new ProviderBankAccountChangeService(records, NullLogger<ProviderBankAccountChangeService>.Instance);

    private IMongoCollection<BsonDocument> RawProviders => _database.GetCollection<BsonDocument>("Providers");
    private IMongoCollection<BsonDocument> RawRecords => _database.GetCollection<BsonDocument>("ProviderBankAccounts");

    private static ProviderBankAccount BankAccount(string routing = Routing, string account = Account) => new()
    {
        EftEnabled = true,
        PreferredDisbursementMethod = DisbursementMethod.NachaCredit,
        RoutingNumber = routing,
        AccountNumber = account,
        AccountHolderName = "Sunrise Clinic",
        TaxId = TaxId,
        TaxIdType = TaxIdType.EIN,
    };

    private static Provider Sample(string id, ProviderBankAccount? account = null, string tenant = Tenant) => new()
    {
        Id = id,
        ProviderId = id,
        VersionId = id + "-v1",
        VersionNumber = 1,
        VersionState = ProviderVersionState.Draft,
        TenantId = tenant,
        NPI = "1234567890",
        ProviderType = ProviderType.Individual,
        FirstName = "Jane",
        LastName = "Doe",
        BankAccount = account,
    };

    private static void AssertEncrypted(BsonDocument account, params string[] plaintexts)
    {
        foreach (var field in new[] { "RoutingNumber", "AccountNumber", "TaxId" })
        {
            var value = account[field].AsString;
            value.Should().StartWith("enc:v1:", $"{field} is stored encrypted");
        }
        var raw = account.ToJson();
        foreach (var plaintext in plaintexts) raw.Should().NotContain(plaintext);
    }

    private static Task<BsonDocument> RawAsync(IMongoCollection<BsonDocument> collection, string id)
        => collection.Find(Builders<BsonDocument>.Filter.Eq("_id", id)).FirstAsync();

    // ── ProviderBankAccounts ───────────────────────────────────────────

    [Fact]
    public async Task BankAccountRecord_active_pending_and_history_are_stored_encrypted_and_read_back_in_plaintext()
    {
        var provider = Sample("p-rec");
        var changes = Changes(Records());

        var first = await changes.ProposeAsync(provider, BankAccount(), "proposer", "test");
        await changes.ApproveAsync(provider, first.Id, new BankAccountActor("approver", false), null);
        await changes.ProposeAsync(provider, BankAccount(NewRouting, NewAccount), "proposer", "test");

        var raw = await RawAsync(RawRecords, ProviderBankAccountRecord.KeyFor(Tenant, "p-rec"));
        AssertEncrypted(raw["Active"].AsBsonDocument, Routing, Account, TaxId);
        var pending = raw["Changes"].AsBsonArray.Select(c => c.AsBsonDocument).Single(c => c["Status"] == 0);
        AssertEncrypted(pending["Proposed"].AsBsonDocument, NewRouting, NewAccount, TaxId);
        raw.ToJson().Should().NotContain(Account).And.NotContain(NewAccount).And.NotContain(Routing).And.NotContain(NewRouting);
        raw["Active"]["AccountNumberLast4"].AsString.Should().Be("3333");

        var read = await Records().GetAsync(Tenant, "p-rec");
        read!.Active!.AccountNumber.Should().Be(Account);
        read.Active.RoutingNumber.Should().Be(Routing);
        read.Active.TaxId.Should().Be(TaxId);
        read.Pending!.Proposed!.AccountNumber.Should().Be(NewAccount);
    }

    [Fact]
    public async Task Another_services_key_purpose_cannot_read_the_record()
    {
        var provider = Sample("p-purpose");
        await Changes(Records()).ProposeAsync(provider, BankAccount(), "proposer", "test");

        var sponsorPurpose = new DataProtectionFieldProtector(_keys, "sponsor-service");
        var read = () => Records(sponsorPurpose).GetAsync(Tenant, "p-purpose");

        await read.Should().ThrowAsync<FieldProtectionException>();
    }

    [Fact]
    public async Task Legacy_plaintext_record_is_read_logged_without_numbers_and_reencrypted_on_the_next_write()
    {
        await RawRecords.InsertOneAsync(new BsonDocument
        {
            ["_id"] = ProviderBankAccountRecord.KeyFor(Tenant, "p-legacy"),
            ["TenantId"] = Tenant,
            ["ProviderId"] = "p-legacy",
            ["Active"] = new BsonDocument
            {
                ["EftEnabled"] = true, ["PreferredDisbursementMethod"] = 1, ["RoutingNumber"] = Routing,
                ["AccountNumber"] = Account, ["AccountType"] = 1, ["TaxId"] = TaxId, ["W9OnFile"] = false,
            },
            ["ActiveChangeId"] = ProviderBankAccountRecord.LegacyChangeId,
            ["Changes"] = new BsonArray(),
            ["Revision"] = 1L,
            ["UpdatedAt"] = DateTime.UtcNow,
        });
        var log = new ListLogger<ProtectedProviderBankAccountRepository>();
        var records = Records(log: log);

        var read = await records.GetAsync(Tenant, "p-legacy");
        read!.Active!.AccountNumber.Should().Be(Account);
        log.Entries.Should().Contain(e => e.Message.Contains("p-legacy") && e.Message.Contains("before encryption"));
        log.Entries.Should().NotContain(e => e.Message.Contains(Account) || e.Message.Contains(Routing) || e.Message.Contains(TaxId));

        // Any write of the record (here: a proposal) stores everything encrypted.
        await Changes(records).ProposeAsync(Sample("p-legacy"), BankAccount(NewRouting, NewAccount), "proposer", "test");

        var raw = await RawAsync(RawRecords, ProviderBankAccountRecord.KeyFor(Tenant, "p-legacy"));
        AssertEncrypted(raw["Active"].AsBsonDocument, Routing, Account, TaxId);
        (await Records().GetAsync(Tenant, "p-legacy"))!.Active!.AccountNumber.Should().Be(Account);
    }

    [Fact]
    public async Task Without_a_key_ring_a_record_write_fails_and_nothing_is_stored()
    {
        var changes = Changes(Records(new UnconfiguredFieldProtector()));

        var act = () => changes.ProposeAsync(Sample("p-nokey"), BankAccount(), "proposer", "test");

        await act.Should().ThrowAsync<FieldProtectionException>();
        (await RawRecords.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty)).Should().Be(0);
    }

    // ── provider documents and version rows ────────────────────────────

    [Fact]
    public async Task Provider_row_copy_is_stored_encrypted_read_back_in_plaintext_and_the_caller_keeps_plaintext()
    {
        var draft = Sample("p-row", BankAccount());

        var written = await Providers().CreateDraftAsync(draft);

        draft.BankAccount!.AccountNumber.Should().Be(Account, "the caller's object is not changed");
        written.BankAccount!.AccountNumber.Should().Be(Account);
        var raw = await RawAsync(RawProviders, "p-row");
        AssertEncrypted(raw["BankAccount"].AsBsonDocument, Routing, Account, TaxId);
        raw["BankAccount"]["RoutingNumberLast4"].AsString.Should().Be("0019");

        var read = await Providers().GetVersionAsync("p-row", "p-row-v1");
        read!.BankAccount!.AccountNumber.Should().Be(Account);
        read.BankAccount.TaxId.Should().Be(TaxId);
    }

    [Fact]
    public async Task Activate_and_supersede_store_both_version_rows_encrypted()
    {
        var repo = Providers();
        var v1 = Sample("p-chain", BankAccount());
        await repo.CreateDraftAsync(v1);
        v1.VersionState = ProviderVersionState.Active;
        await repo.ActivateAndSupersedeAsync(v1, predecessor: null);

        var v2 = Sample("p-chain", BankAccount(NewRouting, NewAccount));
        v2.Id = "p-chain-2";
        v2.VersionId = "p-chain-v2";
        v2.VersionNumber = 2;
        await repo.CreateDraftAsync(v2);
        v2.VersionState = ProviderVersionState.Active;
        var predecessor = await repo.GetVersionAsync("p-chain", "p-chain-v1");
        predecessor!.VersionState = ProviderVersionState.Superseded;
        await repo.ActivateAndSupersedeAsync(v2, predecessor);

        AssertEncrypted((await RawAsync(RawProviders, "p-chain"))["BankAccount"].AsBsonDocument, Routing, Account, TaxId);
        AssertEncrypted((await RawAsync(RawProviders, "p-chain-2"))["BankAccount"].AsBsonDocument, NewRouting, NewAccount, TaxId);
        var (versions, _) = await repo.ListVersionsAsync("p-chain", 10, null);
        versions.Select(v => v.BankAccount!.AccountNumber).Should().BeEquivalentTo(new[] { NewAccount, Account });
    }

    [Fact]
    public async Task Legacy_plaintext_provider_row_is_read_as_is_and_reencrypted_on_the_next_write()
    {
        await _database.GetCollection<Provider>("Providers").InsertOneAsync(Sample("p-old", BankAccount()));
        var repo = Providers();

        var read = await repo.GetVersionAsync("p-old", "p-old-v1");
        read!.BankAccount!.AccountNumber.Should().Be(Account);

        await repo.UpdateDraftAsync(read);

        AssertEncrypted((await RawAsync(RawProviders, "p-old"))["BankAccount"].AsBsonDocument, Routing, Account, TaxId);
        (await repo.GetVersionAsync("p-old", "p-old-v1"))!.BankAccount!.AccountNumber.Should().Be(Account);
    }

    [Fact]
    public async Task Without_a_key_ring_a_provider_row_with_numbers_is_not_written_but_one_without_is()
    {
        var repo = Providers(new UnconfiguredFieldProtector());

        var act = () => repo.CreateDraftAsync(Sample("p-nokey-row", BankAccount()));
        await act.Should().ThrowAsync<FieldProtectionException>();
        (await RawProviders.CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("_id", "p-nokey-row"))).Should().Be(0);

        await repo.CreateDraftAsync(Sample("p-nokey-plain"));
        (await RawProviders.CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("_id", "p-nokey-plain"))).Should().Be(1);
    }

    [Fact]
    public async Task A_row_value_that_does_not_decrypt_does_not_fail_the_provider_read_and_is_never_shown()
    {
        await Providers().CreateDraftAsync(Sample("p-lost", BankAccount()));
        var otherKeys = new DataProtectionFieldProtector(new EphemeralDataProtectionProvider(), ProviderBankAccountProtection.Purpose);

        var read = await Providers(otherKeys).GetVersionAsync("p-lost", "p-lost-v1");

        read.Should().NotBeNull();
        read!.BankAccount!.AccountNumber.Should().StartWith("enc:v1:");
        ProviderBankAccountProtection.HasCiphertext(read.BankAccount).Should().BeTrue();
        BankAccountMasking.Mask(read.BankAccount)!.AccountNumberLast4.Should().Be("3333", "last 4 stored at write time");
    }

    // ── migration ──────────────────────────────────────────────────────

    private async Task SeedLegacyPlaintextAsync()
    {
        var rows = _database.GetCollection<Provider>("Providers");
        await rows.InsertOneAsync(Sample("m-1", BankAccount()));
        var superseded = Sample("m-1", BankAccount(NewRouting, NewAccount));
        superseded.Id = "m-1-old";
        superseded.VersionId = "m-1-v0";
        superseded.VersionState = ProviderVersionState.Superseded;
        await rows.InsertOneAsync(superseded);
        await rows.InsertOneAsync(Sample("m-2", BankAccount(), OtherTenant));
        await rows.InsertOneAsync(Sample("m-3")); // no bank account

        await RawRecords.InsertOneAsync(new BsonDocument
        {
            ["_id"] = ProviderBankAccountRecord.KeyFor(Tenant, "m-1"),
            ["TenantId"] = Tenant,
            ["ProviderId"] = "m-1",
            ["Active"] = new BsonDocument { ["RoutingNumber"] = Routing, ["AccountNumber"] = Account, ["TaxId"] = TaxId },
            ["Changes"] = new BsonArray
            {
                new BsonDocument { ["Id"] = "c-1", ["Status"] = 0, ["Proposed"] = new BsonDocument { ["RoutingNumber"] = NewRouting, ["AccountNumber"] = NewAccount, ["TaxId"] = TaxId } },
                new BsonDocument { ["Id"] = "c-0", ["Status"] = 1, ["Proposed"] = new BsonDocument { ["AccountNumberLast4"] = "3333" } },
            },
            ["Revision"] = 3L,
        });
    }

    [Fact]
    public async Task Migration_encrypts_every_plaintext_value_per_tenant_and_is_idempotent()
    {
        await SeedLegacyPlaintextAsync();
        var output = new StringWriter();

        var first = await EncryptProviderBankAccounts.MigrateAsync(_database, _protector, null, "ProviderBankAccounts", dryRun: false, output);

        first.ProvidersEncrypted.Should().Be(3);
        first.RecordsEncrypted.Should().Be(1);
        first.Complete.Should().BeTrue();
        output.ToString().Should().Contain($"tenant {Tenant}:").And.Contain($"tenant {OtherTenant}:");
        output.ToString().Should().NotContain(Account).And.NotContain(Routing).And.NotContain(NewAccount);

        foreach (var id in new[] { "m-1", "m-1-old", "m-2" })
            AssertEncrypted((await RawAsync(RawProviders, id))["BankAccount"].AsBsonDocument, Routing, Account, NewRouting, NewAccount, TaxId);
        var record = await RawAsync(RawRecords, ProviderBankAccountRecord.KeyFor(Tenant, "m-1"));
        AssertEncrypted(record["Active"].AsBsonDocument, Routing, Account, TaxId);
        AssertEncrypted(record["Changes"][0]["Proposed"].AsBsonDocument, NewRouting, NewAccount, TaxId);
        record["Revision"].AsInt64.Should().Be(3, "the service's revision is not bumped");

        // The service reads what the migration wrote.
        (await Records().GetAsync(Tenant, "m-1"))!.Active!.AccountNumber.Should().Be(Account);
        (await Providers().GetVersionAsync("m-1", "m-1-v0"))!.BankAccount!.AccountNumber.Should().Be(NewAccount);

        // A second run changes nothing.
        var before = (await RawProviders.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync()).Select(d => d.ToJson()).ToList();
        var recordBefore = record.ToJson();
        var second = await EncryptProviderBankAccounts.MigrateAsync(_database, _protector, null, "ProviderBankAccounts", dryRun: false, new StringWriter());

        second.ProvidersEncrypted.Should().Be(0);
        second.RecordsEncrypted.Should().Be(0);
        second.ProvidersAlreadyEncrypted.Should().Be(3);
        second.RecordsAlreadyEncrypted.Should().Be(1);
        (await RawProviders.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync()).Select(d => d.ToJson()).Should().Equal(before);
        (await RawAsync(RawRecords, ProviderBankAccountRecord.KeyFor(Tenant, "m-1"))).ToJson().Should().Be(recordBefore);
    }

    [Fact]
    public async Task Migration_can_be_limited_to_one_tenant_and_resumed_for_the_rest()
    {
        await SeedLegacyPlaintextAsync();

        var one = await EncryptProviderBankAccounts.MigrateAsync(_database, _protector, new[] { OtherTenant }, "ProviderBankAccounts", false, new StringWriter());
        one.ProvidersEncrypted.Should().Be(1);
        (await RawAsync(RawProviders, "m-1"))["BankAccount"]["AccountNumber"].AsString.Should().Be(Account, "other tenants untouched");

        var rest = await EncryptProviderBankAccounts.MigrateAsync(_database, _protector, null, "ProviderBankAccounts", false, new StringWriter());
        rest.ProvidersEncrypted.Should().Be(2);
        rest.ProvidersAlreadyEncrypted.Should().Be(1);
        rest.RecordsEncrypted.Should().Be(1);
    }

    [Fact]
    public async Task Migration_dry_run_writes_nothing()
    {
        await SeedLegacyPlaintextAsync();

        var counts = await EncryptProviderBankAccounts.MigrateAsync(_database, _protector, null, "ProviderBankAccounts", dryRun: true, new StringWriter());

        counts.ProvidersEncrypted.Should().Be(3);
        (await RawAsync(RawProviders, "m-1"))["BankAccount"]["AccountNumber"].AsString.Should().Be(Account);
    }

    [Fact]
    public async Task Migration_does_not_overwrite_a_record_the_service_changed_meanwhile()
    {
        await SeedLegacyPlaintextAsync();
        // Simulates the service saving between the migration's read and write:
        // the compare-and-set on the read values and revision does not match.
        var key = ProviderBankAccountRecord.KeyFor(Tenant, "m-1");
        var counts = await EncryptProviderBankAccounts.MigrateAsync(_database, _protector, new[] { Tenant }, "ProviderBankAccounts", false,
            new StringWriter(), beforeWrite: async id =>
            {
                if (id == key)
                    await RawRecords.UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("_id", key), Builders<BsonDocument>.Update.Inc("Revision", 1L));
            });

        counts.RecordConflicts.Should().Be(1);
        counts.Complete.Should().BeFalse();
        (await RawAsync(RawRecords, key))["Active"]["AccountNumber"].AsString.Should().Be(Account, "left for the next run");
        var again = await EncryptProviderBankAccounts.MigrateAsync(_database, _protector, new[] { Tenant }, "ProviderBankAccounts", false, new StringWriter());
        again.RecordsEncrypted.Should().Be(1);
        again.Complete.Should().BeTrue();
    }
}
