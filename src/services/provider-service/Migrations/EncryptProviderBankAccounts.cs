using CloudHealthOffice.FieldProtection;
using MongoDB.Bson;
using MongoDB.Driver;
using ProviderService.Repositories;
using ProviderService.Security;

namespace ProviderService.Migrations;

/// <summary>
/// One-off operator command: encrypts the bank numbers that were stored before
/// encryption at rest existed, per tenant, in
/// <list type="bullet">
///   <item>provider documents and version rows (<c>Providers</c>,
///   <c>BankAccount.RoutingNumber / AccountNumber / TaxId</c>);</item>
///   <item>bank-account records (<c>ProviderBankAccounts</c>: the active
///   account and every change's proposed and previous account).</item>
/// </list>
///
/// <para>
/// Usage (with provider-service's own configuration: <c>MongoDb</c> and
/// <c>FieldProtection</c>, the same key ring the service uses):
/// <c>dotnet provider-service.dll --encrypt-bank-accounts [--tenant &lt;id&gt;]... [--dry-run]</c>
/// or <c>dotnet run --project src/services/provider-service -- --encrypt-bank-accounts</c>.
/// Without <c>--tenant</c> every tenant found in the two collections is processed
/// (with <c>MongoDb:UseTenantScoping</c>, <c>--tenant</c> is required: one database per tenant).
/// </para>
///
/// <para>
/// Idempotent and resumable: a value already encrypted is left alone, and each
/// document is updated on its own with a compare-and-set filter (the values it
/// read and, for bank-account records, the record revision). A document changed
/// by the running service in between is counted as a conflict and left to the
/// next run (the service itself stores it encrypted on that write). Stop it at
/// any point and run it again. Exit code 0 when nothing is left in plaintext, 2
/// when a conflict or a failure needs another run, 1 when it cannot start (no
/// key ring, no Mongo). Never prints a number.
/// </para>
///
/// <para>
/// Mongo only (what the deployments use). A Cosmos deployment has no command:
/// its values are re-encrypted on their next write.
/// </para>
/// </summary>
public static class EncryptProviderBankAccounts
{
    public const string Switch = "--encrypt-bank-accounts";
    public const string ProvidersCollection = "Providers";

    private static readonly string[] SecretFields = { "RoutingNumber", "AccountNumber", "TaxId" };

    /// <summary>Counts for one tenant (or every tenant).</summary>
    public sealed class Counts
    {
        public int ProvidersScanned { get; set; }
        public int ProvidersEncrypted { get; set; }
        public int ProvidersAlreadyEncrypted { get; set; }
        public int ProviderConflicts { get; set; }
        public int RecordsScanned { get; set; }
        public int RecordsEncrypted { get; set; }
        public int RecordsAlreadyEncrypted { get; set; }
        public int RecordConflicts { get; set; }
        public int Failures { get; set; }

        public bool Complete => ProviderConflicts == 0 && RecordConflicts == 0 && Failures == 0;

        public void Add(Counts other)
        {
            ProvidersScanned += other.ProvidersScanned;
            ProvidersEncrypted += other.ProvidersEncrypted;
            ProvidersAlreadyEncrypted += other.ProvidersAlreadyEncrypted;
            ProviderConflicts += other.ProviderConflicts;
            RecordsScanned += other.RecordsScanned;
            RecordsEncrypted += other.RecordsEncrypted;
            RecordsAlreadyEncrypted += other.RecordsAlreadyEncrypted;
            RecordConflicts += other.RecordConflicts;
            Failures += other.Failures;
        }

        public override string ToString()
            => $"provider rows: scanned {ProvidersScanned}, encrypted {ProvidersEncrypted}, already encrypted {ProvidersAlreadyEncrypted}, " +
               $"conflicts {ProviderConflicts}; bank-account records: scanned {RecordsScanned}, encrypted {RecordsEncrypted}, " +
               $"already encrypted {RecordsAlreadyEncrypted}, conflicts {RecordConflicts}; failures {Failures}";
    }

    public static async Task<int> RunAsync(string[] args, IConfiguration configuration, IHostEnvironment environment)
    {
        Console.WriteLine("Provider bank numbers: encrypt values stored before encryption at rest");

        var services = new ServiceCollection();
        FieldProtectionKeyRing ring;
        try
        {
            ring = services.AddChoFieldProtection(configuration, environment, ProviderBankAccountProtection.Purpose);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"ABORT: {ex.Message}");
            return 1;
        }
        if (ring == FieldProtectionKeyRing.None)
        {
            Console.Error.WriteLine("ABORT: " + UnconfiguredFieldProtector.Message);
            return 1;
        }
        Console.WriteLine($"Key ring: {ring}");

        var connectionString = configuration["MongoDb:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine("ABORT: MongoDb:ConnectionString is not configured. This command covers Mongo deployments; " +
                                    "a Cosmos deployment re-encrypts each value on its next write.");
            return 1;
        }

        var tenants = ArgValues(args, "--tenant");
        var dryRun = args.Contains("--dry-run");
        var baseName = configuration["MongoDb:DatabaseName"] ?? "CloudHealthOffice";
        var scoped = configuration.GetValue<bool>("MongoDb:UseTenantScoping", false);
        if (scoped && tenants.Count == 0)
        {
            Console.Error.WriteLine("ABORT: MongoDb:UseTenantScoping is on (one database per tenant): name each tenant with --tenant <id>.");
            return 1;
        }

        await using var provider = services.BuildServiceProvider();
        var protector = provider.GetRequiredService<IFieldProtector>();
        var client = new MongoClient(connectionString);
        var bankCollection = configuration["MongoDb:ProviderBankAccountsCollection"] ?? MongoProviderBankAccountRepository.DefaultCollectionName;

        Counts total;
        if (scoped)
        {
            total = new Counts();
            foreach (var tenant in tenants)
            {
                var db = client.GetDatabase($"{baseName}_{SanitizeTenantForDatabase(tenant)}");
                total.Add(await MigrateAsync(db, protector, new[] { tenant }, bankCollection, dryRun, Console.Out));
            }
        }
        else
        {
            total = await MigrateAsync(client.GetDatabase(baseName), protector, tenants.Count == 0 ? null : tenants,
                bankCollection, dryRun, Console.Out);
        }

        Console.WriteLine($"TOTAL{(dryRun ? " (dry run, nothing written)" : string.Empty)}: {total}");
        if (!total.Complete)
            Console.WriteLine("Some documents were changed while this ran or failed: run the command again.");
        return total.Complete ? 0 : 2;
    }

    /// <summary>
    /// Encrypts the plaintext bank numbers of <paramref name="tenants"/> (every
    /// tenant in the collections when null) in <paramref name="database"/>.
    /// <paramref name="beforeWrite"/> runs with a document's id just before its
    /// compare-and-set write (tests use it to simulate a concurrent writer).
    /// </summary>
    public static async Task<Counts> MigrateAsync(
        IMongoDatabase database,
        IFieldProtector protector,
        IReadOnlyCollection<string>? tenants,
        string bankCollectionName,
        bool dryRun,
        TextWriter output,
        Func<string, Task>? beforeWrite = null,
        CancellationToken ct = default)
    {
        var providers = database.GetCollection<BsonDocument>(ProvidersCollection);
        var records = database.GetCollection<BsonDocument>(bankCollectionName);

        var tenantList = tenants?.ToList() ?? await AllTenantsAsync(providers, records, ct);
        var total = new Counts();
        foreach (var tenant in tenantList)
        {
            var counts = new Counts();
            await EncryptProvidersAsync(providers, protector, tenant, dryRun, counts, beforeWrite, ct);
            await EncryptRecordsAsync(records, protector, tenant, dryRun, counts, beforeWrite, ct);
            await output.WriteLineAsync($"tenant {Display(tenant)}: {counts}");
            total.Add(counts);
        }
        return total;
    }

    // ── provider documents and version rows ─────────────────────────────

    private static async Task EncryptProvidersAsync(
        IMongoCollection<BsonDocument> providers, IFieldProtector protector, string tenant, bool dryRun, Counts counts,
        Func<string, Task>? beforeWrite, CancellationToken ct)
    {
        var f = Builders<BsonDocument>.Filter;
        var filter = f.And(TenantFilter(tenant), f.Type("BankAccount", BsonType.Document));
        using var cursor = await providers.Find(filter)
            .Project(Builders<BsonDocument>.Projection.Include("_id").Include("BankAccount"))
            .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
            .ToCursorAsync(ct);

        while (await cursor.MoveNextAsync(ct))
        {
            foreach (var doc in cursor.Current)
            {
                counts.ProvidersScanned++;
                try
                {
                    var plan = Plan(protector, doc["BankAccount"].AsBsonDocument, "BankAccount");
                    if (plan.IsEmpty)
                    {
                        counts.ProvidersAlreadyEncrypted++;
                        continue;
                    }
                    if (dryRun)
                    {
                        counts.ProvidersEncrypted++;
                        continue;
                    }

                    var match = f.And(new[] { f.Eq("_id", doc["_id"]) }.Concat(plan.Expected.Select(e => f.Eq(e.Path, e.Value))));
                    var update = Builders<BsonDocument>.Update.Combine(plan.Sets.Select(s => Builders<BsonDocument>.Update.Set(s.Path, s.Value)));
                    if (beforeWrite != null) await beforeWrite(doc["_id"].ToString()!);
                    var result = await providers.UpdateOneAsync(match, update, cancellationToken: ct);
                    if (result.ModifiedCount == 1) counts.ProvidersEncrypted++;
                    else counts.ProviderConflicts++;
                }
                catch (Exception ex) when (ex is FieldProtectionException or MongoException or InvalidCastException)
                {
                    counts.Failures++;
                    await Console.Error.WriteLineAsync(
                        $"provider row {doc["_id"]} in tenant {Display(tenant)} failed: {ex.GetType().Name}");
                }
            }
        }
    }

    // ── ProviderBankAccounts records ───────────────────────────────────

    private static async Task EncryptRecordsAsync(
        IMongoCollection<BsonDocument> records, IFieldProtector protector, string tenant, bool dryRun, Counts counts,
        Func<string, Task>? beforeWrite, CancellationToken ct)
    {
        var f = Builders<BsonDocument>.Filter;
        using var cursor = await records.Find(TenantFilter(tenant))
            .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
            .ToCursorAsync(ct);

        while (await cursor.MoveNextAsync(ct))
        {
            foreach (var doc in cursor.Current)
            {
                counts.RecordsScanned++;
                try
                {
                    var plan = new UpdatePlan();
                    if (doc.TryGetValue("Active", out var active) && active.IsBsonDocument)
                        plan.Merge(Plan(protector, active.AsBsonDocument, "Active"));
                    if (doc.TryGetValue("Changes", out var changes) && changes.IsBsonArray)
                    {
                        var i = 0;
                        foreach (var change in changes.AsBsonArray)
                        {
                            if (change.IsBsonDocument)
                            {
                                foreach (var part in new[] { "Proposed", "PreviousAccount" })
                                {
                                    if (change.AsBsonDocument.TryGetValue(part, out var account) && account.IsBsonDocument)
                                        plan.Merge(Plan(protector, account.AsBsonDocument, $"Changes.{i}.{part}"));
                                }
                            }
                            i++;
                        }
                    }

                    if (plan.IsEmpty)
                    {
                        counts.RecordsAlreadyEncrypted++;
                        continue;
                    }
                    if (dryRun)
                    {
                        counts.RecordsEncrypted++;
                        continue;
                    }

                    // Compare-and-set on the revision the service's own saves use, and on the values read.
                    var conditions = new List<FilterDefinition<BsonDocument>> { f.Eq("_id", doc["_id"]) };
                    conditions.Add(doc.TryGetValue("Revision", out var revision) ? f.Eq("Revision", revision) : f.Exists("Revision", false));
                    conditions.AddRange(plan.Expected.Select(e => f.Eq(e.Path, e.Value)));
                    var update = Builders<BsonDocument>.Update.Combine(plan.Sets.Select(s => Builders<BsonDocument>.Update.Set(s.Path, s.Value)));
                    if (beforeWrite != null) await beforeWrite(doc["_id"].ToString()!);
                    var result = await records.UpdateOneAsync(f.And(conditions), update, cancellationToken: ct);
                    if (result.ModifiedCount == 1) counts.RecordsEncrypted++;
                    else counts.RecordConflicts++;
                }
                catch (Exception ex) when (ex is FieldProtectionException or MongoException or InvalidCastException)
                {
                    counts.Failures++;
                    await Console.Error.WriteLineAsync(
                        $"bank-account record {doc["_id"]} in tenant {Display(tenant)} failed: {ex.GetType().Name}");
                }
            }
        }
    }

    // ── helpers ────────────────────────────────────────────────────────

    private sealed record FieldValue(string Path, BsonValue Value);

    private sealed class UpdatePlan
    {
        public List<FieldValue> Expected { get; } = new();
        public List<FieldValue> Sets { get; } = new();
        public bool IsEmpty => Sets.Count == 0;

        public void Merge(UpdatePlan other)
        {
            Expected.AddRange(other.Expected);
            Sets.AddRange(other.Sets);
        }
    }

    /// <summary>What to change in one stored account: each plaintext secret encrypted, last 4 filled in when missing.</summary>
    private static UpdatePlan Plan(IFieldProtector protector, BsonDocument account, string path)
    {
        var plan = new UpdatePlan();
        foreach (var field in SecretFields)
        {
            if (!account.TryGetValue(field, out var value) || !value.IsString) continue;
            var plaintext = value.AsString;
            if (string.IsNullOrEmpty(plaintext) || protector.IsProtected(plaintext)) continue;

            plan.Expected.Add(new FieldValue($"{path}.{field}", value));
            plan.Sets.Add(new FieldValue($"{path}.{field}", protector.Protect(plaintext)!));

            var last4Field = field + "Last4";
            if (field != "TaxId" && (!account.TryGetValue(last4Field, out var last4) || last4.IsBsonNull))
                plan.Sets.Add(new FieldValue($"{path}.{last4Field}", plaintext.Length >= 4 ? plaintext[^4..] : plaintext));
        }
        return plan;
    }

    private static FilterDefinition<BsonDocument> TenantFilter(string tenant)
    {
        var f = Builders<BsonDocument>.Filter;
        return tenant.Length == 0
            ? f.Or(f.Exists("TenantId", false), f.Eq("TenantId", BsonNull.Value), f.Eq("TenantId", string.Empty))
            : f.Eq("TenantId", tenant);
    }

    private static async Task<List<string>> AllTenantsAsync(
        IMongoCollection<BsonDocument> providers, IMongoCollection<BsonDocument> records, CancellationToken ct)
    {
        var tenants = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var collection in new[] { providers, records })
        {
            using var cursor = await collection.DistinctAsync<BsonValue>("TenantId", FilterDefinition<BsonDocument>.Empty, cancellationToken: ct);
            foreach (var value in await cursor.ToListAsync(ct))
                tenants.Add(value.IsString ? value.AsString : string.Empty);
        }
        // Rows without a tenant ("") are encrypted too; they are listed as "(none)".
        tenants.Add(string.Empty);
        return tenants.ToList();
    }

    private static List<string> ArgValues(string[] args, string name)
    {
        var values = new List<string>();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name && !string.IsNullOrWhiteSpace(args[i + 1])) values.Add(args[i + 1]);
        }
        return values;
    }

    private static string SanitizeTenantForDatabase(string tenantId)
    {
        // As MongoDbConnectionFactory names a tenant's database.
        foreach (var c in new[] { '/', '\\', '.', ' ', '"', '$', '*', '<', '>', ':', '|', '?' })
            tenantId = tenantId.Replace(c, '_');
        return tenantId;
    }

    private static string Display(string tenant) => tenant.Length == 0 ? "(none)" : tenant;
}
