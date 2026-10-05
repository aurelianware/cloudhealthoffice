using CHO.TerminologyService.Configuration;
using CHO.TerminologyService.Models;
using MongoDB.Driver;

namespace CHO.TerminologyService.Migrations;

/// <summary>
/// One-off operator command: sets <see cref="MapVersion.TenantId"/> on map
/// versions saved before that field existed, so a tenant's override version no
/// longer reads as global (listed to every tenant by <c>GET /admin/maps</c>,
/// reported as "the" map behind a translation, counted by syndication).
///
/// <para>
/// Usage (terminology-service's own configuration, <c>TerminologyService</c>
/// section): <c>dotnet CHO.TerminologyService.dll --backfill-override-tenants [--dry-run]</c>.
/// </para>
///
/// <para>
/// Logic, for each map version with no tenant (field missing or null):
/// <list type="number">
///   <item>Read the entries that point at it (<c>concept_map_entries.MapVersionId</c>).</item>
///   <item>No override entries and at least one global entry: a global map.
///   Left alone (<see cref="Counts.Global"/>).</item>
///   <item>Only override entries, all of one tenant T: the version is T's.
///   <c>TenantId = T</c> is set with a compare-and-set on the version still
///   having no tenant (<see cref="Counts.Assigned"/>).</item>
///   <item>Override entries of several tenants, override entries without a
///   tenant, or override and global entries mixed: ambiguous. Left alone and
///   listed (<see cref="Counts.Ambiguous"/>).</item>
///   <item>No entries at all (a later override load re-points an entry to its
///   own newer version, so an old override version can end up empty):
///   undeterminable from the data. Left alone and listed
///   (<see cref="Counts.NoEntries"/>); an operator decides (delete it, or set
///   its tenant by hand from the import record).</item>
/// </list>
/// Idempotent: a version that has a tenant is never read again. Exit code 0
/// when nothing is left to decide, 2 when ambiguous or empty versions remain,
/// 1 when it cannot start.
/// </para>
/// </summary>
public static class BackfillOverrideVersionTenants
{
    public const string Switch = "--backfill-override-tenants";

    public sealed class Counts
    {
        public int Scanned { get; set; }
        public int Assigned { get; set; }
        public int Global { get; set; }
        public List<string> Ambiguous { get; } = new();
        public List<string> NoEntries { get; } = new();
        public int Conflicts { get; set; }

        public bool Complete => Ambiguous.Count == 0 && NoEntries.Count == 0 && Conflicts == 0;

        public override string ToString()
            => $"map versions without a tenant: scanned {Scanned}, assigned a tenant {Assigned}, global {Global}, " +
               $"ambiguous {Ambiguous.Count}, no entries {NoEntries.Count}, changed meanwhile {Conflicts}";
    }

    public static async Task<int> RunAsync(string[] args, IConfiguration configuration)
    {
        var options = configuration.GetSection(TerminologyServiceOptions.SectionName).Get<TerminologyServiceOptions>()
            ?? new TerminologyServiceOptions();
        if (string.IsNullOrWhiteSpace(options.MongoConnectionString))
        {
            Console.Error.WriteLine($"{Switch}: TerminologyService:MongoConnectionString is not configured.");
            return 1;
        }

        var dryRun = args.Contains("--dry-run");
        var database = new MongoClient(options.MongoConnectionString).GetDatabase(options.MongoDatabaseName);
        var counts = await BackfillAsync(database, dryRun, Console.Out);

        Console.WriteLine((dryRun ? "[dry run] " : string.Empty) + counts);
        foreach (var id in counts.Ambiguous)
            Console.WriteLine($"  ambiguous (entries of several tenants, or overrides mixed with global entries): {id}");
        foreach (var id in counts.NoEntries)
            Console.WriteLine($"  no entries (cannot tell whose it is; decide by hand): {id}");
        return counts.Complete ? 0 : 2;
    }

    public static async Task<Counts> BackfillAsync(IMongoDatabase database, bool dryRun, TextWriter? log = null, CancellationToken ct = default)
    {
        var versions = database.GetCollection<MapVersion>("map_versions");
        var entries = database.GetCollection<ConceptMapEntry>("concept_map_entries");
        var counts = new Counts();

        // Eq null also matches versions where the field is missing.
        var withoutTenant = await versions.Find(Builders<MapVersion>.Filter.Eq(v => v.TenantId, null)).ToListAsync(ct);
        foreach (var version in withoutTenant)
        {
            counts.Scanned++;
            var ofVersion = Builders<ConceptMapEntry>.Filter.Eq(e => e.MapVersionId, version.Id);

            var overrideTenants = await entries.Distinct(
                e => e.TenantId,
                ofVersion & Builders<ConceptMapEntry>.Filter.Eq(e => e.IsOverride, true),
                cancellationToken: ct).ToListAsync(ct);
            var hasGlobal = await entries.Find(ofVersion & Builders<ConceptMapEntry>.Filter.Eq(e => e.IsOverride, false))
                .Limit(1).AnyAsync(ct);

            if (overrideTenants.Count == 0)
            {
                if (hasGlobal) counts.Global++;
                else counts.NoEntries.Add(version.Id);
                continue;
            }

            if (hasGlobal || overrideTenants.Count != 1 || string.IsNullOrEmpty(overrideTenants[0]))
            {
                counts.Ambiguous.Add(version.Id);
                continue;
            }

            var tenant = overrideTenants[0]!;
            if (dryRun)
            {
                counts.Assigned++;
                log?.WriteLine($"would assign {version.Id} to tenant {tenant}");
                continue;
            }

            var result = await versions.UpdateOneAsync(
                Builders<MapVersion>.Filter.Eq(v => v.Id, version.Id) & Builders<MapVersion>.Filter.Eq(v => v.TenantId, null),
                Builders<MapVersion>.Update.Set(v => v.TenantId, tenant),
                cancellationToken: ct);
            if (result.ModifiedCount == 1)
            {
                counts.Assigned++;
                log?.WriteLine($"AUDIT terminology map version {version.Id} assigned to tenant {tenant} (backfill)");
            }
            else
            {
                counts.Conflicts++;
            }
        }

        return counts;
    }
}
