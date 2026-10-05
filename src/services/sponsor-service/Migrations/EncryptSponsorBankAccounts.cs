using CloudHealthOffice.FieldProtection;
using MongoDB.Driver;
using SponsorService.Models;
using SponsorService.Repositories;
using SponsorService.Services;

namespace SponsorService.Migrations;

/// <summary>
/// Operator command: stores every sponsor bank number encrypted and bound to
/// its record (<c>enc:v2:</c>): values stored before encryption existed
/// (plaintext) and values encrypted before binding existed (<c>enc:v1:</c>),
/// in
/// <list type="bullet">
///   <item>sponsor documents (<c>Sponsors</c>,
///   <c>BillingInfo.BillingAccountNumber</c>, bound to tenant + sponsor id, as
///   <see cref="ProtectedSponsorRepository"/> binds it);</item>
///   <item>bank-account records (<c>SponsorBankAccounts</c>: the active
///   account and every change's proposed and previous account, routing and
///   account numbers, bound to tenant + group number, as
///   <see cref="SponsorBankAccountService"/> binds them).</item>
/// </list>
///
/// <para>
/// Usage (with sponsor-service's own configuration: <c>MongoDb</c>, or
/// <c>Database:Provider=CosmosDb</c> with <c>CosmosDb</c>, and
/// <c>FieldProtection</c>, the same key ring the service uses):
/// <c>dotnet sponsor-service.dll --encrypt-bank-accounts [--tenant &lt;id&gt;]... [--dry-run]</c>
/// or <c>dotnet run --project src/services/sponsor-service -- --encrypt-bank-accounts</c>.
/// Without <c>--tenant</c> every tenant is processed (with
/// <c>MongoDb:UseTenantScoping</c>, <c>--tenant</c> is required: one database per tenant).
/// </para>
///
/// <para>
/// Idempotent and resumable: a value already bound is left alone, and each
/// document is written on its own and only if unchanged since it was read
/// (sponsor documents: Mongo on the value read, Cosmos a patch on the ETag;
/// bank-account records: the service's own revision-checked save). A document
/// changed by the running service in between is counted as a conflict and
/// left to the next run. Exit code 0 when nothing is left to bind, 2 when a
/// conflict or a failure needs another run, 1 when it cannot start (no key
/// ring, no database). Never prints a number.
/// </para>
///
/// <para>
/// Once it exits 0 for every tenant, set <c>FieldProtection:RejectPlaintext=true</c>
/// and <c>FieldProtection:RejectUnbound=true</c>.
/// </para>
/// </summary>
public static class EncryptSponsorBankAccounts
{
    public const string Switch = "--encrypt-bank-accounts";

    /// <summary>Counts for one tenant (or every tenant).</summary>
    public sealed class Counts
    {
        public int SponsorsScanned { get; set; }
        public int SponsorsEncrypted { get; set; }
        public int SponsorsAlreadyEncrypted { get; set; }
        public int SponsorConflicts { get; set; }
        public int RecordsScanned { get; set; }
        public int RecordsEncrypted { get; set; }
        public int RecordsAlreadyEncrypted { get; set; }
        public int RecordConflicts { get; set; }
        public int Failures { get; set; }

        public bool Complete => SponsorConflicts == 0 && RecordConflicts == 0 && Failures == 0;

        public void Add(Counts other)
        {
            SponsorsScanned += other.SponsorsScanned;
            SponsorsEncrypted += other.SponsorsEncrypted;
            SponsorsAlreadyEncrypted += other.SponsorsAlreadyEncrypted;
            SponsorConflicts += other.SponsorConflicts;
            RecordsScanned += other.RecordsScanned;
            RecordsEncrypted += other.RecordsEncrypted;
            RecordsAlreadyEncrypted += other.RecordsAlreadyEncrypted;
            RecordConflicts += other.RecordConflicts;
            Failures += other.Failures;
        }

        public override string ToString()
            => $"sponsors: scanned {SponsorsScanned}, encrypted {SponsorsEncrypted}, already encrypted {SponsorsAlreadyEncrypted}, " +
               $"conflicts {SponsorConflicts}; bank-account records: scanned {RecordsScanned}, encrypted {RecordsEncrypted}, " +
               $"already encrypted {RecordsAlreadyEncrypted}, conflicts {RecordConflicts}; failures {Failures}";
    }

    public static async Task<int> RunAsync(string[] args, IConfiguration configuration, IHostEnvironment environment)
    {
        Console.WriteLine("Sponsor bank numbers: encrypt and bind values stored before encryption or before binding");

        var services = new ServiceCollection();
        FieldProtectionKeyRing ring;
        try
        {
            ring = services.AddChoFieldProtection(configuration, environment, "sponsor-service");
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

        var tenants = ArgValues(args, "--tenant");
        var dryRun = args.Contains("--dry-run");
        await using var provider = services.BuildServiceProvider();
        var protector = provider.GetRequiredService<IFieldProtector>();

        Counts total;
        var useCosmos = string.Equals(configuration["Database:Provider"], "CosmosDb", StringComparison.OrdinalIgnoreCase);
        if (useCosmos)
        {
            // As Program.cs builds the service's own Cosmos client.
            if (string.IsNullOrWhiteSpace(configuration["CosmosDb:Endpoint"]) || string.IsNullOrWhiteSpace(configuration["CosmosDb:Key"]))
            {
                Console.Error.WriteLine("ABORT: Database:Provider is CosmosDb but CosmosDb:Endpoint/Key is not configured.");
                return 1;
            }
            using var cosmos = new Microsoft.Azure.Cosmos.CosmosClient(configuration["CosmosDb:Endpoint"], configuration["CosmosDb:Key"],
                new Microsoft.Azure.Cosmos.CosmosClientOptions
                {
                    SerializerOptions = new Microsoft.Azure.Cosmos.CosmosSerializationOptions
                    {
                        PropertyNamingPolicy = Microsoft.Azure.Cosmos.CosmosPropertyNamingPolicy.CamelCase
                    }
                });
            total = await MigrateStoresAsync(
                new CosmosSponsorRowStore(cosmos, configuration),
                new CosmosSponsorBankAccountRecordStore(cosmos, configuration),
                protector, tenants.Count == 0 ? null : tenants, dryRun, Console.Out);
        }
        else
        {
            var connectionString = configuration["MongoDb:ConnectionString"];
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                Console.Error.WriteLine("ABORT: neither MongoDb:ConnectionString nor Database:Provider=CosmosDb is configured.");
                return 1;
            }
            var baseName = configuration["MongoDb:DatabaseName"] ?? "CloudHealthOffice";
            var scoped = configuration.GetValue<bool>("MongoDb:UseTenantScoping", false);
            if (scoped && tenants.Count == 0)
            {
                Console.Error.WriteLine("ABORT: MongoDb:UseTenantScoping is on (one database per tenant): name each tenant with --tenant <id>.");
                return 1;
            }

            var client = new MongoClient(connectionString);
            if (scoped)
            {
                total = new Counts();
                foreach (var tenant in tenants)
                {
                    var db = client.GetDatabase($"{baseName}_{SanitizeTenantForDatabase(tenant)}");
                    total.Add(await MigrateStoresAsync(new MongoSponsorRowStore(db, configuration),
                        new MongoSponsorBankAccountRecordStore(db, configuration), protector, new[] { tenant }, dryRun, Console.Out));
                }
            }
            else
            {
                var db = client.GetDatabase(baseName);
                total = await MigrateStoresAsync(new MongoSponsorRowStore(db, configuration),
                    new MongoSponsorBankAccountRecordStore(db, configuration), protector,
                    tenants.Count == 0 ? null : tenants, dryRun, Console.Out);
            }
        }

        Console.WriteLine($"TOTAL{(useCosmos ? " (Cosmos)" : string.Empty)}{(dryRun ? " (dry run, nothing written)" : string.Empty)}: {total}");
        if (!total.Complete)
            Console.WriteLine("Some documents were changed while this ran or failed: run the command again.");
        return total.Complete ? 0 : 2;
    }

    /// <summary>
    /// Binds every sponsor billing account number and bank-account record
    /// number of <paramref name="tenants"/> (every tenant when null) not yet
    /// bound (plaintext or <c>enc:v1:</c>) as <c>enc:v2:</c>, each document
    /// written only if unchanged since read.
    /// </summary>
    public static async Task<Counts> MigrateStoresAsync(
        ISponsorRowStore sponsors,
        ISponsorBankAccountRecordStore records,
        IFieldProtector protector,
        IReadOnlyCollection<string>? tenants,
        bool dryRun,
        TextWriter output,
        CancellationToken ct = default)
    {
        var byTenant = new SortedDictionary<string, Counts>(StringComparer.Ordinal);
        Counts For(string? tenant) => byTenant.TryGetValue(tenant ?? string.Empty, out var c) ? c : byTenant[tenant ?? string.Empty] = new Counts();

        foreach (var tenant in tenants?.Cast<string?>() ?? new string?[] { null })
        {
            if (tenant != null) For(tenant);

            await foreach (var stored in sponsors.ListWithBillingAccountAsync(tenant, ct))
            {
                var counts = For(stored.Sponsor.TenantId);
                counts.SponsorsScanned++;
                try
                {
                    var rebound = Rebind(protector, stored.Sponsor.BillingInfo?.BillingAccountNumber,
                        ProtectedSponsorRepository.Context(stored.Sponsor));
                    if (rebound == null) { counts.SponsorsAlreadyEncrypted++; continue; }
                    if (dryRun) { counts.SponsorsEncrypted++; continue; }
                    if (await sponsors.SetBillingAccountIfUnchangedAsync(stored, rebound.Value.Stored, ct)) counts.SponsorsEncrypted++;
                    else counts.SponsorConflicts++;
                }
                catch (Exception ex) when (ex is FieldProtectionException or MongoException or Microsoft.Azure.Cosmos.CosmosException or ArgumentException)
                {
                    counts.Failures++;
                    await Console.Error.WriteLineAsync(
                        $"sponsor {stored.Sponsor.Id} in tenant {Display(stored.Sponsor.TenantId)} failed: {ex.GetType().Name}");
                }
            }

            await foreach (var record in records.ListAsync(tenant, ct))
            {
                var counts = For(record.TenantId);
                counts.RecordsScanned++;
                try
                {
                    var changed = RebindAccount(protector, record, record.Active);
                    foreach (var change in record.Changes)
                    {
                        changed |= RebindAccount(protector, record, change.Proposed);
                        changed |= RebindAccount(protector, record, change.PreviousAccount);
                    }
                    if (!changed) { counts.RecordsAlreadyEncrypted++; continue; }
                    if (dryRun) { counts.RecordsEncrypted++; continue; }
                    if (await records.SaveAsync(record, record.Revision, ct)) counts.RecordsEncrypted++;
                    else counts.RecordConflicts++;
                }
                catch (Exception ex) when (ex is FieldProtectionException or MongoException or Microsoft.Azure.Cosmos.CosmosException or ArgumentException)
                {
                    counts.Failures++;
                    await Console.Error.WriteLineAsync(
                        $"bank-account record {record.Id} in tenant {Display(record.TenantId)} failed: {ex.GetType().Name}");
                }
            }
        }

        var total = new Counts();
        foreach (var (tenant, counts) in byTenant)
        {
            await output.WriteLineAsync($"tenant {Display(tenant)}: {counts}");
            total.Add(counts);
        }
        return total;
    }

    /// <summary>Binds the routing and account numbers of one stored account in place (last 4 filled in when missing). True when anything changed.</summary>
    private static bool RebindAccount(IFieldProtector protector, SponsorBankAccountRecord record, SponsorBankAccountDetails? account)
    {
        if (account == null) return false;
        var changed = false;
        if (Rebind(protector, account.RoutingNumber, SponsorBankAccountService.Context(record, "routingNumber")) is { } routing)
        {
            account.RoutingNumber = routing.Stored;
            account.RoutingNumberLast4 ??= SponsorBankAccountMasking.Last4(routing.Plaintext);
            changed = true;
        }
        if (Rebind(protector, account.AccountNumber, SponsorBankAccountService.Context(record, "accountNumber")) is { } number)
        {
            account.AccountNumber = number.Stored;
            account.AccountNumberLast4 ??= SponsorBankAccountMasking.Last4(number.Plaintext);
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// The stored form of one value bound to <paramref name="context"/>, with
    /// its plaintext; null when there is nothing to do (empty, or already
    /// <c>enc:v2:</c>). <c>enc:v1:</c> is decrypted through the context-free
    /// overload (its format; the context overload refuses it under
    /// <c>FieldProtection:RejectUnbound</c>); plaintext is taken as stored
    /// (never through Unprotect, which <c>RejectPlaintext</c> would refuse).
    /// Throws <see cref="FieldProtectionException"/> when an <c>enc:v1:</c>
    /// value does not decrypt.
    /// </summary>
    internal static (string Stored, string Plaintext)? Rebind(IFieldProtector protector, string? value, FieldProtectionContext context)
    {
        if (string.IsNullOrEmpty(value) || FieldCiphertext.IsBound(value)) return null;
        var plaintext = FieldCiphertext.IsCiphertext(value) ? protector.Unprotect(value)! : value;
        return (protector.Protect(plaintext, context)!, plaintext);
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

    private static string Display(string? tenant) => string.IsNullOrEmpty(tenant) ? "(none)" : tenant;
}
