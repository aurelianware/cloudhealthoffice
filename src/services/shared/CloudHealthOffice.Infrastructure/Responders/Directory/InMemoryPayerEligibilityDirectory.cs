using CloudHealthOffice.Infrastructure.Responders.Models;

namespace CloudHealthOffice.Infrastructure.Responders.Directory;

/// <summary>
/// In-memory CHO eligibility directory for Development and tests. Seeded with
/// the synthetic CHO Demo Health Plan. Write methods exist only so tests can
/// prove the responder never calls them.
/// </summary>
public sealed class InMemoryPayerEligibilityDirectory : IPayerEligibilityDirectory
{
    private readonly IReadOnlyList<PayerEligibilityRoute> _routes;
    private readonly IReadOnlyList<PayerDirectoryMember> _members;
    private readonly IReadOnlyList<PayerDirectoryCoverage> _coverages;
    private readonly IReadOnlyList<PayerDirectoryPlan> _plans;
    private readonly Dictionary<string, PayerDirectoryAccumulatorSnapshot> _accumulators;
    private readonly IReadOnlyList<PayerDirectoryProvider> _providers;

    public InMemoryPayerEligibilityDirectory()
        : this(
            ChoDemoEligibilitySeed.Routes,
            ChoDemoEligibilitySeed.Members,
            ChoDemoEligibilitySeed.Coverages,
            ChoDemoEligibilitySeed.Plans,
            ChoDemoEligibilitySeed.Accumulators,
            ChoDemoEligibilitySeed.Providers)
    {
    }

    public InMemoryPayerEligibilityDirectory(
        IReadOnlyList<PayerEligibilityRoute> routes,
        IReadOnlyList<PayerDirectoryMember> members,
        IReadOnlyList<PayerDirectoryCoverage> coverages,
        IReadOnlyList<PayerDirectoryPlan> plans,
        IReadOnlyList<PayerDirectoryAccumulatorSnapshot> accumulators,
        IReadOnlyList<PayerDirectoryProvider> providers)
    {
        _routes = routes;
        _members = members;
        _coverages = coverages;
        _plans = plans;
        _accumulators = accumulators.ToDictionary(AccumulatorKey, StringComparer.OrdinalIgnoreCase);
        _providers = providers;
        MutationProbe = new PayerEligibilityMutationProbe();
    }

    public PayerEligibilityMutationProbe MutationProbe { get; }

    public IReadOnlyList<PayerEligibilityRoute> GetInboundRoutes() => _routes;

    public Task<MemberLookupResult> FindSubscriberAsync(
        string tenantId, PersonLookupQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || !query.HasIdentity)
        {
            return Task.FromResult(MemberLookupResult.Invalid());
        }

        var candidates = _members
            .Where(m => TenantEquals(m.TenantId, tenantId) && m.IsSubscriber)
            .Where(m => Matches(m, query))
            .ToList();

        return Task.FromResult(ToLookupResult(candidates));
    }

    public Task<MemberLookupResult> FindDependentAsync(
        string tenantId, string subscriberMemberId, PersonLookupQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) ||
            string.IsNullOrWhiteSpace(subscriberMemberId) ||
            !query.HasIdentity)
        {
            return Task.FromResult(MemberLookupResult.Invalid());
        }

        var candidates = _members
            .Where(m => TenantEquals(m.TenantId, tenantId) && !m.IsSubscriber)
            .Where(m => string.Equals(m.SubscriberMemberId, subscriberMemberId, StringComparison.OrdinalIgnoreCase))
            .Where(m => Matches(m, query))
            .ToList();

        // A person who exists but is not related to this subscriber is NotFound
        // for this inquiry — never return another subscriber's dependent.
        return Task.FromResult(ToLookupResult(candidates));
    }

    public Task<PayerDirectoryCoverage?> GetCoverageAsync(
        string tenantId, string memberId, DateOnly serviceDate, CancellationToken ct = default)
    {
        var coverage = _coverages.FirstOrDefault(c =>
            TenantEquals(c.TenantId, tenantId) &&
            string.Equals(c.MemberId, memberId, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(coverage);
    }

    public Task<PayerDirectoryPlan?> GetPlanAsync(
        string tenantId, string planId, CancellationToken ct = default)
    {
        var plan = _plans.FirstOrDefault(p =>
            TenantEquals(p.TenantId, tenantId) &&
            string.Equals(p.PlanId, planId, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(plan);
    }

    public Task<PayerDirectoryAccumulatorSnapshot?> GetAccumulatorsAsync(
        string tenantId, string memberId, string planId, CancellationToken ct = default)
    {
        _accumulators.TryGetValue(AccumulatorKey(tenantId, memberId, planId), out var snapshot);
        return Task.FromResult(snapshot);
    }

    public Task<PayerDirectoryProvider?> FindProviderAsync(
        string tenantId, string? npi, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(npi))
        {
            return Task.FromResult<PayerDirectoryProvider?>(null);
        }

        var provider = _providers.FirstOrDefault(p =>
            TenantEquals(p.TenantId, tenantId) &&
            string.Equals(p.Npi, npi, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(provider);
    }

    /// <summary>Test hook — increments the accumulator write counter and optionally mutates remaining deductible.</summary>
    public void RecordAccumulatorWrite(string tenantId, string memberId, string planId, decimal? remainingDeductible = null)
    {
        MutationProbe.AccumulatorWrites++;
        if (remainingDeductible is not { } remaining)
        {
            return;
        }

        var key = AccumulatorKey(tenantId, memberId, planId);
        if (!_accumulators.TryGetValue(key, out var current))
        {
            return;
        }

        _accumulators[key] = new PayerDirectoryAccumulatorSnapshot
        {
            TenantId = current.TenantId,
            MemberId = current.MemberId,
            PlanId = current.PlanId,
            IndividualDeductible = current.IndividualDeductible,
            IndividualDeductibleMet = current.IndividualDeductible - remaining,
            IndividualDeductibleRemaining = remaining,
            FamilyDeductible = current.FamilyDeductible,
            FamilyDeductibleMet = current.FamilyDeductibleMet,
            FamilyDeductibleRemaining = current.FamilyDeductibleRemaining,
            IndividualOutOfPocketMax = current.IndividualOutOfPocketMax,
            IndividualOutOfPocketMet = current.IndividualOutOfPocketMet,
            IndividualOutOfPocketRemaining = current.IndividualOutOfPocketRemaining,
            FamilyOutOfPocketMax = current.FamilyOutOfPocketMax,
            FamilyOutOfPocketMet = current.FamilyOutOfPocketMet,
            FamilyOutOfPocketRemaining = current.FamilyOutOfPocketRemaining
        };
    }

    public void RecordClaimCreate() => MutationProbe.ClaimCreates++;

    public void RecordAuthorizationCreate() => MutationProbe.AuthorizationCreates++;

    public void RecordPaymentCreate() => MutationProbe.PaymentCreates++;

    public void RecordMemberWrite() => MutationProbe.MemberWrites++;

    public void RecordCoverageWrite() => MutationProbe.CoverageWrites++;

    private static MemberLookupResult ToLookupResult(List<PayerDirectoryMember> candidates)
    {
        if (candidates.Count == 0)
        {
            return MemberLookupResult.NotFound();
        }

        if (candidates.Count > 1)
        {
            // Never expose another member's data when lookup is ambiguous.
            return MemberLookupResult.Ambiguous();
        }

        return MemberLookupResult.Matched(candidates[0]);
    }

    private static bool Matches(PayerDirectoryMember member, PersonLookupQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.MemberId))
        {
            if (!string.Equals(member.MemberId, query.MemberId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Member id matched. Optional demographics, when supplied, must also match exactly.
            if (!string.IsNullOrWhiteSpace(query.FirstName) &&
                !string.Equals(member.FirstName, query.FirstName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(query.LastName) &&
                !string.Equals(member.LastName, query.LastName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (query.DateOfBirth is { } dob && member.DateOfBirth != dob)
            {
                return false;
            }

            return true;
        }

        // No member id: require exact first + last + DOB. Missing any of the
        // three is InvalidRequest at the caller; here treat incomplete name+DOB
        // as no match rather than guessing.
        if (string.IsNullOrWhiteSpace(query.FirstName) ||
            string.IsNullOrWhiteSpace(query.LastName) ||
            query.DateOfBirth is null)
        {
            return false;
        }

        return string.Equals(member.FirstName, query.FirstName, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(member.LastName, query.LastName, StringComparison.OrdinalIgnoreCase) &&
               member.DateOfBirth == query.DateOfBirth;
    }

    private static bool TenantEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string AccumulatorKey(PayerDirectoryAccumulatorSnapshot snapshot) =>
        AccumulatorKey(snapshot.TenantId, snapshot.MemberId, snapshot.PlanId);

    private static string AccumulatorKey(string tenantId, string memberId, string planId) =>
        $"{tenantId}|{memberId}|{planId}";
}
