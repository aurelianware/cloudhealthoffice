using CloudHealthOffice.RiskAdjustmentEngine.Data;
using CloudHealthOffice.RiskAdjustmentEngine.Domain;

namespace CloudHealthOffice.RiskAdjustmentEngine.Services;

/// <summary>
/// Maps ICD-10-CM codes to HCC categories using the embedded crosswalk tables.
///
/// ICD-10 codes in the CMS crosswalk have dots removed (e.g., "E11.9" → "E119").
/// This mapper normalizes input codes before lookup so callers may pass either format.
/// </summary>
public class IcdToHccMapper : IIcdToHccMapper
{
    // Indexed at construction for O(1) lookup
    private readonly Dictionary<string, int> _cmsV28Index;
    private readonly Dictionary<string, int> _hhsIndex;

    public IcdToHccMapper()
    {
        _cmsV28Index = HccMappingData.CmsHccV28Mappings
            .ToDictionary(m => m.Icd10.ToUpperInvariant(), m => m.HccCode);

        _hhsIndex = HccMappingData.HhsHccMappings
            .ToDictionary(m => m.Icd10.ToUpperInvariant(), m => m.HccCode);
    }

    public int? Map(string icd10Code, HccModel model)
    {
        var normalized = Normalize(icd10Code);
        var index = model == HccModel.CmsHccV28 ? _cmsV28Index : _hhsIndex;
        return index.TryGetValue(normalized, out var hcc) ? hcc : null;
    }

    public Dictionary<string, int?> MapAll(IEnumerable<string> icd10Codes, HccModel model)
    {
        var result = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in icd10Codes)
        {
            if (!result.ContainsKey(code))
                result[code] = Map(code, model);
        }
        return result;
    }

    /// <summary>
    /// Strips dots and uppercases the code to match the crosswalk format.
    /// E.g. "E11.9" → "E119", "j44.0" → "J440".
    /// </summary>
    internal static string Normalize(string code) =>
        code.Replace(".", "").ToUpperInvariant().Trim();
}
