using ClaimsService.Models;
using MongoDB.Driver;

namespace ClaimsService.Repositories;

/// <summary>MongoDB implementation of <see cref="IProviderAttributionRepository"/>.</summary>
public sealed class ProviderAttributionRepositoryMongo : IProviderAttributionRepository
{
    private readonly IMongoCollection<Claim> _collection;

    public ProviderAttributionRepositoryMongo(IMongoDatabase database)
    {
        _collection = database.GetCollection<Claim>("Claims");
    }

    public async Task<bool> HasTreatedAsync(
        string tenantId,
        string providerNpi,
        string memberId,
        DateTime sinceUtc,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        // Blank ids attribute no one. Guarded here as well as at the caller so
        // the filter can never degrade into "any provider" or "any member".
        if (string.IsNullOrWhiteSpace(providerNpi) || string.IsNullOrWhiteSpace(memberId))
            return false;

        var filter = BuildFilter(tenantId, providerNpi, memberId, sinceUtc);

        // Existence, not a count: one matching claim settles the question and
        // the rest of the member's history is nothing this caller needs read.
        var match = await _collection.Find(filter)
            .Project(c => c.Id)
            .Limit(1)
            .FirstOrDefaultAsync(ct);

        return match is not null;
    }

    internal static FilterDefinition<Claim> BuildFilter(
        string tenantId, string providerNpi, string memberId, DateTime sinceUtc)
    {
        var b = Builders<Claim>.Filter;
        var npi = providerNpi.Trim();

        return b.Eq(c => c.TenantId, tenantId)
               & b.Eq(c => c.MemberId, memberId.Trim())
               // Current versions only, as the FHIR search repository defines
               // them. A superseded version describes the same visit as its
               // chain head, so counting it would change nothing but the cost.
               & b.Eq(c => c.SupersededAt, (DateTime?)null)
               & b.Ne(c => c.VersionState, ClaimVersionState.Draft)
               // A void withdraws the assertion that the service happened, so
               // it cannot evidence a treatment relationship. Pended and denied
               // claims still can: the plan refusing to pay for a visit is not
               // the plan saying the visit did not occur.
               & b.Ne(c => c.Status, ClaimStatus.Voided)
               // Billing OR rendering. CMS attribution conventions count both,
               // and a solo practitioner is frequently only the billing NPI.
               & b.Or(
                   b.Eq(c => c.BillingProviderNPI, npi),
                   b.Eq(c => c.RenderingProviderNPI, npi))
               // The END of the service period against the lookback floor: a
               // stay that began before the window but ran into it is recent
               // treatment, and excluding it would drop exactly the long
               // admissions where the relationship is strongest.
               & b.Gte(c => c.ServiceDateTo, AsUtc(sinceUtc));
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
