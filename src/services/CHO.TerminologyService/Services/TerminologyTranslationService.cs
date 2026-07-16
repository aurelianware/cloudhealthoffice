using CHO.TerminologyService.Configuration;
using CHO.TerminologyService.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace CHO.TerminologyService.Services;

/// <summary>
/// Core terminology translation service.
/// Orchestrates: repository lookup → rule engine → override merge → response assembly.
/// 
/// Translation priority:
/// 1. Plan-specific overrides (tenant-scoped, highest priority)
/// 2. Context-resolved mappings (patient age/gender/state rules applied)
/// 3. Default mappings (priority-ordered, all candidates returned)
/// </summary>
public class TerminologyTranslationService : ITerminologyTranslationService
{
    private readonly IConceptMapRepository _repository;
    private readonly ICodeSystemCatalogRepository _codeSystemCatalog;
    private readonly IContextRuleEngine _ruleEngine;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TerminologyTranslationService> _logger;
    private readonly IOptions<TerminologyServiceOptions> _options;

    public TerminologyTranslationService(
        IConceptMapRepository repository,
        ICodeSystemCatalogRepository codeSystemCatalog,
        IContextRuleEngine ruleEngine,
        IMemoryCache cache,
        ILogger<TerminologyTranslationService> logger,
        IOptions<TerminologyServiceOptions> options)
    {
        _repository = repository;
        _codeSystemCatalog = codeSystemCatalog;
        _ruleEngine = ruleEngine;
        _cache = cache;
        _logger = logger;
        _options = options;
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");

    public async Task<TranslateResponse> TranslateAsync(TranslateRequest request, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(request.System, nameof(request.System));
        ArgumentException.ThrowIfNullOrEmpty(request.Code, nameof(request.Code));
        ArgumentException.ThrowIfNullOrEmpty(request.TargetSystem, nameof(request.TargetSystem));

        var response = new TranslateResponse();

        // 1. Get active map version for audit trail
        var mapVersion = await _repository.GetActiveMapVersionAsync(request.System, request.TargetSystem, ct);
        if (mapVersion != null)
        {
            response.MapVersionId = mapVersion.Id;
        }

        // 2. Look up all candidate entries (cached)
        var cacheKey = $"translate:{request.System}:{request.Code}:{request.TargetSystem}:{request.TenantId ?? "global"}";
        var candidates = await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(_options.Value.CacheMinutes);
            return await _repository.FindBySourceCodeAsync(
                request.System, request.Code, request.TargetSystem, request.TenantId, ct);
        });

        if (candidates == null || candidates.Count == 0)
        {
            response.Result = false;
            response.Message = $"No mapping found for {request.System}|{request.Code} → {request.TargetSystem}";
            _logger.LogInformation("No mapping found: {System}|{Code} → {Target}",
                SanitizeForLog(request.System), SanitizeForLog(request.Code), SanitizeForLog(request.TargetSystem));
            return response;
        }

        // 3. Separate overrides from standard entries
        var overrides = candidates.Where(c => c.IsOverride).ToList();
        var standardEntries = candidates.Where(c => !c.IsOverride).ToList();

        // 4. Apply context rules to standard entries
        var contextResolved = _ruleEngine.ApplyRules(standardEntries, request.Context);

        // 5. Build matches - overrides first, then context-resolved
        var matches = new List<TranslateMatch>();

        foreach (var entry in overrides)
        {
            matches.Add(ToMatch(entry, isContextResolved: false, isOverride: true));
        }

        foreach (var entry in contextResolved)
        {
            // Skip if already covered by an override for the same target code
            if (overrides.Any(o => o.TargetCode == entry.TargetCode))
                continue;

            var isResolved = request.Context != null && entry.Rule != null;
            matches.Add(ToMatch(entry, isContextResolved: isResolved, isOverride: false));
        }

        response.Result = matches.Count > 0;
        response.Matches = matches;

        if (!response.Result)
        {
            response.Message = $"Candidates found but none matched context for {request.System}|{request.Code}";
        }

        _logger.LogDebug("Translated {System}|{Code} → {Target}: {MatchCount} matches (overrides: {OverrideCount})",
            SanitizeForLog(request.System), SanitizeForLog(request.Code), SanitizeForLog(request.TargetSystem), matches.Count, overrides.Count);

        return response;
    }

    public async Task<List<TranslateResponse>> BatchTranslateAsync(
        List<TranslateRequest> requests, CancellationToken ct = default)
    {
        // Process in parallel with bounded concurrency
        var semaphore = new SemaphoreSlim(10);
        var tasks = requests.Select(async request =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                return await TranslateAsync(request, ct);
            }
            finally
            {
                semaphore.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    public async Task<CodeLookupResponse> LookupCodeAsync(CodeLookupRequest request, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(request.System, nameof(request.System));
        ArgumentException.ThrowIfNullOrEmpty(request.Code, nameof(request.Code));

        var response = new CodeLookupResponse
        {
            System = request.System,
            Code = request.Code
        };

        var codeSystemDisplay = await _codeSystemCatalog.FindDisplayAsync(
            request.System,
            request.Code,
            request.TenantId,
            ct);
        if (codeSystemDisplay is not null)
        {
            response.Result = true;
            response.Display = codeSystemDisplay.Display;
            response.MapVersionId = codeSystemDisplay.Version;
            response.Source = codeSystemDisplay.Source;
            return response;
        }

        var candidates = await _repository.FindDisplaysByCodeAsync(
            request.System,
            request.Code,
            request.TenantId,
            ct);

        var match = candidates
            .Select(entry => ToLookupCandidate(entry, request.System, request.Code))
            .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate.Display));

        if (match is null)
        {
            response.Result = false;
            response.Message = $"No display found for {request.System}|{request.Code}";
            return response;
        }

        response.Result = true;
        response.Display = match.Display;
        response.MapVersionId = match.MapVersionId;
        response.Source = match.Source;
        return response;
    }

    public async Task<List<MapVersion>> GetMapVersionsAsync(CancellationToken ct = default)
    {
        return await _repository.GetAllMapVersionsAsync(ct);
    }

    private static TranslateMatch ToMatch(ConceptMapEntry entry, bool isContextResolved, bool isOverride)
    {
        return new TranslateMatch
        {
            Equivalence = entry.Equivalence,
            Concept = new TranslatedCoding
            {
                System = entry.TargetSystem,
                Code = entry.TargetCode,
                Display = entry.TargetDisplay
            },
            IsContextResolved = isContextResolved,
            IsOverride = isOverride,
            Source = isOverride ? "PlanOverride" : DetermineSource(entry.MapVersionId)
        };
    }

    private static string DetermineSource(string mapVersionId)
    {
        // Convention: map version IDs are prefixed with source
        if (mapVersionId.StartsWith("NLM", StringComparison.OrdinalIgnoreCase)) return "NLM";
        if (mapVersionId.StartsWith("AMA", StringComparison.OrdinalIgnoreCase)) return "AMA";
        if (mapVersionId.StartsWith("SNOMED", StringComparison.OrdinalIgnoreCase)) return "SNOMED-Intl";
        return "Unknown";
    }

    private static LookupCandidate ToLookupCandidate(ConceptMapEntry entry, string system, string code)
    {
        if (entry.SourceSystem.Equals(system, StringComparison.Ordinal) &&
            entry.SourceCode.Equals(code, StringComparison.Ordinal))
        {
            return new LookupCandidate(
                entry.SourceDisplay,
                entry.MapVersionId,
                entry.IsOverride ? "PlanOverride" : "ConceptMapSource");
        }

        return new LookupCandidate(
            entry.TargetDisplay,
            entry.MapVersionId,
            entry.IsOverride ? "PlanOverride" : "ConceptMapTarget");
    }

    private sealed record LookupCandidate(string Display, string MapVersionId, string Source);
}
