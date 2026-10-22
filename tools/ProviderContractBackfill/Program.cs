using Microsoft.Extensions.Configuration;
using MongoDB.Driver;

namespace ProviderContractBackfill;

/// <summary>
/// Builds the canonical provider contract of ADR 016 out of the four places a
/// provider's rates live today, and writes it to the engine's contract store
/// (ADR 016 phase 3). See docs/operations/PROVIDER-CONTRACT-BACKFILL.md.
///
/// Usage:
///   ... plan  --out plan.json [--csv plan.csv] --confirm-database &lt;db&gt; [--tenant T1,T2]
///   ... hash  --plan plan.json
///   ... apply --plan plan.json --confirm-database &lt;db&gt; [--log file]
///   ... apply --plan plan.json --confirm-database &lt;db&gt; --execute --sha256 &lt;hash&gt; --operator &lt;name&gt; [--log file]
///
/// <c>plan</c> reads and writes nothing to the database. <c>apply</c> without
/// <c>--execute</c> is a dry run: it says what it would write, line by line, and
/// writes nothing. <c>--execute</c> needs the plan's hash and an operator name,
/// so an edited or stale plan cannot be applied by accident.
///
/// Ids are deterministic (<c>{tenant}:{npi}:{plan}</c>), so a second run of the
/// same plan replaces the same contracts rather than adding more.
///
/// The canonical contracts go in their own collection, beside the engine's
/// existing one rather than over it: the dual-read window of ADR 016 phase 4
/// prices every claim with both and pays on the legacy one, which it can only do
/// while both exist. Undoing this backfill is dropping a collection.
///
/// Settings (appsettings.json or env vars, never the command line):
///   MongoDb:ConnectionString          required
///   MongoDb:DatabaseName              default CloudHealthOffice
///   Collections:LegalContracts        default provider_contracts
///   Collections:FfsRateConfigs        default ffs_rate_configs
///   Collections:CapitationRateConfigs default capitation_rate_configs
///   Collections:EngineContracts       default ProviderContracts (read, never written)
///   Collections:CanonicalContracts    default ProviderContractsCanonical (the only thing written)
///   Collections:FeeSchedules          default FeeSchedules
///
/// Exit codes: 0 done, nothing blocked or failed; 1 some provider is blocked by
/// a conflict, or a write failed; 2 invalid arguments or plan file (nothing
/// changed); 3 plan hash mismatch (nothing changed); 4 wrong database (nothing
/// changed).
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        CommandLine line;
        try
        {
            line = CommandLine.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return Usage();
        }

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        try
        {
            return line.Command switch
            {
                "hash" => Hash(line),
                "plan" => await PlanAsync(StoresFrom(config), line),
                _ => await ApplyAsync(StoresFrom(config), line),
            };
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        catch (FormatException ex)
        {
            Console.Error.WriteLine($"Plan refused: {ex.Message}. Nothing was changed.");
            return 2;
        }
        catch (PlanHashMismatchException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 3;
        }
        catch (EnvironmentMismatchException ex)
        {
            Console.Error.WriteLine($"Refused: {ex.Message}");
            return 4;
        }
    }

    internal static Stores StoresFrom(IConfiguration config)
    {
        var connectionString = config["MongoDb:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("MongoDb:ConnectionString is required (env MongoDb__ConnectionString).");

        var defaults = Collections.Default;
        var collections = new Collections(
            config["Collections:LegalContracts"] ?? defaults.LegalContracts,
            config["Collections:FfsRateConfigs"] ?? defaults.FfsRateConfigs,
            config["Collections:CapitationRateConfigs"] ?? defaults.CapitationRateConfigs,
            config["Collections:EngineContracts"] ?? defaults.EngineContracts,
            config["Collections:CanonicalContracts"] ?? defaults.CanonicalContracts,
            config["Collections:FeeSchedules"] ?? defaults.FeeSchedules);

        return new Stores(
            new MongoClient(connectionString),
            config["MongoDb:DatabaseName"] ?? "CloudHealthOffice",
            collections);
    }

    private static int Hash(CommandLine line)
    {
        var plan = line.Required("plan");
        Console.WriteLine($"{BackfillPlan.Sha256Of(plan)}  {plan}");
        return 0;
    }

    internal static async Task<int> PlanAsync(Stores stores, CommandLine line)
    {
        var output = line.Required("out");
        Console.WriteLine(stores.Describe());
        stores.Confirm(line["confirm-database"]);

        var plan = await new Backfill(stores, Console.WriteLine).PlanAsync(line.List("tenant"));

        await File.WriteAllTextAsync(output, plan.ToJson());
        if (line["csv"] is { } csv)
        {
            await File.WriteAllTextAsync(csv, plan.ToCsv());
            Console.WriteLine($"summary written to {csv}");
        }

        Summarize(plan);
        Console.WriteLine($"plan written to {output}");
        Console.WriteLine($"sha256 {BackfillPlan.Sha256Of(output)}");

        // The plan itself changed nothing, so a conflict is a finding to act on,
        // not a failure of this command.
        return 0;
    }

    internal static async Task<int> ApplyAsync(Stores stores, CommandLine line)
    {
        var path = line.Required("plan");
        var plan = BackfillPlan.FromJson(await File.ReadAllTextAsync(path));

        Console.WriteLine(stores.Describe());
        stores.Confirm(line["confirm-database"]);

        if (!string.Equals(plan.DatabaseName, stores.DatabaseName, StringComparison.Ordinal))
        {
            throw new EnvironmentMismatchException(
                $"the plan was built against database '{plan.DatabaseName}', not '{stores.DatabaseName}'");
        }

        var execute = line.Has("execute");
        if (execute)
        {
            var expected = line.Required("sha256");
            var actual = BackfillPlan.Sha256Of(path);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new PlanHashMismatchException(
                    $"the plan's sha256 is {actual}, not the {expected} given on the command line. " +
                    "Nothing was changed: re-read the plan and pass its current hash.");
            }

            // Named so that the log says who decided this, not just that it happened.
            line.Required("operator");
        }

        Summarize(plan);

        var log = line["log"] is { } logPath
            ? new StreamWriter(logPath, append: true, new System.Text.UTF8Encoding(false))
            : null;

        await using (log)
        {
            void Record(string message)
            {
                Console.WriteLine(message);
                log?.WriteLine($"{DateTime.UtcNow:O} {message}");
            }

            if (execute)
            {
                Record($"apply --execute by {line["operator"]} against {stores.DatabaseName}, " +
                       $"plan {path} sha256 {BackfillPlan.Sha256Of(path)}");
            }
            else
            {
                Record("dry run: nothing will be written. Add --execute --sha256 <hash> --operator <name> to write.");
            }

            var written = 0;
            var failed = 0;

            foreach (var row in plan.Writable)
            {
                if (row.Contract is null)
                {
                    Record($"  SKIP   {row.CanonicalId}: the plan carries no contract document for it");
                    failed++;
                    continue;
                }

                var verb = row.Replaces ? "replace" : "create";
                if (!execute)
                {
                    Record($"  WOULD  {verb} {row.CanonicalId} ({row.Contract.ReimbursementTerms.Count} term(s)) — {row.Summary}");
                    continue;
                }

                try
                {
                    await stores.ReplaceCanonicalContractAsync(row.Contract);
                    Record($"  {verb.ToUpperInvariant()} {row.CanonicalId} ({row.Contract.ReimbursementTerms.Count} term(s))");
                    written++;
                }
                catch (Exception ex)
                {
                    Record($"  FAILED {row.CanonicalId}: {ex.Message}");
                    failed++;
                }
            }

            foreach (var row in plan.Blocked)
                Record($"  BLOCKED {row.ProviderNpi} on plan {row.PlanId}: {string.Join("; ", row.Conflicts)}");

            if (execute)
                Record($"{written} contract(s) written, {failed} failed, {plan.Blocked.Count()} blocked");

            return failed > 0 || plan.Blocked.Any() ? 1 : 0;
        }
    }

    /// <summary>
    /// The plan in one screen, including the conflict report: ADR 016 phase 3
    /// requires every run to produce one, and the cutover (phase 5) is gated on
    /// it being empty for the tenant.
    /// </summary>
    private static void Summarize(BackfillPlan plan)
    {
        Console.WriteLine($"plan generated {plan.GeneratedAtUtc} for {plan.Tenants.Count} tenant(s)");

        foreach (var group in plan.Contracts.GroupBy(c => c.Decision).OrderBy(g => g.Key))
            Console.WriteLine($"  {group.Key}: {group.Count()}");

        var blocked = plan.Blocked.ToList();
        if (blocked.Count == 0)
        {
            Console.WriteLine("  no conflicts");
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"CONFLICT REPORT — {blocked.Count} provider-and-plan(s) cannot be made canonical.");
        Console.WriteLine("Every tenant below is blocked from cutover until these are resolved.");

        foreach (var tenant in blocked.GroupBy(c => c.TenantId).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {tenant.Key}:");
            foreach (var row in tenant.OrderBy(c => c.ProviderNpi, StringComparer.Ordinal))
            {
                Console.WriteLine($"    {row.ProviderNpi} plan {(row.PlanId.Length == 0 ? "(none)" : row.PlanId)}" +
                                  $"{(row.LegalContractId is null ? string.Empty : $" contract {row.ContractNumber}")}");
                foreach (var conflict in row.Conflicts)
                    Console.WriteLine($"      - {conflict}");
            }
        }

        Console.WriteLine();
    }

    private static int Usage()
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("ProviderContractBackfill (ADR 016 phase 3)");
        Console.Error.WriteLine("  plan  --out plan.json [--csv plan.csv] --confirm-database <db> [--tenant T1,T2]");
        Console.Error.WriteLine("  hash  --plan plan.json");
        Console.Error.WriteLine("  apply --plan plan.json --confirm-database <db> [--log file]");
        Console.Error.WriteLine("  apply --plan plan.json --confirm-database <db> --execute --sha256 <hash> --operator <name> [--log file]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("See docs/operations/PROVIDER-CONTRACT-BACKFILL.md.");
        return 2;
    }
}

/// <summary>Raised when the plan on disk is not the plan whose hash the operator gave.</summary>
public sealed class PlanHashMismatchException(string message) : Exception(message);
