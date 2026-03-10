using CloudHealthOffice.RiskAdjustmentEngine.Domain;

namespace CloudHealthOffice.RiskAdjustmentEngine.Services;

/// <summary>
/// Maps ICD-10-CM diagnosis codes to HCC category codes using the
/// appropriate CMS-published crosswalk for the specified model year.
/// </summary>
public interface IIcdToHccMapper
{
    /// <summary>
    /// Maps a single ICD-10-CM code to an HCC category code.
    /// Returns null if the code does not map to any HCC (non-HCC-relevant diagnosis).
    /// </summary>
    int? Map(string icd10Code, HccModel model);

    /// <summary>
    /// Maps a collection of ICD-10-CM codes, deduplicating HCC categories.
    /// Returns a dictionary of {icd10Code → hccCode?} for audit purposes.
    /// </summary>
    Dictionary<string, int?> MapAll(IEnumerable<string> icd10Codes, HccModel model);
}
