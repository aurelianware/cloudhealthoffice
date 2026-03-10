using CloudHealthOffice.EncounterEngine.Domain;

namespace CloudHealthOffice.EncounterEngine.Services;

/// <summary>
/// Transforms an adjudicated claim into a single X12 837 encounter transaction (ST through SE).
/// </summary>
public interface IEncounterTransformer
{
    /// <summary>
    /// Produces an <see cref="EncounterRecord"/> whose <see cref="EncounterRecord.RawX12"/>
    /// contains the complete ST…SE transaction set for submission.
    /// </summary>
    EncounterRecord Transform(EncounterInput input);
}
