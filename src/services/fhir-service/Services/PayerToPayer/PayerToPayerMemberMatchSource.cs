using FhirService.Models;
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

    /// <summary>Candidate members within the tenant. Empty when the tenant is not served.</summary>
    Task<IReadOnlyList<ChoMember>> GetMembersAsync(string tenantId, CancellationToken ct = default);

    /// <summary>The member's coverage records within the tenant. Empty when the tenant is not served.</summary>
    Task<IReadOnlyList<ChoCoverage>> GetCoveragesAsync(
        string tenantId, string memberId, CancellationToken ct = default);
}

/// <summary>
/// Default <see cref="IPayerToPayerMemberMatchSource"/> over the CHO member
/// directory, scoped to the fhir-service's configured tenant
/// (<see cref="FhirAdapterOptions.TenantId"/>).
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

    public async Task<IReadOnlyList<ChoMember>> GetMembersAsync(string tenantId, CancellationToken ct = default)
        => ServesTenant(tenantId) ? await _directory.GetAllMembersAsync(ct) : Array.Empty<ChoMember>();

    public async Task<IReadOnlyList<ChoCoverage>> GetCoveragesAsync(
        string tenantId, string memberId, CancellationToken ct = default)
        => ServesTenant(tenantId)
            ? await _directory.GetCoveragesByMemberIdAsync(memberId, ct)
            : Array.Empty<ChoCoverage>();

    private bool ServesTenant(string tenantId) =>
        string.Equals(tenantId, ServedTenantId, StringComparison.Ordinal);
}
