using Microsoft.Extensions.Configuration;
using MongoDB.Driver;

namespace ArLegacyPostingReconciliation;

/// <summary>
/// Lists and reconciles ar-service cash postings applied before applying credited AR
/// balances (PR #1271). See docs/operations/AR-LEGACY-POSTING-RECONCILIATION.md.
///
/// Usage:
///   ... list      --out legacy-postings.csv --confirm-database &lt;db&gt; [--tenant T1,T2]
///   ... reconcile --csv reviewed.csv --confirm-database &lt;db&gt; [--tenant T1,T2] [--log file]
///   ... reconcile --csv reviewed.csv --confirm-database &lt;db&gt; --execute --sha256 &lt;hash&gt; --operator &lt;name&gt; [--tenant T1,T2] [--log file]
///   ... hash      --csv reviewed.csv
///
/// Database settings (appsettings.json or env vars MongoDb__...), the same as ar-service:
///   MongoDb:ConnectionString   required (MongoDB, or Cosmos DB for MongoDB)
///   MongoDb:DatabaseName       default CloudHealthOffice
///   MongoDb:UseTenantScoping   default false
///
/// Exit codes: 0 done, nothing refused or failed; 1 some posting refused or failed;
/// 2 invalid arguments or CSV (nothing changed); 3 CSV hash mismatch (nothing changed);
/// 4 wrong environment or no guard-capable ar-service (nothing changed).
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
                    Console.WriteLine($"{LegacyPostingReconciler.Sha256Of(csv)}  {csv}");
                    return 0;
                }
                case "list":
                    return await ListAsync(config, line);
                default:
                    return await ReconcileAsync(config, line);
            }
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
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
        catch (FormatException ex)
        {
            Console.Error.WriteLine($"CSV refused: {ex.Message}. Nothing was changed.");
            return 2;
        }
    }

    private static TenantDatabases Databases(IConfiguration config)
    {
        var connectionString = config["MongoDb:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("MongoDb:ConnectionString is required (env MongoDb__ConnectionString).");
        return new TenantDatabases(
            new MongoClient(connectionString),
            config["MongoDb:DatabaseName"] ?? "CloudHealthOffice",
            config.GetValue("MongoDb:UseTenantScoping", false));
    }

    private static async Task<int> ListAsync(IConfiguration config, CommandLine line)
    {
        var output = line.Required("out");
        var databases = Databases(config);
        Console.WriteLine(databases.Describe());
        databases.Confirm(line["confirm-database"]);

        var rows = await new LegacyPostingLister(databases).ListAsync(line.List("tenant"));
        await using (var writer = new StreamWriter(output, append: false, new System.Text.UTF8Encoding(false)))
            await LegacyPostingLister.WriteCsvAsync(writer, rows);
        var postings = rows.Select(r => (r[0], r[1])).Distinct().Count();
        Console.WriteLine($"{postings} legacy posting(s), {rows.Count} application row(s) written to {output}");
        Console.WriteLine($"sha256 {LegacyPostingReconciler.Sha256Of(output)}");
        Console.WriteLine("The file holds payer names and ids: keep it in the restricted ticket storage only (see the runbook).");
        return 0;
    }

    private static async Task<int> ReconcileAsync(IConfiguration config, CommandLine line)
    {
        var csv = line.Required("csv");
        var execute = line.Has("execute");
        var databases = Databases(config);
        // Before the log file is opened: a wrong environment leaves nothing behind. The
        // refusal names the configured host and database; the run prints them first thing.
        databases.Confirm(line["confirm-database"]);

        var logFile = line["log"]
            ?? $"ar-legacy-reconciliation-{DateTime.UtcNow:yyyyMMddTHHmmssZ}-{(execute ? "execute" : "dryrun")}.log";
        var op = line["operator"] ?? (execute ? string.Empty : Environment.UserName);

        await using var log = new StreamWriter(logFile, append: true, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
        void Output(string text)
        {
            Console.WriteLine(text);
            log.WriteLine($"{DateTime.UtcNow:O} {text}");
        }

        var result = await new LegacyPostingReconciler(databases, Output).RunAsync(new ReconcileOptions
        {
            CsvPath = csv,
            Execute = execute,
            ExpectedSha256 = line["sha256"],
            Operator = op,
            ConfirmDatabase = line["confirm-database"],
            Tenants = line.List("tenant"),
            LogFile = Path.GetFullPath(logFile)
        });
        Output($"Log written to {Path.GetFullPath(logFile)}");
        return result.Count(OutcomeKind.Refused) + result.Count(OutcomeKind.Failed) == 0 ? 0 : 1;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("""
            Usage:
              list      --out <file.csv> --confirm-database <db> [--tenant T1,T2]
                        list legacy cash postings (read-only)
              hash      --csv <file.csv>
                        print the SHA-256 of a CSV
              reconcile --csv <file.csv> --confirm-database <db> [--tenant T1,T2] [--log <file>]
                        dry-run: print what would be done
              reconcile --csv <file.csv> --confirm-database <db> --execute --sha256 <hash> --operator <name>
                        [--tenant T1,T2] [--log <file>]
            Database settings come from MongoDb__ConnectionString, MongoDb__DatabaseName and
            MongoDb__UseTenantScoping. See docs/operations/AR-LEGACY-POSTING-RECONCILIATION.md.
            """);
        return 2;
    }
}
