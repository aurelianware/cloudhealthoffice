using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace SmartAuthService.Services;

/// <summary>One app approval (consent): a valid permanent OpenIddict authorization.</summary>
public sealed record SmartAppApproval(
    string Id, string ClientId, string DisplayName, string Subject, IReadOnlyList<string> Scopes, DateTimeOffset? CreatedAt);

/// <summary>
/// The app approvals the consent page creates (<see cref="SmartConsent"/>):
/// permanent OpenIddict authorizations for (identity, client, scopes). Lists
/// them for the person (<c>/account/apps</c>) and for a tenant administrator
/// (per client), and revokes them: the authorization and every token issued
/// under it, so the app's refresh tokens stop working at once.
///
/// Only a valid permanent authorization is an approval. Authorization codes
/// and refresh tokens are honoured only when they rest on one
/// (<see cref="IsApprovalAsync"/>); a refresh token issued before consent
/// existed rests on an ad-hoc authorization and is refused.
/// </summary>
public sealed class SmartAppApprovals
{
    private readonly IOpenIddictAuthorizationManager _authorizations;
    private readonly IOpenIddictApplicationManager _applications;
    private readonly IOpenIddictTokenManager _tokens;

    public SmartAppApprovals(
        IOpenIddictAuthorizationManager authorizations,
        IOpenIddictApplicationManager applications,
        IOpenIddictTokenManager tokens)
    {
        _authorizations = authorizations;
        _applications = applications;
        _tokens = tokens;
    }

    /// <summary>Whether <paramref name="authorizationId"/> names a valid approval.</summary>
    public async Task<bool> IsApprovalAsync(string? authorizationId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(authorizationId))
            return false;
        var authorization = await _authorizations.FindByIdAsync(authorizationId, ct);
        return authorization is not null && await IsValidPermanentAsync(authorization, ct);
    }

    /// <summary>The person's approvals, newest first.</summary>
    public async Task<List<SmartAppApproval>> ForSubjectAsync(string subject, CancellationToken ct)
    {
        var result = new List<SmartAppApproval>();
        await foreach (var authorization in _authorizations.FindBySubjectAsync(subject, ct))
        {
            if (await DescribeAsync(authorization, ct) is { } approval)
                result.Add(approval);
        }
        return result.OrderByDescending(a => a.CreatedAt).ToList();
    }

    /// <summary>The approvals of one client (optionally of one subject), newest first. Empty for an unknown client.</summary>
    public async Task<List<SmartAppApproval>> ForClientAsync(string clientId, string? subject, CancellationToken ct)
    {
        var result = new List<SmartAppApproval>();
        var application = await _applications.FindByClientIdAsync(clientId, ct);
        if (application is null)
            return result;
        var applicationId = await _applications.GetIdAsync(application, ct);
        await foreach (var authorization in _authorizations.FindByApplicationIdAsync(applicationId!, ct))
        {
            if (await DescribeAsync(authorization, ct) is { } approval
                && (subject is null || string.Equals(approval.Subject, subject, StringComparison.Ordinal)))
                result.Add(approval);
        }
        return result.OrderByDescending(a => a.CreatedAt).ToList();
    }

    /// <summary>The approval with this id, or null when there is none (or it is no longer valid).</summary>
    public async Task<SmartAppApproval?> FindAsync(string id, CancellationToken ct)
    {
        var authorization = await _authorizations.FindByIdAsync(id, ct);
        return authorization is null ? null : await DescribeAsync(authorization, ct);
    }

    /// <summary>
    /// Revokes the approval and every token issued under it. False when it is
    /// not (or no longer) a valid approval.
    /// </summary>
    public async Task<bool> RevokeAsync(string id, CancellationToken ct)
    {
        var authorization = await _authorizations.FindByIdAsync(id, ct);
        if (authorization is null || !await IsValidPermanentAsync(authorization, ct))
            return false;
        if (!await _authorizations.TryRevokeAsync(authorization, ct))
            return false;
        await _tokens.RevokeByAuthorizationIdAsync(id, ct);
        return true;
    }

    private async Task<bool> IsValidPermanentAsync(object authorization, CancellationToken ct)
        => await _authorizations.HasTypeAsync(authorization, AuthorizationTypes.Permanent, ct)
           && await _authorizations.HasStatusAsync(authorization, Statuses.Valid, ct);

    private async Task<SmartAppApproval?> DescribeAsync(object authorization, CancellationToken ct)
    {
        if (!await IsValidPermanentAsync(authorization, ct))
            return null;
        var applicationId = await _authorizations.GetApplicationIdAsync(authorization, ct);
        var application = applicationId is null ? null : await _applications.FindByIdAsync(applicationId, ct);
        var clientId = application is null ? null : await _applications.GetClientIdAsync(application, ct);
        if (clientId is null)
            return null;
        var displayName = await _applications.GetDisplayNameAsync(application!, ct) ?? clientId;
        return new SmartAppApproval(
            (await _authorizations.GetIdAsync(authorization, ct))!,
            clientId,
            displayName,
            await _authorizations.GetSubjectAsync(authorization, ct) ?? string.Empty,
            (await _authorizations.GetScopesAsync(authorization, ct)).ToList(),
            await _authorizations.GetCreationDateAsync(authorization, ct));
    }
}
