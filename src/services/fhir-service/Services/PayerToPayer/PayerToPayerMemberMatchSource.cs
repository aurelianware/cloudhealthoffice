using FhirService.Models;
using FhirService.Models.PayerToPayer;
using FhirService.Services;
using Microsoft.Extensions.Options;

namespace FhirService.Services.PayerToPayer;

/// <summary>
/// Tenant-scoped source of the CHO-owned member/coverage directory used by the
/// Payer-to-Payer <c>$member-match</c> (P2P-04). It reuses the same
/// <see cref="IChoMemberDirectory"/> the Patient Access data provider serves —
/// no P2P-only store — and adds the tenant boundary the match requires: a
/// request for a tenant this instance does not serve resolves nothing, so a
/// caller can never enumerate or match against another tenant's members.
/// </summary>
public interface IPayerToPayerMemberMatchSource
{
    /// <summary>The tenant this source serves. Requests for other tenants match nothing.</summary>
    string ServedTenantId { get; }

    /// <summary>
    /// Every member within the tenant that could be a strong match for
    /// <paramref name="criteria"/> (a superset: <see cref="MemberMatchPolicy"/>
    /// decides). Empty when the tenant is not served. See <see cref="MemberMatchCandidates"/>
    /// for a source that cannot search the criteria, or cannot list every candidate.
    /// </summary>
    Task<MemberMatchCandidates> GetCandidatesAsync(
        string tenantId, MemberMatchCriteria criteria, CancellationToken ct = default);

    /// <summary>The member's coverage records within the tenant. Empty when the tenant is not served.</summary>
    Task<IReadOnlyList<ChoCoverage>> GetCoveragesAsync(
        string tenantId, string memberId, CancellationToken ct = default);
}

/// <summary>
/// Candidates for a member match.
/// <list type="bullet">
///   <item><description><see cref="Searchable"/> false: the source cannot look the
///     criteria up (an SSN-only request where SSNs cannot be searched, ADR 020);
///     the match answers insufficient criteria.</description></item>
///   <item><description><see cref="Complete"/> false: there were more candidates than
///     the source reads, so uniqueness cannot be proven; the match answers ambiguous.</description></item>
/// </list>
/// </summary>
public sealed record MemberMatchCandidates(IReadOnlyList<ChoMember> Members, bool Searchable = true, bool Complete = true)
{
    public static readonly MemberMatchCandidates None = new([]);
    public static readonly MemberMatchCandidates NotSearchable = new([], Searchable: false);
}

/// <summary>
/// Default <see cref="IPayerToPayerMemberMatchSource"/> over the CHO member
/// directory, scoped to the fhir-service's configured tenant
/// (<see cref="FhirAdapterOptions.TenantId"/>). It offers every member as a
/// candidate; the mock directory is small.
/// </summary>
public sealed class PatientAccessPayerToPayerMemberMatchSource : IPayerToPayerMemberMatchSource
{
    private readonly IChoMemberDirectory _directory;

    public PatientAccessPayerToPayerMemberMatchSource(
        IChoMemberDirectory directory, IOptions<FhirAdapterOptions> options)
    {
        _directory = directory;
        ServedTenantId = string.IsNullOrWhiteSpace(options.Value.TenantId)
            ? "demo-tenant"
            : options.Value.TenantId.Trim();
    }

    public string ServedTenantId { get; }

    public async Task<MemberMatchCandidates> GetCandidatesAsync(
        string tenantId, MemberMatchCriteria criteria, CancellationToken ct = default)
        => ServesTenant(tenantId) ? new MemberMatchCandidates(await _directory.GetAllMembersAsync(ct)) : MemberMatchCandidates.None;

    public async Task<IReadOnlyList<ChoCoverage>> GetCoveragesAsync(
        string tenantId, string memberId, CancellationToken ct = default)
        => ServesTenant(tenantId)
            ? await _directory.GetCoveragesByMemberIdAsync(memberId, ct)
            : Array.Empty<ChoCoverage>();

    // Trim the incoming tenant id: it can arrive from headers/claims without being
    // trimmed upstream, and ServedTenantId is already trimmed, so comparing raw
    // would turn a stray space into a false miss. Trimming never widens scope.
    private bool ServesTenant(string? tenantId) =>
        string.Equals(tenantId?.Trim(), ServedTenantId, StringComparison.Ordinal);
}
