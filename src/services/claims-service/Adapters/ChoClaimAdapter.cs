using ClaimsService.Models;
using ClaimsService.Repositories;

namespace ClaimsService.Adapters;

/// <summary>
/// Default claim adapter using CHO's internal <see cref="IClaimRepository"/>.
/// Preserves existing behavior — for the current set of tenants the factory
/// always resolves to this adapter and the read paths return the same rows
/// (post-hydration) the controller served before the refactor.
/// </summary>
/// <remarks>
/// Adjudication writes deliberately bypass the adapter (the adjudication
/// orchestrator in capability 5.5 calls <c>IClaimRepository.UpdateAdjudicationProjectionAsync</c>
/// directly — the projection-metadata bypass surface that 5.1a shipped).
/// Version-event emission is similarly off the adapter — the system-of-record
/// publisher (<c>IClaimVersionEventPublisher</c>) is wired by the submission
/// service in 5.3 and the adjudication orchestrator in 5.5; the adapter
/// itself stays simple.
/// </remarks>
public class ChoClaimAdapter : IClaimAdapter
{
    private readonly IClaimRepository _repository;
    private readonly ILogger<ChoClaimAdapter> _logger;

    public string Platform => "cho";

    public ChoClaimAdapter(
        IClaimRepository repository,
        ILogger<ChoClaimAdapter> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<ClaimAdapterResponse> GetClaimAsync(
        ClaimAdapterRequest request, CancellationToken ct = default)
    {
        // ClaimVersionId provided ⇒ resolve latest non-Draft version of the
        // chain in effect at AsOf (defaults to UtcNow). This is the path the
        // submission API (5.3) and adjustment workflow (5.12) wire onto when
        // they surface "the current claim" by chain key. Without
        // ClaimVersionId, fall back to per-document-id read so existing 22
        // controller endpoints continue to behave exactly as today.
        Claim? claim;
        if (!string.IsNullOrEmpty(request.ClaimVersionId))
        {
            claim = await _repository.GetLatestVersionAsync(
                request.ClaimVersionId, request.AsOf ?? DateTime.UtcNow);
        }
        else if (!string.IsNullOrEmpty(request.ClaimId))
        {
            claim = await _repository.GetByIdAsync(request.ClaimId);
        }
        else
        {
            throw new ArgumentException(
                "GetClaimAsync requires either ClaimId or ClaimVersionId.",
                nameof(request));
        }

        return new ClaimAdapterResponse
        {
            Platform = Platform,
            Claim = claim is null ? null : AdapterClaim.From(claim),
        };
    }

    public async Task<ClaimAdapterResponse> GetClaimByNumberAsync(
        ClaimAdapterRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(request.ClaimNumber))
        {
            throw new ArgumentException(
                "ClaimNumber is required for GetClaimByNumberAsync.",
                nameof(request));
        }

        var claim = await _repository.GetByClaimNumberAsync(request.ClaimNumber);
        return new ClaimAdapterResponse
        {
            Platform = Platform,
            Claim = claim is null ? null : AdapterClaim.From(claim),
        };
    }

    public async Task<ClaimAdapterResponse> GetClaimVersionAsync(
        ClaimAdapterRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(request.ClaimVersionId) ||
            string.IsNullOrEmpty(request.VersionId))
        {
            throw new ArgumentException(
                "GetClaimVersionAsync requires both ClaimVersionId and VersionId.",
                nameof(request));
        }

        var claim = await _repository.GetVersionAsync(
            request.ClaimVersionId, request.VersionId);

        return new ClaimAdapterResponse
        {
            Platform = Platform,
            Claim = claim is null ? null : AdapterClaim.From(claim),
        };
    }

    public async Task<ClaimVersionListAdapterResponse> ListClaimVersionsAsync(
        ClaimAdapterRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(request.ClaimVersionId))
        {
            throw new ArgumentException(
                "ClaimVersionId is required for ListClaimVersionsAsync.",
                nameof(request));
        }

        var (items, continuation) = await _repository.ListVersionsAsync(
            request.ClaimVersionId,
            request.PageSize,
            request.ContinuationToken);

        return new ClaimVersionListAdapterResponse
        {
            Platform = Platform,
            Versions = items.Select(AdapterClaim.From).ToList(),
            ContinuationToken = continuation,
        };
    }

    public async Task<ClaimAdapterResponse> SubmitClaimAsync(
        ClaimSubmissionAdapterRequest request, CancellationToken ct = default)
    {
        if (request.Claim is null)
        {
            throw new ArgumentException(
                "Claim is required for SubmitClaimAsync.", nameof(request));
        }

        // CreateAsync seeds the version chain on the way in
        // (ClaimVersionId=Id, VersionNumber=1, VersionState=Submitted) per
        // 5.1a. The adapter just maps DTO → domain → DTO and lets the repo
        // do the work; event emission is the submission service's concern
        // (capability 5.3), not the adapter's.
        var domain = request.Claim.ToClaim();
        var created = await _repository.CreateAsync(domain);

        _logger.LogDebug(
            "ChoClaimAdapter submitted claim {ClaimId} (chain {ClaimVersionId} v{VersionNumber}) for tenant {TenantId}",
            created.Id, created.ClaimVersionId, created.VersionNumber, request.TenantId);

        return new ClaimAdapterResponse
        {
            Platform = Platform,
            Claim = AdapterClaim.From(created),
        };
    }

    public async Task<ClaimSearchAdapterResponse> SearchClaimsAsync(
        ClaimSearchAdapterRequest request, CancellationToken ct = default)
    {
        var claims = await _repository.SearchAsync(
            memberId: request.MemberId,
            providerNPI: request.ProviderNPI,
            serviceDateFrom: request.ServiceDateFrom,
            serviceDateTo: request.ServiceDateTo,
            status: request.Status,
            lineOfBusiness: request.LineOfBusiness,
            page: request.Page,
            pageSize: request.PageSize);

        return new ClaimSearchAdapterResponse
        {
            Platform = Platform,
            Claims = claims.Select(AdapterClaim.From).ToList(),
        };
    }

    public async Task<ClaimSearchAdapterResponse> SearchClaimsForMemberAsync(
        ClaimMemberSearchAdapterRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(request.MemberId))
        {
            throw new ArgumentException(
                "MemberId is required for SearchClaimsForMemberAsync.",
                nameof(request));
        }

        var (page, totalCount) = await _repository.SearchForMemberAsync(
            memberId: request.MemberId,
            serviceDateFrom: request.ServiceDateFrom,
            serviceDateTo: request.ServiceDateTo,
            status: request.Status,
            providerNPI: request.ProviderNPI,
            claimType: request.ClaimType,
            amountMin: request.AmountMin,
            amountMax: request.AmountMax,
            page: request.Page,
            pageSize: request.PageSize);

        return new ClaimSearchAdapterResponse
        {
            Platform = Platform,
            Claims = page.Select(AdapterClaim.From).ToList(),
            TotalCount = totalCount,
        };
    }
}
