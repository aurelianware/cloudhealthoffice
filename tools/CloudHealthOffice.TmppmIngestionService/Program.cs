using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using CHO.TmppmIngestionService.Loaders;
using CHO.TmppmIngestionService.Parsers;
using CHO.TmppmIngestionService.Services;

namespace CHO.TmppmIngestionService;

/// <summary>
/// CHO TMPPM Ingestion Service
/// 
/// Usage:
///   dotnet run -- ingest 2026 4                    # Full ingestion of April 2026 TMPPM
///   dotnet run -- ingest 2026 4 --tenant txmco01   # With tenant scoping
///   dotnet run -- parse-section 2_13 9.2.46.14     # Parse a specific section from a chapter
///   dotnet run -- diff 2026-03 2026-04             # Diff two editions
///   dotnet run -- download 2026 4                  # Download only (no parsing)
/// </summary>
public class Program
{
    public static async Task<int> Main(string[] args)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("Config/appsettings.json", optional: true)
            .AddEnvironmentVariables("CHO_TMPPM_")
            .Build();

        var services = new ServiceCollection();
        ConfigureServices(services, config);
        var sp = services.BuildServiceProvider();

        var logger = sp.GetRequiredService<ILogger<Program>>();

        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var command = args[0].ToLowerInvariant();

        try
        {
            return command switch
            {
                "ingest" => await RunIngest(args, sp, logger),
                "parse-section" => await RunParseSection(args, sp, logger),
                "download" => await RunDownload(args, sp, logger),
                _ => PrintUsage()
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Pipeline failed");
            return 1;
        }
    }

    private static async Task<int> RunIngest(string[] args, IServiceProvider sp, ILogger logger)
    {
        if (args.Length < 3 || !int.TryParse(args[1], out var year) || !int.TryParse(args[2], out var month))
        {
            logger.LogError("Usage: ingest <year> <month> [--tenant <id>]");
            return 1;
        }

        string? tenantId = null;
        var tenantIdx = Array.IndexOf(args, "--tenant");
        if (tenantIdx >= 0 && tenantIdx + 1 < args.Length)
            tenantId = args[tenantIdx + 1];

        var pipeline = sp.GetRequiredService<IngestionPipeline>();
        var result = await pipeline.RunAsync(year, month, tenantId);

        logger.LogInformation(
            "\n══ Ingestion Summary ══\n" +
            "Edition:        {Edition}\n" +
            "Downloaded:     {Downloaded} chapters\n" +
            "Changed:        {Changed} chapters\n" +
            "Rules extracted: {Rules}\n" +
            "ConceptMap overrides: {Overrides}\n",
            result.EditionId,
            result.ChaptersDownloaded,
            result.ChaptersChanged,
            result.RulesExtracted,
            result.ConceptMapOverridesPublished);

        return 0;
    }

    private static async Task<int> RunParseSection(string[] args, IServiceProvider sp, ILogger logger)
    {
        if (args.Length < 3)
        {
            logger.LogError("Usage: parse-section <chapter_id> <section_ref>");
            return 1;
        }

        var chapterId = args[1];
        var sectionRef = args[2];
        var parser = sp.GetRequiredService<TmppmPdfParser>();

        // Look for the PDF in tmppm-data
        var pdfPath = Directory.EnumerateFiles("tmppm-data", $"{chapterId}*.pdf", SearchOption.AllDirectories)
            .FirstOrDefault();

        if (pdfPath == null)
        {
            logger.LogError("PDF not found for chapter {Id}. Run 'download' first.", chapterId);
            return 1;
        }

        var pages = parser.ExtractAllText(pdfPath);
        var section = parser.ExtractSection(pages, sectionRef);

        if (section == null)
        {
            logger.LogError("Section {Ref} not found in {Path}", sectionRef, pdfPath);
            return 1;
        }

        var codes = parser.ExtractProcedureCodes(section);
        var ageRule = parser.ExtractAgeRule(section);
        var dxCodes = parser.ExtractDiagnosisCodes(section);
        var paRequired = parser.DetectPaRequired(section);

        Console.WriteLine($"\n══ Section §{sectionRef} ══");
        Console.WriteLine($"PA Required:     {paRequired}");
        Console.WriteLine($"Procedure codes: {string.Join(", ", codes.Select(c => $"{c.Code} ({c.System})"))}");
        Console.WriteLine($"Age rule:        {(ageRule != null ? $"{ageRule.MinAge ?? 0}-{ageRule.MaxAge ?? 999} {ageRule.Unit}" : "none")}");
        Console.WriteLine($"Dx codes:        {string.Join(", ", dxCodes)}");
        Console.WriteLine($"Text length:     {section.Length} chars");
        Console.WriteLine($"\n── First 1000 chars ──\n{section[..Math.Min(1000, section.Length)]}");

        return 0;
    }

    private static async Task<int> RunDownload(string[] args, IServiceProvider sp, ILogger logger)
    {
        if (args.Length < 3 || !int.TryParse(args[1], out var year) || !int.TryParse(args[2], out var month))
        {
            logger.LogError("Usage: download <year> <month>");
            return 1;
        }

        var downloader = sp.GetRequiredService<TmhpChapterDownloader>();
        var outputDir = Path.Combine("tmppm-data", $"{year}-{month:D2}");
        var edition = await downloader.DownloadEditionAsync(year, month, outputDir);

        logger.LogInformation("Downloaded {Count} chapters to {Dir}", edition.Chapters.Count, outputDir);
        return 0;
    }

    private static void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));
        services.AddHttpClient<TmhpChapterDownloader>();

        // MongoDB
        var mongoConn = config["MongoDB:ConnectionString"] ?? "mongodb://localhost:27017";
        var mongoDb = config["MongoDB:DatabaseName"] ?? "cho_terminology";
        services.AddSingleton<IMongoClient>(new MongoClient(mongoConn));
        services.AddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase(mongoDb));

        // Services
        services.AddSingleton<TmppmPdfParser>();
        services.AddSingleton<TmhpChapterDownloader>();
        services.AddSingleton<TmppmRuleStore>();
        services.AddSingleton<IngestionPipeline>();
    }

    private static int PrintUsage()
    {
        Console.WriteLine("""
            CHO TMPPM Ingestion Service
            ═══════════════════════════
            
            Commands:
              ingest <year> <month> [--tenant <id>]   Full pipeline: download → parse → diff → persist
              parse-section <chapter_id> <section_ref> Parse a specific TMPPM section
              download <year> <month>                  Download PDFs only (no parsing)
            
            Examples:
              dotnet run -- ingest 2026 4                    # Ingest April 2026 TMPPM
              dotnet run -- ingest 2026 4 --tenant txmco01   # Scoped to a specific tenant
              dotnet run -- parse-section 2_13 9.2.46.14     # Extract HNS PA rules
              dotnet run -- download 2026 4                  # Download chapters only
            
            Environment variables (prefix CHO_TMPPM_):
              CHO_TMPPM_MONGODB__CONNECTIONSTRING     MongoDB connection string
              CHO_TMPPM_MONGODB__DATABASENAME          Database name
            """);
        return 1;
    }
}
