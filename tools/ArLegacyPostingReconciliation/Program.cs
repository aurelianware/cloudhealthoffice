using Microsoft.Extensions.Configuration;
using MongoDB.Driver;

namespace ArLegacyPostingReconciliation;

/// <summary>
/// Lists and reconciles ar-service cash postings applied before applying credited AR
/// balances (PR #1271). See docs/operations/AR-LEGACY-POSTING-RECONCILIATION.md.
///
/// Usage:
///   dotnet run --project tools/ArLegacyPostingReconciliation -- list --out legacy-postings.csv [--tenant T1,T2]
///   dotnet run --project tools/ArLegacyPostingReconciliation -- reconcile --csv reviewed.csv [--log file]
///   dotnet run --project tools/ArLegacyPostingReconciliation -- reconcile --csv reviewed.csv \
///       --execute --sha256 &lt;hash&gt; --operator &lt;name&gt; [--log file]
///   dotnet run --project tools/ArLegacyPostingReconciliation -- hash --csv reviewed.csv
///
/// Configuration (appsettings.json, env vars MongoDb__..., or --MongoDb:...): the same as ar-service.
///   MongoDb:ConnectionString   required (MongoDB, or Cosmos DB for MongoDB)
///   MongoDb:DatabaseName       default CloudHealthOffice
///   MongoDb:UseTenantScoping   default false
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith('-'))
            return Usage();
        var command = args[0].ToLowerInvariant();
        var rest = Normalize(args.Skip(1).ToArray());

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .AddCommandLine(rest)
            .Build();

        try
        {
            switch (command)
            {
                case "hash":
                {
                    var csv = Required(config, "csv");
                    Console.WriteLine($"{LegacyPostingReconciler.Sha256Of(csv)}  {csv}");
                    return 0;
                }
                case "list":
                    return await ListAsync(config);
                case "reconcile":
                    return await ReconcileAsync(config);
                default:
                    return Usage();
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
        catch (FormatException ex)
        {
            Console.Error.WriteLine($"CSV unreadable: {ex.Message}. Nothing was changed.");
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

    private static async Task<int> ListAsync(IConfiguration config)
    {
        var output = Required(config, "out");
        var tenants = (config["tenant"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var rows = await new LegacyPostingLister(Databases(config)).ListAsync(tenants);
        await using (var writer = new StreamWriter(output, append: false, new System.Text.UTF8Encoding(false)))
            await LegacyPostingLister.WriteCsvAsync(writer, rows);
        var postings = rows.Select(r => (r[0], r[1])).Distinct().Count();
        Console.WriteLine($"{postings} legacy posting(s), {rows.Count} application row(s) written to {output}");
        Console.WriteLine($"sha256 {LegacyPostingReconciler.Sha256Of(output)}");
        return 0;
    }

    private static async Task<int> ReconcileAsync(IConfiguration config)
    {
        var csv = Required(config, "csv");
        var execute = config.GetValue("execute", false);
        var logFile = config["log"]
            ?? $"ar-legacy-reconciliation-{DateTime.UtcNow:yyyyMMddTHHmmssZ}-{(execute ? "execute" : "dryrun")}.log";
        var op = config["operator"] ?? (execute ? string.Empty : Environment.UserName);

        await using var log = new StreamWriter(logFile, append: true, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
        void Output(string line)
        {
            Console.WriteLine(line);
            log.WriteLine($"{DateTime.UtcNow:O} {line}");
        }

        var reconciler = new LegacyPostingReconciler(Databases(config), Output);
        var result = await reconciler.RunAsync(new ReconcileOptions
        {
            CsvPath = csv,
            Execute = execute,
            ExpectedSha256 = config["sha256"],
            Operator = op,
            LogFile = Path.GetFullPath(logFile)
        });
        Output($"Log written to {Path.GetFullPath(logFile)}");
        return result.Count(OutcomeKind.Refused) + result.Count(OutcomeKind.Failed) == 0 ? 0 : 1;
    }

    private static string Required(IConfiguration config, string key) =>
        string.IsNullOrWhiteSpace(config[key]) ? throw new ArgumentException($"--{key} is required.") : config[key]!;

    /// <summary>Lets <c>--execute</c> stand alone as a switch.</summary>
    private static string[] Normalize(string[] args)
    {
        var result = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            result.Add(args[i]);
            if (args[i] == "--execute" && (i + 1 >= args.Length || args[i + 1].StartsWith("--")))
                result.Add("true");
        }
        return result.ToArray();
    }

    private static int Usage()
    {
        Console.Error.WriteLine("""
            Usage:
              list      --out <file.csv> [--tenant T1,T2]      list legacy cash postings (read-only)
              hash      --csv <file.csv>                       print the SHA-256 of a CSV
              reconcile --csv <file.csv> [--log <file>]        dry-run: print what would be done
              reconcile --csv <file.csv> --execute --sha256 <hash> --operator <name> [--log <file>]
            See docs/operations/AR-LEGACY-POSTING-RECONCILIATION.md.
            """);
        return 2;
    }
}
