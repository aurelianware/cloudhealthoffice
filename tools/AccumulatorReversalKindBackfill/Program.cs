using Microsoft.Extensions.Configuration;
using MongoDB.Driver;

namespace AccumulatorReversalKindBackfill;

/// <summary>
/// Lists and backfills <c>ReversalKind</c> on accumulator-service reversal rows and tombstones
/// written before the kind was recorded. See docs/operations/ACCUMULATOR-REVERSAL-KIND-BACKFILL.md.
///
/// Usage:
///   ... list  --out plan.csv --confirm-database &lt;db&gt; [--tenant T1,T2]
///   ... hash  --csv plan.csv
///   ... apply --csv plan.csv --confirm-database &lt;db&gt; [--tenant T1,T2] [--log file]
///   ... apply --csv plan.csv --confirm-database &lt;db&gt; --execute --sha256 &lt;hash&gt; --operator &lt;name&gt; [--tenant T1,T2] [--log file]
///
/// Settings (appsettings.json or env vars, never the command line):
///   MongoDb:ConnectionString        accumulator-service's database (required)
///   MongoDb:DatabaseName            default CloudHealthOffice
///   ClaimsMongoDb:DatabaseName      claims-service's database, for evidence (optional; without it
///                                   own reversals and voids stay AMBIGUOUS)
///   ClaimsMongoDb:ConnectionString  default: MongoDb:ConnectionString
///   ClaimsMongoDb:UseTenantScoping  default false
///
/// Exit codes: 0 done, nothing refused or failed (AMBIGUOUS rows are reported, not failures);
/// 1 some row refused or failed; 2 invalid arguments or CSV (nothing changed); 3 CSV hash
/// mismatch (nothing changed); 4 wrong environment (nothing changed).
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
            switch (line.Command)
            {
                case "hash":
                {
                    var csv = line.Required("csv");
                    Console.WriteLine($"{Backfill.Sha256Of(csv)}  {csv}");
                    return 0;
                }
                case "list":
                    return await ListAsync(TargetFrom(config), line);
                default:
                    return await ApplyAsync(TargetFrom(config), line);
            }
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        catch (FormatException ex)
        {
            Console.Error.WriteLine($"CSV refused: {ex.Message}. Nothing was changed.");
            return 2;
        }
        catch (CsvHashMismatchException ex)
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

    internal static Target TargetFrom(IConfiguration config)
    {
        var connectionString = config["MongoDb:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("MongoDb:ConnectionString is required (env MongoDb__ConnectionString).");
        var client = new MongoClient(connectionString);

        ClaimsEvidence? claims = null;
        var claimsDatabase = config["ClaimsMongoDb:DatabaseName"];
        if (!string.IsNullOrWhiteSpace(claimsDatabase))
        {
            var claimsConnection = config["ClaimsMongoDb:ConnectionString"];
            claims = new ClaimsEvidence(
                string.IsNullOrWhiteSpace(claimsConnection) ? client : new MongoClient(claimsConnection),
                claimsDatabase,
                config.GetValue("ClaimsMongoDb:UseTenantScoping", false));
        }
        return new Target(client, config["MongoDb:DatabaseName"] ?? "CloudHealthOffice", claims);
    }

    internal static async Task<int> ListAsync(Target target, CommandLine line)
    {
        var output = line.Required("out");
        Console.WriteLine(target.Describe());
        target.Confirm(line["confirm-database"]);

        var backfill = new Backfill(target, Console.WriteLine);
        var rows = await backfill.ListAsync(line.List("tenant"));
        await using (var writer = new StreamWriter(output, append: false, new System.Text.UTF8Encoding(false)))
            await backfill.WriteCsvAsync(writer, rows);
        foreach (var kind in rows.GroupBy(r => r.Classification.Kind).OrderBy(g => g.Key, StringComparer.Ordinal))
            Console.WriteLine($"  {kind.Key}: {kind.Count()} record(s), {kind.Select(r => (r.Item.TenantId, r.Item.OriginalClaimId)).Distinct().Count()} original claim(s)");
        Console.WriteLine($"{rows.Count} legacy record(s) written to {output}");
        Console.WriteLine($"sha256 {Backfill.Sha256Of(output)}");
        Console.WriteLine("The file holds member and claim ids: keep it in the restricted ticket storage only (see the runbook).");
        return 0;
    }

    internal static async Task<int> ApplyAsync(Target target, CommandLine line)
    {
        var csv = line.Required("csv");
        var execute = line.Has("execute");
        // Before the log file is opened: a wrong environment leaves nothing behind.
        target.Confirm(line["confirm-database"]);

        var logFile = line["log"]
            ?? $"accumulator-reversal-kind-backfill-{DateTime.UtcNow:yyyyMMddTHHmmssZ}-{(execute ? "execute" : "dryrun")}.log";
        await using var log = new StreamWriter(logFile, append: true, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
        void Output(string text)
        {
            Console.WriteLine(text);
            log.WriteLine($"{DateTime.UtcNow:O} {text}");
        }

        var results = await new Backfill(target, Output).ApplyAsync(new ApplyOptions
        {
            CsvPath = csv,
            Execute = execute,
            ExpectedSha256 = line["sha256"],
            Operator = line["operator"] ?? string.Empty,
            ConfirmDatabase = line["confirm-database"],
            Tenants = line.List("tenant"),
            LogFile = Path.GetFullPath(logFile),
        });
        Output($"Log written to {Path.GetFullPath(logFile)}");
        return results.Any(r => r.Outcome is RowOutcome.Refused or RowOutcome.Failed) ? 1 : 0;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("""
            Usage:
              list  --out <plan.csv> --confirm-database <db> [--tenant T1,T2]
                    list legacy reversal rows / tombstones and the kind their evidence supports (read-only)
              hash  --csv <plan.csv>
                    print the SHA-256 of a plan
              apply --csv <plan.csv> --confirm-database <db> [--tenant T1,T2] [--log <file>]
                    dry-run: print what would be written
              apply --csv <plan.csv> --confirm-database <db> --execute --sha256 <hash> --operator <name>
                    [--tenant T1,T2] [--log <file>]
            Settings come from MongoDb__ConnectionString, MongoDb__DatabaseName and, for claims-service
            evidence, ClaimsMongoDb__DatabaseName (+ ClaimsMongoDb__ConnectionString, ClaimsMongoDb__UseTenantScoping).
            See docs/operations/ACCUMULATOR-REVERSAL-KIND-BACKFILL.md.
            """);
        return 2;
    }
}
