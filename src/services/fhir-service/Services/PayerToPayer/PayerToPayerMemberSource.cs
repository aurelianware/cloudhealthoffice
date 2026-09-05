using FhirService.Models;
using FhirService.Models.PayerToPayer;
using Microsoft.Extensions.Options;

namespace FhirService.Services.PayerToPayer;

/// <summary>
/// Tenant-scoped source of the CHO-owned member and payment data used by the
/// inbound Payer-to-Payer respond. It reuses the existing
/// <see cref="IPatientAccessDataProvider"/> rather than a P2P-only store, and
/// adds the tenant boundary the exchange requires.
/// </summary>
public interface IPayerToPayerMemberSource
{
    /// <summary>The tenant this source serves. Requests for other tenants match nothing.</summary>
    string ServedTenantId { get; }

    /// <summary>
    /// Candidate members matching the criteria within the tenant. For P2P-01 this
    /// resolves by member id and confirms any supplied demographics, so a wrong
    /// member is never returned. Empty when the tenant is not served, the
    /// criteria are insufficient, or nothing matches.
    /// </summary>
    Task<IReadOnlyList<ChoMember>> FindCandidatesAsync(
        string tenantId, PayerToPayerMemberCriteria criteria, CancellationToken ct = default);

    Task<IReadOnlyList<ChoPaymentDocument>> GetPaymentsAsync(
        string tenantId, string memberId, CancellationToken ct = default);
}

/// <summary>
/// Default <see cref="IPayerToPayerMemberSource"/> over
/// <see cref="IPatientAccessDataProvider"/>, scoped to the fhir-service's
/// configured tenant (<see cref="FhirAdapterOptions.TenantId"/>). Member
/// identity is resolved deterministically by member id; any demographics the
/// receiving payer supplied must also agree, guarding against a wrong-member
/// match. No fuzzy matching, no demographic-only search (that is P2P-04).
/// </summary>
public sealed class PatientAccessPayerToPayerMemberSource : IPayerToPayerMemberSource
{
    private readonly IPatientAccessDataProvider _provider;

    public PatientAccessPayerToPayerMemberSource(
        IPatientAccessDataProvider provider, IOptions<FhirAdapterOptions> options)
    {
        _provider = provider;
        ServedTenantId = string.IsNullOrWhiteSpace(options.Value.TenantId)
            ? "demo-tenant"
            : options.Value.TenantId.Trim();
    }

    public string ServedTenantId { get; }

    public async Task<IReadOnlyList<ChoMember>> FindCandidatesAsync(
        string tenantId, PayerToPayerMemberCriteria criteria, CancellationToken ct = default)
    {
        if (!ServesTenant(tenantId) || string.IsNullOrWhiteSpace(criteria.MemberId))
            return Array.Empty<ChoMember>();

        var member = await _provider.GetMemberAsync(criteria.MemberId, ct);
        if (member is null || !DemographicsAgree(member, criteria))
            return Array.Empty<ChoMember>();

        return new[] { member };
    }

    public async Task<IReadOnlyList<ChoPaymentDocument>> GetPaymentsAsync(
        string tenantId, string memberId, CancellationToken ct = default)
    {
        if (!ServesTenant(tenantId)) return Array.Empty<ChoPaymentDocument>();
        return await _provider.GetPaymentsByPatientIdAsync(memberId, ct);
    }

    private bool ServesTenant(string tenantId) =>
        string.Equals(tenantId, ServedTenantId, StringComparison.Ordinal);

    /// <summary>
    /// Any demographics the receiving payer supplied must match the matched
    /// member; a mismatch rejects the candidate so the member id alone can never
    /// return the wrong person's record.
    /// </summary>
    private static bool DemographicsAgree(ChoMember member, PayerToPayerMemberCriteria criteria)
    {
        if (!string.IsNullOrWhiteSpace(criteria.Dob)
            && !string.Equals(criteria.Dob.Trim(), member.Dob, StringComparison.Ordinal))
            return false;

        if (!string.IsNullOrWhiteSpace(criteria.LastName)
            && !string.Equals(criteria.LastName.Trim(), member.LastName, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(criteria.Gender)
            && !string.Equals(NormalizeGender(criteria.Gender), NormalizeGender(member.Gender), StringComparison.Ordinal))
            return false;

        return true;
    }

    private static string NormalizeGender(string? gender) => gender?.Trim().ToUpperInvariant() switch
    {
        "M" or "MALE" => "MALE",
        "F" or "FEMALE" => "FEMALE",
        "O" or "OTHER" => "OTHER",
        _ => "UNKNOWN",
    };
}
