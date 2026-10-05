using System.ComponentModel.DataAnnotations;
using CloudHealthOffice.Infrastructure.Security;
using TenantService.Models;
using TenantService.Security;

namespace TenantService.Services;

/// <summary>A refused invitation request, with the HTTP status and error code to answer with.</summary>
public sealed class InvitationRequestException(int status, string error, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Error { get; } = error;
}

/// <summary>
/// Tenant administrators' invitation operations. The code is generated here,
/// returned once and only its hash is handed to the store.
/// </summary>
public sealed class InvitationService
{
    private readonly IInvitationStore _store;
    private readonly ITenantRepository _tenants;
    private readonly ITenantRoleRepository _roles;
    private readonly ICurrentActor _actor;
    private readonly InvitationOptions _options;
    private readonly TenantAuditLog _audit;
    private readonly TimeProvider _time;

    public InvitationService(
        IInvitationStore store,
        ITenantRepository tenants,
        ITenantRoleRepository roles,
        ICurrentActor actor,
        InvitationOptions options,
        TenantAuditLog audit,
        TimeProvider? time = null)
    {
        _store = store;
        _tenants = tenants;
        _roles = roles;
        _actor = actor;
        _options = options;
        _audit = audit;
        _time = time ?? TimeProvider.System;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public async Task<IssuedInvitationResponse> CreateAsync(string tenantId, CreateInvitationRequest request, CancellationToken ct)
    {
        var email = (request.Email ?? string.Empty).Trim();
        if (email.Length == 0 || email.Length > 320 || !new EmailAddressAttribute().IsValid(email))
            throw new InvitationRequestException(StatusCodes.Status400BadRequest, "invalid_email", "A valid email address is required.");

        var roles = (request.Roles ?? new List<string>())
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (roles.Count == 0)
            throw new InvitationRequestException(StatusCodes.Status400BadRequest, "roles_required", "At least one role is required.");

        // An invitation never hands out cross-tenant or service power, whoever sends it.
        if (roles.Any(r => TenantPermissions.PlatformOnlyRoles.Contains(r)))
        {
            _audit.Record("invitation refused: platform-only role", tenantId, "refused");
            throw new InvitationRequestException(StatusCodes.Status403Forbidden, "role_not_allowed",
                "PlatformAdmin and cho.service cannot be granted by an invitation.");
        }

        var catalogue = (await _roles.GetAllAsync()).Select(r => r.RoleName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = roles.Where(r => !catalogue.Contains(r)).ToList();
        if (unknown.Count > 0)
            throw new InvitationRequestException(StatusCodes.Status400BadRequest, "invalid_roles",
                $"Invalid roles: {string.Join(", ", unknown)}");

        if (await _tenants.GetByTenantIdAsync(tenantId) == null)
            throw new InvitationRequestException(StatusCodes.Status404NotFound, "tenant_not_found", $"Tenant {tenantId} not found");

        var first = (request.FirstName ?? string.Empty).Trim();
        var last = (request.LastName ?? string.Empty).Trim();
        var display = (request.DisplayName ?? string.Empty).Trim();
        if (display.Length == 0)
            display = $"{first} {last}".Trim() is { Length: > 0 } full ? full : email;

        var code = InvitationCodes.NewCode();
        var invitation = new Invitation
        {
            TenantId = tenantId,
            Email = email,
            EmailNormalized = InvitationCodes.NormalizeEmail(email),
            DisplayName = display,
            FirstName = first,
            LastName = last,
            Department = (request.Department ?? string.Empty).Trim(),
            Roles = roles,
            CodeHash = InvitationCodes.Hash(code),
            CreatedBy = _actor.UserId,
            ExpiresAt = Now.Add(_options.Lifetime),
        };

        var result = await _store.CreateAsync(invitation, ct);
        switch (result.Error)
        {
            case InvitationError.None:
                break;
            case InvitationError.UserExists:
                throw new InvitationRequestException(StatusCodes.Status409Conflict, "user_exists",
                    "This address already belongs to an active or linked user in this tenant.");
            case InvitationError.InvitationPending:
                throw new InvitationRequestException(StatusCodes.Status409Conflict, "invitation_pending",
                    "This address already has a pending invitation. Resend it instead.");
            default:
                throw new InvitationRequestException(StatusCodes.Status409Conflict, "conflict",
                    "The user changed while the invitation was being created. Try again.");
        }

        var created = result.Invitation!;
        _audit.Record($"invitation created {created.Id} for user {created.UserId} roles {string.Join(",", roles)}", tenantId);
        return Issued(created, code);
    }

    public async Task<IReadOnlyList<InvitationView>> ListAsync(string tenantId, CancellationToken ct)
    {
        var now = Now;
        return (await _store.ListAsync(tenantId, ct)).Select(i => InvitationView.From(i, now)).ToList();
    }

    public async Task<IssuedInvitationResponse> ResendAsync(string tenantId, string invitationId, CancellationToken ct)
    {
        var code = InvitationCodes.NewCode();
        var result = await _store.ResendAsync(tenantId, invitationId, InvitationCodes.Hash(code),
            Now.Add(_options.Lifetime), _actor.UserId, ct);
        ThrowIfFailed(result);
        _audit.Record($"invitation resent {invitationId}", tenantId);
        return Issued(result.Invitation!, code);
    }

    public async Task<InvitationView> RevokeAsync(string tenantId, string invitationId, CancellationToken ct)
    {
        var result = await _store.RevokeAsync(tenantId, invitationId, _actor.UserId, ct);
        ThrowIfFailed(result);
        _audit.Record($"invitation revoked {invitationId}", tenantId);
        return InvitationView.From(result.Invitation!, Now);
    }

    private IssuedInvitationResponse Issued(Invitation invitation, string code) => new()
    {
        Invitation = InvitationView.From(invitation, Now),
        Code = code,
        RedemptionUrl = _options.RedemptionUrl(code),
    };

    private static void ThrowIfFailed(InvitationResult result)
    {
        switch (result.Error)
        {
            case InvitationError.None:
                return;
            case InvitationError.NotFound:
                throw new InvitationRequestException(StatusCodes.Status404NotFound, "not_found", "Invitation not found.");
            case InvitationError.AlreadyRedeemed:
                throw new InvitationRequestException(StatusCodes.Status409Conflict, "already_redeemed",
                    "The invitation has already been redeemed.");
            case InvitationError.Revoked:
                throw new InvitationRequestException(StatusCodes.Status409Conflict, "revoked",
                    "The invitation was revoked. Invite the person again.");
            default:
                throw new InvitationRequestException(StatusCodes.Status409Conflict, "conflict",
                    "The invitation or its user changed. Reload and try again.");
        }
    }
}
