namespace ClaimsService.Import.Cpcds;

/// <summary>
/// Turns the member identifier a core system puts on its extracts into the
/// CHO member id the claim is filed under, which is what the EOB's
/// <c>patient</c> reference and a SMART patient-bound token both name. A plan's
/// core and its member-facing identifiers often differ (QNXT's internal
/// member key versus the card's member number, a Facets subscriber id plus
/// suffix), and a claim filed under the wrong one is invisible to the member.
/// </summary>
public interface ICoreMemberCrosswalk
{
    /// <summary>
    /// The CHO member id for <paramref name="sourceMemberId"/>, or null when
    /// the crosswalk knows the source system but not this member, in which
    /// case the claim is rejected rather than filed under a guess.
    /// </summary>
    Task<string?> ResolveAsync(
        string tenantId, string? sourceSystem, string sourceMemberId, CancellationToken ct = default);
}

/// <summary>
/// The crosswalk for a plan whose core already uses the CHO member id: the
/// source id is the member id. The default until a tenant loads a crosswalk.
/// </summary>
public sealed class PassThroughMemberCrosswalk : ICoreMemberCrosswalk
{
    public Task<string?> ResolveAsync(
        string tenantId, string? sourceSystem, string sourceMemberId, CancellationToken ct = default)
        => Task.FromResult<string?>(sourceMemberId.Trim());
}
