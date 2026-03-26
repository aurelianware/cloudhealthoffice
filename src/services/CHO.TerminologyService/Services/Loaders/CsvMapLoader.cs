using CHO.TerminologyService.Models;

namespace CHO.TerminologyService.Services.Loaders;

/// <summary>
/// Loads crosswalk data from CSV files.
/// Used for:
///   - AMA CPT-to-SNOMED cross maps (customer provides under their AMA license)
///   - Plan-specific overrides (TMPPM, state Medicaid, local coding conventions)
///   - Custom mappings created by plan clinical staff
/// 
/// Expected CSV format (header row required):
///   source_code,source_display,target_code,target_display,equivalence,priority,rule_type,rule_value
/// 
/// For plan overrides, the TenantId in MapLoadOptions scopes entries to that plan.
/// </summary>
public class CsvMapLoader : IMapLoader
{
    private readonly IConceptMapRepository _repository;
    private readonly ILogger<CsvMapLoader> _logger;

    public string Format => "CSV";

    public CsvMapLoader(IConceptMapRepository repository, ILogger<CsvMapLoader> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<MapLoadResult> LoadAsync(Stream source, MapLoadOptions options, CancellationToken ct = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = new MapLoadResult();
        var entries = new List<ConceptMapEntry>();

        var mapVersionId = $"{options.MapName}-{options.Version}-{DateTime.UtcNow:yyyyMMddHHmmss}";

        try
        {
            using var reader = new StreamReader(source);
            var headerLine = await reader.ReadLineAsync(ct);
            if (headerLine == null)
            {
                result.Errors.Add("Empty file");
                return result;
            }

            var headers = headerLine.Split(',').Select(h => h.Trim().ToLowerInvariant()).ToArray();
            var headerMap = new Dictionary<string, int>();
            for (int i = 0; i < headers.Length; i++)
            {
                headerMap[headers[i]] = i;
            }

            if (!headerMap.ContainsKey("source_code") || !headerMap.ContainsKey("target_code"))
            {
                result.Errors.Add("CSV must contain 'source_code' and 'target_code' columns");
                return result;
            }

            int lineNumber = 1;
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) != null)
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var fields = ParseCsvLine(line);
                    var sourceCode = GetCsvField(fields, headerMap, "source_code");
                    var targetCode = GetCsvField(fields, headerMap, "target_code");

                    if (string.IsNullOrEmpty(sourceCode) || string.IsNullOrEmpty(targetCode))
                    {
                        result.EntriesSkipped++;
                        continue;
                    }

                    var entry = new ConceptMapEntry
                    {
                        Id = $"{mapVersionId}:{sourceCode}:{targetCode}:{lineNumber}",
                        SourceSystem = options.SourceSystem,
                        SourceCode = sourceCode,
                        SourceDisplay = GetCsvFieldOptional(fields, headerMap, "source_display") ?? "",
                        TargetSystem = options.TargetSystem,
                        TargetCode = targetCode,
                        TargetDisplay = GetCsvFieldOptional(fields, headerMap, "target_display") ?? "",
                        Equivalence = GetCsvFieldOptional(fields, headerMap, "equivalence") ?? "equivalent",
                        Priority = int.TryParse(GetCsvFieldOptional(fields, headerMap, "priority"), out var p) ? p : 1,
                        Rule = ParseCsvRule(fields, headerMap),
                        MapVersionId = mapVersionId,
                        IsOverride = options.IsOverride,
                        TenantId = options.TenantId
                    };

                    entries.Add(entry);
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Line {lineNumber}: {ex.Message}");
                }
            }

            if (entries.Count > 0)
            {
                if (options.IsOverride)
                {
                    // For overrides, upsert individually (they may update existing entries)
                    foreach (var entry in entries)
                    {
                        await _repository.UpsertOverrideAsync(entry, ct);
                    }
                }
                else
                {
                    await _repository.BulkInsertAsync(entries, ct);
                }

                var mapVersion = new MapVersion
                {
                    Id = mapVersionId,
                    MapName = options.MapName,
                    Version = options.Version,
                    SourceSystem = options.SourceSystem,
                    TargetSystem = options.TargetSystem,
                    ImportedAt = DateTime.UtcNow,
                    IsActive = true,
                    EntryCount = entries.Count
                };

                if (!options.IsOverride)
                {
                    await _repository.DeactivatePreviousVersionsAsync(options.MapName, mapVersionId, ct);
                }
                await _repository.SaveMapVersionAsync(mapVersion, ct);
            }

            result.Success = true;
            result.EntriesLoaded = entries.Count;
            result.MapVersionId = mapVersionId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load CSV map file");
            result.Errors.Add($"Fatal: {ex.Message}");
        }

        stopwatch.Stop();
        result.Duration = stopwatch.Elapsed;
        return result;
    }

    private MapRule? ParseCsvRule(string[] fields, Dictionary<string, int> headerMap)
    {
        var ruleType = GetCsvFieldOptional(fields, headerMap, "rule_type");
        if (string.IsNullOrEmpty(ruleType)) return null;

        var ruleValue = GetCsvFieldOptional(fields, headerMap, "rule_value") ?? "";

        var rule = new MapRule { RuleType = ruleType };

        switch (ruleType.ToLowerInvariant())
        {
            case "age":
                // rule_value format: "min-max" (e.g., "0-17", "65-150")
                var ageParts = ruleValue.Split('-');
                if (ageParts.Length == 2)
                {
                    if (int.TryParse(ageParts[0], out var min)) rule.AgeMin = min;
                    if (int.TryParse(ageParts[1], out var max)) rule.AgeMax = max;
                }
                break;
            case "gender":
                rule.Gender = ruleValue;
                break;
            case "statespecific":
                rule.StateCode = ruleValue;
                break;
            case "comorbidity":
                rule.CoMorbidCodes = ruleValue.Split(';').Select(x => x.Trim()).Where(x => !string.IsNullOrEmpty(x)).ToList();
                break;
            default:
                rule.Expression = ruleValue;
                break;
        }

        return rule;
    }

    /// <summary>Basic CSV parser that handles quoted fields.</summary>
    private string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = "";
        var inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (line[i] == ',' && !inQuotes)
            {
                fields.Add(current.Trim());
                current = "";
            }
            else
            {
                current += line[i];
            }
        }
        fields.Add(current.Trim());
        return fields.ToArray();
    }

    private string GetCsvField(string[] fields, Dictionary<string, int> headerMap, string columnName)
    {
        if (!headerMap.TryGetValue(columnName, out var idx) || idx >= fields.Length)
            return "";
        return fields[idx].Trim();
    }

    private string? GetCsvFieldOptional(string[] fields, Dictionary<string, int> headerMap, string columnName)
    {
        var value = GetCsvField(fields, headerMap, columnName);
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
