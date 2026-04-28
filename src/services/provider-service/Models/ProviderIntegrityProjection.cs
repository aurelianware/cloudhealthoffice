namespace ProviderService.Models;

/// <summary>
/// Read-side snapshot of a provider's integrity-projection state, fed to
/// <see cref="Services.IFhirPractitionerProjector"/>. Decouples the
/// projector from the storage shape — today the four fields live directly
/// on <see cref="Provider"/> (capability 5.4.5), but the projector
/// interface accepts this record so a future move to a separate document
/// is a controller-side change only.
/// </summary>
/// <param name="Score">Composite integrity score (0–100). Null when the projection has not yet been populated.</param>
/// <param name="Rating">Rating bucket (Clear / Advisory / Caution / Alert / Blocked / Unknown). Null when no score has been computed.</param>
/// <param name="LastVerifiedAt">When provider-verification-service produced the score. Null when never verified.</param>
public sealed record ProviderIntegrityProjection(
    int? Score,
    string? Rating,
    DateTimeOffset? LastVerifiedAt)
{
    /// <summary>
    /// Build a projection snapshot from the four fields embedded on
    /// <see cref="Provider"/>. Returns null when no score is available so
    /// the projector can omit the integrity extension entirely (no
    /// placeholder, no zero score).
    /// </summary>
    public static ProviderIntegrityProjection? FromProvider(Provider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (provider.IntegrityScore is null) return null;
        return new ProviderIntegrityProjection(
            provider.IntegrityScore,
            provider.IntegrityRating,
            provider.LastVerifiedAt);
    }
}
