using PersonalRepresentativeService.Middleware;
using PersonalRepresentativeService.Models;
using PersonalRepresentativeService.Repositories;
using PersonalRepresentativeService.Services;
using Microsoft.AspNetCore.Mvc;

// TODO(consent-integration-followup): consent-service will call this
// controller's resolver endpoint to resolve GrantedBy → structured
// PersonalRep reference. Not wired in this PR. See consent-service PR #674.
// When wiring lands, the resolver endpoint should require the
// `consents:read` or a dedicated `representatives:resolve` scope;
// authorization-service integration belongs in the consent wiring PR.

namespace PersonalRepresentativeService.Controllers;

/// <summary>
/// Member-centric views of Personal Representatives. Separated from
/// <see cref="PersonalRepresentativesController"/> because these endpoints
/// live under a different route prefix (<c>/api/v1/members/{memberId}/…</c>)
/// and serve a different access pattern — this is what consent-service
/// will call to resolve its <c>GrantedBy</c> field.
/// </summary>
[ApiController]
[Route("api/v1/members/{memberId}/personal-representatives")]
public class MemberRepresentativesController : ControllerBase
{
    private string TenantId => HttpContext.GetTenantId();

    private readonly IPersonalRepRepository _reps;
    private readonly IPersonalRepFieldEncryptor _encryptor;

    public MemberRepresentativesController(
        IPersonalRepRepository reps,
        IPersonalRepFieldEncryptor encryptor)
    {
        _reps = reps;
        _encryptor = encryptor;
    }

    [HttpGet]
    [ProducesResponseType(typeof(MemberRepresentativesResponse), 200)]
    public async Task<IActionResult> ListAll(
        [FromRoute] string memberId,
        [FromQuery] DateTime? asOf = null,
        CancellationToken ct = default)
    {
        var associations = await _reps.ListAssociationsForMemberAsync(
            TenantId, memberId, activeOnly: false, asOf: asOf, ct: ct);

        var items = await BuildSummariesAsync(associations, asOf, ct);
        return Ok(new MemberRepresentativesResponse
        {
            Items = items,
            AsOf = asOf ?? DateTime.UtcNow
        });
    }

    /// <summary>
    /// Returns all ACTIVE representatives for the member as of
    /// <paramref name="asOf"/>, optionally filtered by
    /// <paramref name="credentialType"/>. Callers filter by credential
    /// type to narrow to reps with healthcare-decision authority (Parent,
    /// LegalGuardian, HealthcarePowerOfAttorney, HealthcareSurrogate).
    /// </summary>
    /// <remarks>
    /// TODO(authority-scope-followup): finer-grained authority-scope
    /// filtering — "which reps can grant a §164.508 authorization" vs
    /// "which reps can receive PHI disclosures" — lands with the
    /// authority-scope feature. Today, credential type is a proxy:
    /// callers decide from the credential type what authorities it
    /// implies. When authority scopes become first-class, this endpoint
    /// will grow a <c>scope=</c> query parameter without breaking the
    /// response shape. Clients should NOT rely on credential type alone
    /// for finer-grained authority decisions indefinitely.
    ///
    /// Response is a lightweight summary (no mailing address, no phone,
    /// no notes). Consumers that need the full decrypted record should
    /// call <c>GET /api/v1/personal-representatives/{repId}</c>. Keeps
    /// the resolver endpoint from over-disclosing PHI.
    /// </remarks>
    [HttpGet("active")]
    [ProducesResponseType(typeof(MemberRepresentativesResponse), 200)]
    public async Task<IActionResult> ListActive(
        [FromRoute] string memberId,
        [FromQuery] DateTime? asOf = null,
        [FromQuery(Name = "credentialType")] List<PersonalRepCredentialType>? credentialTypes = null,
        CancellationToken ct = default)
    {
        var associations = await _reps.ListAssociationsForMemberAsync(
            TenantId, memberId, activeOnly: true, asOf: asOf, ct: ct);

        if (credentialTypes is { Count: > 0 })
        {
            var allow = new HashSet<PersonalRepCredentialType>(credentialTypes);
            associations = associations.Where(a => allow.Contains(a.CredentialType)).ToList();
        }

        var items = await BuildSummariesAsync(associations, asOf, ct);
        // Only include reps whose ObservedStatus is Active at asOf. The
        // association may still be "active" while the parent rep has been
        // revoked or expired — in either case it should drop out.
        items = items.Where(i => i.Status == PersonalRepStatus.Active).ToList();

        return Ok(new MemberRepresentativesResponse
        {
            Items = items,
            AsOf = asOf ?? DateTime.UtcNow
        });
    }

    private async Task<List<PersonalRepSummary>> BuildSummariesAsync(
        IReadOnlyList<PersonalRepAssociation> associations,
        DateTime? asOf,
        CancellationToken ct)
    {
        var t = asOf ?? DateTime.UtcNow;
        var summaries = new List<PersonalRepSummary>(associations.Count);

        // Batch-fetch all reps in one round-trip instead of N individual reads.
        var repIds = associations.Select(a => a.RepId).Distinct().ToList();
        var repMap = (await _reps.GetByIdsAsync(TenantId, repIds, ct))
            .ToDictionary(r => r.Id);

        foreach (var a in associations)
        {
            if (!repMap.TryGetValue(a.RepId, out var rep)) continue;

            // Uses the same IPersonalRepFieldEncryptor.DecryptAsync call
            // the primary controller uses — no partial-decrypt shortcut.
            // RotatingKeyProvider cache amortizes the Key Vault cost.
            var first = await _encryptor.DecryptAsync(rep.FirstName, ct);
            var last = await _encryptor.DecryptAsync(rep.LastName, ct);
            var displayName = $"{first} {last}".Trim();

            summaries.Add(new PersonalRepSummary
            {
                PersonalRepId = rep.Id,
                CredentialType = a.CredentialType,
                Status = rep.ObservedStatus(t),
                EffectiveFrom = a.EffectiveFrom,
                EffectiveTo = a.EffectiveTo,
                ExpiresAt = rep.ExpiresAt,
                DisplayName = displayName,
                ProofOfAuthorityDocumentId = rep.ProofOfAuthorityDocumentId
            });
        }
        return summaries;
    }
}

public class MemberRepresentativesResponse
{
    public List<PersonalRepSummary> Items { get; set; } = new();
    public DateTime AsOf { get; set; }
}

public class PersonalRepSummary
{
    public string PersonalRepId { get; set; } = string.Empty;
    public PersonalRepCredentialType CredentialType { get; set; }
    public PersonalRepStatus Status { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? ProofOfAuthorityDocumentId { get; set; }
}
