using FhirService.Models;
using FhirService.Models.PayerToPayer;

namespace FhirService.Services.PayerToPayer;

/// <summary>Deterministic, safe resolution of the member an exchange refers to.</summary>
public interface IPayerToPayerMemberResolver
{
    Task<PayerToPayerMemberResolution> ResolveAsync(
        PayerToPayerExchangeRequest request, CancellationToken ct = default);
}

/// <summary>The resolver's decision: a single matched member, or why not.</summary>
public sealed class PayerToPayerMemberResolution
{
    public PayerToPayerOutcome Outcome { get; init; }
    public ChoMember? Member { get; init; }

    public static PayerToPayerMemberResolution Matched(ChoMember member) =>
        new() { Outcome = PayerToPayerOutcome.Exported, Member = member };

    public static PayerToPayerMemberResolution Failure(PayerToPayerOutcome outcome) =>
        new() { Outcome = outcome };
}

/// <summary>
/// Resolves the member for an inbound respond via the tenant-scoped
/// <see cref="IPayerToPayerMemberSource"/>. The rules are conservative:
/// insufficient criteria, no candidate, or more than one candidate all fail
/// explicitly rather than guessing — the exchange never returns an ambiguous or
/// wrong member's data.
/// </summary>
public sealed class PayerToPayerMemberResolver : IPayerToPayerMemberResolver
{
    private readonly IPayerToPayerMemberSource _source;

    public PayerToPayerMemberResolver(IPayerToPayerMemberSource source) => _source = source;

    public async Task<PayerToPayerMemberResolution> ResolveAsync(
        PayerToPayerExchangeRequest request, CancellationToken ct = default)
    {
        if (!string.Equals(request.TenantId, _source.ServedTenantId, StringComparison.Ordinal))
            return PayerToPayerMemberResolution.Failure(PayerToPayerOutcome.TenantMismatch);

        var criteria = PayerToPayerMemberCriteria.From(request);
        if (!criteria.IsSufficient)
            return PayerToPayerMemberResolution.Failure(PayerToPayerOutcome.InsufficientCriteria);

        var candidates = await _source.FindCandidatesAsync(request.TenantId, criteria, ct);
        return candidates.Count switch
        {
            0 => PayerToPayerMemberResolution.Failure(PayerToPayerOutcome.NoMatch),
            1 => PayerToPayerMemberResolution.Matched(candidates[0]),
            _ => PayerToPayerMemberResolution.Failure(PayerToPayerOutcome.AmbiguousMatch),
        };
    }
}
