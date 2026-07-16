using CHO.TerminologyService.Models;

namespace CHO.TerminologyService.Services;

/// <summary>
/// Primary terminology translation service.
/// Implements the FHIR ConceptMap/$translate operation pattern.
/// </summary>
public interface ITerminologyTranslationService
{
    /// <summary>
    /// Translate a code from one system to another.
    /// Applies context rules and plan-specific overrides when available.
    /// </summary>
    Task<TranslateResponse> TranslateAsync(TranslateRequest request, CancellationToken ct = default);

    /// <summary>
    /// Batch translate multiple codes in a single call.
    /// Used by the PAS server for 278-to-FHIR bulk conversion.
    /// </summary>
    Task<List<TranslateResponse>> BatchTranslateAsync(List<TranslateRequest> requests, CancellationToken ct = default);

    /// <summary>
    /// Look up display metadata for a code already expressed in a known code system.
    /// </summary>
    Task<CodeLookupResponse> LookupCodeAsync(CodeLookupRequest request, CancellationToken ct = default);

    /// <summary>
    /// Get all loaded map versions (for admin/audit).
    /// </summary>
    Task<List<MapVersion>> GetMapVersionsAsync(CancellationToken ct = default);
}

/// <summary>
/// Repository for ConceptMap entries.
/// Backed by MongoDB with in-memory caching.
/// </summary>
public interface IConceptMapRepository
{
    /// <summary>Find all entries matching a source code in the active map version.</summary>
    Task<List<ConceptMapEntry>> FindBySourceCodeAsync(
        string sourceSystem, string sourceCode, string targetSystem,
        string? tenantId = null, CancellationToken ct = default);

    /// <summary>Find entries by target code (reverse lookup).</summary>
    Task<List<ConceptMapEntry>> FindByTargetCodeAsync(
        string targetSystem, string targetCode, string sourceSystem,
        string? tenantId = null, CancellationToken ct = default);

    /// <summary>Find active entries where the supplied code appears as source or target.</summary>
    Task<List<ConceptMapEntry>> FindDisplaysByCodeAsync(
        string system, string code, string? tenantId = null, CancellationToken ct = default);

    /// <summary>Bulk insert entries during map loading.</summary>
    Task BulkInsertAsync(List<ConceptMapEntry> entries, CancellationToken ct = default);

    /// <summary>Insert or update a single override entry.</summary>
    Task UpsertOverrideAsync(ConceptMapEntry entry, CancellationToken ct = default);

    /// <summary>Get the active map version for a source→target system pair.</summary>
    Task<MapVersion?> GetActiveMapVersionAsync(string sourceSystem, string targetSystem, CancellationToken ct = default);

    /// <summary>Save a new map version record.</summary>
    Task SaveMapVersionAsync(MapVersion version, CancellationToken ct = default);

    /// <summary>Deactivate all previous versions for a given map name.</summary>
    Task DeactivatePreviousVersionsAsync(string mapName, string exceptVersionId, CancellationToken ct = default);

    /// <summary>Get all map versions.</summary>
    Task<List<MapVersion>> GetAllMapVersionsAsync(CancellationToken ct = default);
}

/// <summary>
/// Repository for display metadata owned by a code system, independent of ConceptMap crosswalks.
/// </summary>
public interface ICodeSystemCatalogRepository
{
    Task<CodeSystemDisplay?> FindDisplayAsync(
        string system, string code, string? tenantId = null, CancellationToken ct = default);

    Task UpsertManyAsync(IEnumerable<CodeSystemConcept> concepts, CancellationToken ct = default);
}

/// <summary>
/// Loads crosswalk data from various source formats.
/// Implementations: Rf2MapLoader (NLM SNOMED), CsvMapLoader (AMA CPT cross maps, plan-specific overrides).
/// </summary>
public interface IMapLoader
{
    /// <summary>Supported format identifier.</summary>
    string Format { get; }

    /// <summary>Load map data from a file path or stream.</summary>
    Task<MapLoadResult> LoadAsync(Stream source, MapLoadOptions options, CancellationToken ct = default);
}

public class MapLoadOptions
{
    /// <summary>Human-readable map name (e.g., "NLM-SNOMED-ICD10CM")</summary>
    public string MapName { get; set; } = string.Empty;

    /// <summary>Version string from source</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Source system URI</summary>
    public string SourceSystem { get; set; } = string.Empty;

    /// <summary>Target system URI</summary>
    public string TargetSystem { get; set; } = string.Empty;

    /// <summary>Tenant ID if loading plan-specific overrides</summary>
    public string? TenantId { get; set; }

    /// <summary>Whether entries should be marked as overrides</summary>
    public bool IsOverride { get; set; } = false;
}

public class MapLoadResult
{
    public bool Success { get; set; }
    public int EntriesLoaded { get; set; }
    public int EntriesSkipped { get; set; }
    public string? MapVersionId { get; set; }
    public List<string> Errors { get; set; } = new();
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// Context-aware rule engine for disambiguating one-to-many mappings.
/// When a SNOMED code maps to multiple ICD-10-CM codes, the rule engine
/// selects the most appropriate one based on patient context.
/// </summary>
public interface IContextRuleEngine
{
    /// <summary>
    /// Given multiple candidate entries and optional patient context,
    /// return the entries that match the context (or all if no context provided).
    /// Results are ordered by priority.
    /// </summary>
    List<ConceptMapEntry> ApplyRules(List<ConceptMapEntry> candidates, PatientContext? context);
}
