using MongoDB.Driver;

namespace ClaimsService.Repositories;

/// <summary>
/// The one question the Provider Access attribution control asks of claims
/// history: has this provider treated this member recently enough to be in a
/// treatment relationship with them?
///
/// <para>
/// Deliberately a boolean and not a panel. fhir-service needs to decide one
/// request about one member, and an endpoint that could list a provider's whole
/// panel would be a new bulk-PHI surface nothing on the Provider Access path
/// asks for. The question is also asked per tenant: a provider treating the
/// same member under two plans is two independent relationships.
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
}
