using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.Infrastructure.Responders.Directory;

/// <summary>
/// Read-only Cloud Health Office lookups used by the payer eligibility
/// responder. Production hosts should back this with member-service,
/// coverage-service, benefit-plan-service, provider-service, and
/// accumulator-service. The in-memory implementation is the development /
/// test projection of those same concepts.
///
/// Implementations must not persist inquiries, consume accumulators, create
/// claims, authorizations, or payments, or mutate enrollment / coverage as
/// part of an eligibility inquiry.
/// </summary>
public interface IPayerEligibilityDirectory
{
    IReadOnlyList<PayerEligibilityRoute> GetInboundRoutes();

    Task<MemberLookupResult> FindSubscriberAsync(
        string tenantId,
        PersonLookupQuery query,
        CancellationToken ct = default);

    Task<MemberLookupResult> FindDependentAsync(
        string tenantId,
        string subscriberMemberId,
        PersonLookupQuery query,
        CancellationToken ct = default);

    Task<PayerDirectoryCoverage?> GetCoverageAsync(
        string tenantId,
        string memberId,
        DateOnly serviceDate,
        CancellationToken ct = default);

    Task<PayerDirectoryPlan?> GetPlanAsync(
        string tenantId,
        string planId,
        CancellationToken ct = default);

    Task<PayerDirectoryAccumulatorSnapshot?> GetAccumulatorsAsync(
        string tenantId,
        string memberId,
        string planId,
        CancellationToken ct = default);

    Task<PayerDirectoryProvider?> FindProviderAsync(
        string tenantId,
        string? npi,
        CancellationToken ct = default);

    /// <summary>Mutation counters. Always zero across a well-behaved inquiry.</summary>
    PayerEligibilityMutationProbe MutationProbe { get; }
}

/// <summary>
/// Exact-match person query. Empty queries are
/// <see cref="Models.MemberLookupStatus.InvalidRequest"/>. Matching is
/// identity-based (member id and/or name+DOB), never fuzzy.
/// </summary>
public sealed class PersonLookupQuery
{
    public string? MemberId { get; init; }

    public string? FirstName { get; init; }

    public string? LastName { get; init; }

    public DateOnly? DateOfBirth { get; init; }

    public bool HasIdentity =>
        !string.IsNullOrWhiteSpace(MemberId) ||
        !string.IsNullOrWhiteSpace(FirstName) ||
        !string.IsNullOrWhiteSpace(LastName) ||
        DateOfBirth is not null;

    public static PersonLookupQuery From(GatewayEligibilityPerson? person)
    {
        if (person is null)
        {
            return new PersonLookupQuery();
        }

        return new PersonLookupQuery
        {
            MemberId = person.MemberId,
            FirstName = person.FirstName,
            LastName = person.LastName,
            DateOfBirth = person.DateOfBirth
        };
    }
}
