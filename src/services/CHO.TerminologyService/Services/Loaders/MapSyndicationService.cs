using CHO.TerminologyService.Configuration;
using CHO.TerminologyService.Services;
using Microsoft.Extensions.Options;

namespace CHO.TerminologyService.Services.Loaders;

/// <summary>
/// Background service that manages the map syndication lifecycle:
///   1. On startup: load any auto-configured map files from mounted volumes
///   2. On schedule: check NLM/SNOMED download endpoints for new editions
///   3. On trigger: load ad-hoc map files uploaded via the admin API
/// 
/// Syndication cadence:
///   - NLM SNOMED-to-ICD-10-CM: twice yearly (March, September US Edition releases)
///   - SNOMED International ICD-10 map: twice yearly (January, July)
///   - AMA CPT cross maps: annually (CPT updates each January)
///   - Plan overrides: on-demand via admin API
/// 
/// The loader is idempotent — if the same version is already loaded (matched by
/// version string), it skips the import. This means you can safely point the
/// scheduled check at the same NLM download URL repeatedly.
/// </summary>
public class MapSyndicationService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IOptions<TerminologyServiceOptions> _options;
    private readonly ILogger<MapSyndicationService> _logger;

    // Check for new maps daily at 2 AM UTC
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    // Well-known NLM download base URL (requires UMLS API key)
    private const string NLM_DOWNLOAD_BASE = "https://download.nlm.nih.gov/umls/kss/SNOMEDCT_US";

    public MapSyndicationService(
        IServiceProvider serviceProvider,
        IOptions<TerminologyServiceOptions> options,
        ILogger<MapSyndicationService> logger)
    {
        _serviceProvider = serviceProvider;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Phase 1: Startup auto-load from mounted volumes
        await LoadMountedMapsAsync(stoppingToken);

        // Phase 2: Periodic check for new editions
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
                await CheckForUpdatesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Map syndication check failed — will retry next cycle");
            }
        }
    }

    /// <summary>
    /// Phase 1: Load map files from Kubernetes volume mounts on startup.
    /// 
    /// In the AKS deployment, the /data/maps PVC contains files placed there by
    /// an init job or manual upload. The auto-load config in appsettings.json
    /// tells us which files to load and how to interpret them.
    /// 
    /// Volume layout:
    ///   /data/maps/
    ///     nlm/
    ///       der2_iisssccRefset_ExtendedMapFull_US.txt    (NLM RF2)
    ///       sct2_Description_Full_US.txt                  (Display names)
    ///     ama/
    ///       cpt_snomed_crossmap.csv                       (AMA, customer-provided)
    ///     overrides/
    ///       plan_tmppm_overrides.csv                      (Plan-specific)
    /// </summary>
    private async Task LoadMountedMapsAsync(CancellationToken ct)
    {
        var autoLoadMaps = _options.Value.AutoLoadMaps;
        if (autoLoadMaps.Count == 0)
        {
            _logger.LogInformation("No auto-load maps configured");
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var loaders = scope.ServiceProvider.GetServices<IMapLoader>().ToList();
        var repository = scope.ServiceProvider.GetRequiredService<IConceptMapRepository>();

        foreach (var mapConfig in autoLoadMaps)
        {
            if (!File.Exists(mapConfig.FilePath))
            {
                _logger.LogWarning("Auto-load map file not found: {Path} — skipping", mapConfig.FilePath);
                continue;
            }

            // Check if this version is already loaded (idempotent)
            var existingVersion = await repository.GetActiveMapVersionAsync(
                mapConfig.SourceSystem, mapConfig.TargetSystem, ct);

            if (existingVersion != null && existingVersion.Version == mapConfig.Version)
            {
                _logger.LogInformation("Map {MapName} v{Version} already loaded — skipping",
                    mapConfig.MapName, mapConfig.Version);
                continue;
            }

            var loader = loaders.FirstOrDefault(l =>
                l.Format.Equals(mapConfig.Format, StringComparison.OrdinalIgnoreCase));

            if (loader == null)
            {
                _logger.LogWarning("No loader for format {Format} — skipping {MapName}",
                    mapConfig.Format, mapConfig.MapName);
                continue;
            }

            _logger.LogInformation("Auto-loading map: {MapName} v{Version} ({Format}) from {Path}",
                mapConfig.MapName, mapConfig.Version, mapConfig.Format, mapConfig.FilePath);

            try
            {
                using var stream = File.OpenRead(mapConfig.FilePath);
                var result = await loader.LoadAsync(stream, new MapLoadOptions
                {
                    MapName = mapConfig.MapName,
                    Version = mapConfig.Version,
                    SourceSystem = mapConfig.SourceSystem,
                    TargetSystem = mapConfig.TargetSystem
                }, ct);

                if (result.Success)
                {
                    _logger.LogInformation(
                        "Loaded {MapName} v{Version}: {Entries} entries in {Duration:F1}s",
                        mapConfig.MapName, mapConfig.Version,
                        result.EntriesLoaded, result.Duration.TotalSeconds);
                }
                else
                {
                    _logger.LogError("Failed to load {MapName}: {Errors}",
                        mapConfig.MapName, string.Join("; ", result.Errors));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception loading {MapName} from {Path}",
                    mapConfig.MapName, mapConfig.FilePath);
            }
        }
    }

    /// <summary>
    /// Phase 2: Check NLM and SNOMED International for new map editions.
    /// 
    /// This is a lightweight check — it hits the NLM release metadata endpoint
    /// to see if a newer version exists than what we have loaded. Only downloads
    /// the full RF2 file if a new version is detected.
    /// 
    /// NLM release schedule:
    ///   - US Edition: March 1 and September 1
    ///   - The ICD-10-CM map is included in the US Edition package
    /// 
    /// SNOMED International release schedule:
    ///   - International Edition: January 31 and July 31
    ///   - The ICD-10 map is included in the International Edition
    /// 
    /// For production, the NLM API key is stored in Azure Key Vault and
    /// injected via environment variable UMLS_API_KEY.
    /// </summary>
    private async Task CheckForUpdatesAsync(CancellationToken ct)
    {
        _logger.LogDebug("Checking for map updates...");

        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IConceptMapRepository>();

        // Check current active versions
        var versions = await repository.GetAllMapVersionsAsync(ct);
        var activeVersions = versions.Where(v => v.IsActive).ToList();

        foreach (var version in activeVersions)
        {
            _logger.LogDebug("Active map: {MapName} v{Version} ({Entries} entries, imported {ImportedAt})",
                version.MapName, version.Version, version.EntryCount, version.ImportedAt);
        }

        // TODO: Implement NLM release API check
        // The NLM provides a REST API at https://uts-ws.nlm.nih.gov/rest/
        // that can be queried for the latest SNOMED CT release version.
        // 
        // Workflow:
        // 1. GET https://uts-ws.nlm.nih.gov/rest/content/current/source/SNOMEDCT_US
        //    (with API key auth)
        // 2. Compare returned version against our active map version
        // 3. If newer, download the RF2 package
        // 4. Extract the ExtendedMap file
        // 5. Feed to Rf2MapLoader
        // 6. Previous version auto-deactivated
        //
        // For the demo phase, manual upload via the admin API is sufficient.
        // This automated check is the Phase 2 enhancement.

        _logger.LogDebug("Map update check complete");
    }
}

/// <summary>
/// Extension to register the syndication service.
/// Called from Program.cs: builder.Services.AddMapSyndication()
/// </summary>
public static class MapSyndicationExtensions
{
    public static IServiceCollection AddMapSyndication(this IServiceCollection services)
    {
        services.AddHostedService<MapSyndicationService>();
        return services;
    }
}
