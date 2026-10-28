using MongoDB.Driver;

namespace ClaimsService.Repositories;

/// <summary>
/// The questions the Provider Access attribution control asks of claims
/// history: has this provider treated this member recently enough to be in a
/// treatment relationship with them, and, for a bulk export (G-34), which
/// members has it treated?
///
/// <para>
/// A single read asks the boolean and nothing more. The panel exists only for
/// bulk <c>$export</c>, where fhir-service has to know whom to ask about: it
/// returns member ids and nothing else, is served to fhir-service's own
/// service identity only, and every member it lists is decided again one at a
/// time (attribution, then opt-out) before any data is written. The question
/// is also asked per tenant: a provider treating the same member under two
/// plans is two independent relationships.
/// </para>
/// </summary>
public interface IProviderAttributionRepository
{
    /// <summary>
    /// True when the tenant holds at least one current, non-voided claim for
    /// <paramref name="memberId"/> on which <paramref name="providerNpi"/> is
    /// the billing or the rendering provider and whose service period ended on
    /// or after <paramref name="sinceUtc"/>.
    /// </summary>
    Task<bool> HasTreatedAsync(
        string tenantId,
        string providerNpi,
        string memberId,
        DateTime sinceUtc,
        CancellationToken ct = default);

    /// <summary>
    /// Every member <see cref="HasTreatedAsync"/> would answer true for, for
    /// this provider in this tenant, in ordinal order. Blank ids list no one.
    /// </summary>
    Task<IReadOnlyList<string>> ListTreatedMembersAsync(
        string tenantId,
        string providerNpi,
        DateTime sinceUtc,
        CancellationToken ct = default);
}
