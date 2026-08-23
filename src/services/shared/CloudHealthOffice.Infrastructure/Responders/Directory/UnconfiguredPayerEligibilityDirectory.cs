using CloudHealthOffice.Infrastructure.Responders.Models;

namespace CloudHealthOffice.Infrastructure.Responders.Directory;

/// <summary>
/// Placeholder directory registered when
/// <c>PayerEligibilityResponder:UseInMemoryDirectory</c> is false and the
/// host has not supplied its own <see cref="IPayerEligibilityDirectory"/>.
/// Returns no routes so inbound inquiries fail closed instead of answering
/// from the synthetic CHO Demo seed.
/// </summary>
internal sealed class UnconfiguredPayerEligibilityDirectory : IPayerEligibilityDirectory
{
    public PayerEligibilityMutationProbe MutationProbe { get; } = new();

    public IReadOnlyList<PayerEligibilityRoute> GetInboundRoutes() => Array.Empty<PayerEligibilityRoute>();

    public Task<MemberLookupResult> FindSubscriberAsync(
        string tenantId, PersonLookupQuery query, CancellationToken ct = default) =>
        Task.FromResult(MemberLookupResult.NotFound());

    public Task<MemberLookupResult> FindDependentAsync(
        string tenantId, string subscriberMemberId, PersonLookupQuery query, CancellationToken ct = default) =>
        Task.FromResult(MemberLookupResult.NotFound());

    public Task<PayerDirectoryCoverage?> GetCoverageAsync(
        string tenantId, string memberId, DateOnly serviceDate, CancellationToken ct = default) =>
        Task.FromResult<PayerDirectoryCoverage?>(null);

    public Task<PayerDirectoryPlan?> GetPlanAsync(
        string tenantId, string planId, CancellationToken ct = default) =>
        Task.FromResult<PayerDirectoryPlan?>(null);

    public Task<PayerDirectoryAccumulatorSnapshot?> GetAccumulatorsAsync(
        string tenantId, string memberId, string planId, CancellationToken ct = default) =>
        Task.FromResult<PayerDirectoryAccumulatorSnapshot?>(null);

    public Task<PayerDirectoryProvider?> FindProviderAsync(
        string tenantId, string? npi, CancellationToken ct = default) =>
        Task.FromResult<PayerDirectoryProvider?>(null);
}
