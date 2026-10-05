using System.Security.Cryptography;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using SmartAuthService.Models;
using SmartAuthService.Services;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace SmartAuthService.Controllers.Admin;

public sealed record ClientRegistrationRequest(
    string? ClientId,
    string? Kind,
    string? DisplayName,
    List<string>? RedirectUris,
    List<string>? Scopes,
    bool Confidential = false);

/// <summary>
/// SMART client registrations. The registering administrator's CHO token
/// decides the tenant the client belongs to; there is no tenant field. A
/// client_credentials token is issued for that tenant and no other, and an
/// interactive app issues tokens only to users mapped to that tenant.
/// </summary>
[ApiController]
[Route("api/admin/smart/clients")]
[RequirePermission("settings:manage")]
[HumanActorOnly]
public sealed class SmartClientRegistrationsController : ControllerBase
{
    private readonly ISmartIdentityStore _store;
    private readonly IOpenIddictApplicationManager _applications;
    private readonly ICurrentActor _actor;
    private readonly SmartAuthAudit _audit;
    private readonly IHostEnvironment _environment;

    public SmartClientRegistrationsController(
        ISmartIdentityStore store,
        IOpenIddictApplicationManager applications,
        ICurrentActor actor,
        SmartAuthAudit audit,
        IHostEnvironment environment)
    {
        _store = store;
        _applications = applications;
        _actor = actor;
        _audit = audit;
        _environment = environment;
    }

    [HttpPost]
    public async Task<IActionResult> Register([FromBody] ClientRegistrationRequest request, CancellationToken ct)
    {
        var kind = request.Kind ?? string.Empty;
        if (!SmartClientKind.All.Contains(kind))
            return BadRequest(new { error = $"kind must be one of: {string.Join(", ", SmartClientKind.All)}." });

        var clientId = string.IsNullOrWhiteSpace(request.ClientId)
            ? "cho-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()
            : request.ClientId.Trim();
        if (!SmartIdentifiers.IsClientId(clientId))
            return BadRequest(new { error = "clientId must be 3-64 lowercase letters, digits or hyphens." });

        var scopes = (request.Scopes ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).ToList();
        var scopeError = ValidateScopes(kind, scopes);
        if (scopeError != null) return BadRequest(new { error = scopeError });

        var redirectUris = new List<Uri>();
        if (kind != SmartClientKind.Backend)
        {
            if (request.RedirectUris is not { Count: > 0 })
                return BadRequest(new { error = "redirectUris is required for an interactive app." });
            foreach (var raw in request.RedirectUris)
            {
                if (!IsAcceptableRedirect(raw, out var uri))
                    return BadRequest(new { error = $"redirect URI '{raw}' must be absolute HTTPS without a fragment." });
                redirectUris.Add(uri);
            }
        }

        if (await _applications.FindByClientIdAsync(clientId, ct) is not null)
            return Conflict(new { error = "client_id_taken" });

        var confidential = kind == SmartClientKind.Backend || request.Confidential;
        var secret = confidential ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) : null;
        var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? clientId : request.DisplayName.Trim();

        // Binding first: a client id another tenant already holds is refused
        // before anything is created.
        var binding = await _store.CreateClientAsync(new ClientTenantRegistration
        {
            ClientId = clientId,
            TenantId = _actor.TenantId,
            Kind = kind,
            DisplayName = displayName,
            CreatedBy = _actor.UserId,
            CreatedAt = DateTimeOffset.UtcNow,
        }, ct);
        if (binding == BindingWriteResult.AlreadyBound)
            return Conflict(new { error = "client_id_taken" });

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientSecret = secret,
            ClientType = confidential ? ClientTypes.Confidential : ClientTypes.Public,
            DisplayName = displayName,
        };
        foreach (var uri in redirectUris) descriptor.RedirectUris.Add(uri);

        descriptor.Permissions.Add(Permissions.Endpoints.Token);
        if (kind == SmartClientKind.Backend)
        {
            descriptor.Permissions.Add(Permissions.GrantTypes.ClientCredentials);
        }
        else
        {
            descriptor.Permissions.Add(Permissions.Endpoints.Authorization);
            descriptor.Permissions.Add(Permissions.Endpoints.EndSession);
            descriptor.Permissions.Add(Permissions.GrantTypes.AuthorizationCode);
            descriptor.Permissions.Add(Permissions.GrantTypes.RefreshToken);
            descriptor.Permissions.Add(Permissions.ResponseTypes.Code);
            descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);
        }
        foreach (var scope in scopes)
            descriptor.Permissions.Add(Permissions.Prefixes.Scope + scope);

        try
        {
            await _applications.CreateAsync(descriptor, ct);
        }
        catch
        {
            await _store.DeleteClientAsync(_actor.TenantId, clientId, CancellationToken.None);
            throw;
        }

        _audit.Admin("client-registered", _actor.TenantId, _actor.UserId, $"client={clientId} kind={kind}");
        return StatusCode(201, new
        {
            clientId,
            kind,
            tenantId = _actor.TenantId,
            displayName,
            clientSecret = secret,
            redirectUris = redirectUris.Select(u => u.ToString()),
            scopes,
        });
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
        => Ok(await _store.ListClientsAsync(_actor.TenantId, ct));

    [HttpGet("{clientId}")]
    public async Task<IActionResult> Get(string clientId, CancellationToken ct)
    {
        var binding = await _store.FindClientAsync(clientId, ct);
        return binding != null && binding.TenantId == _actor.TenantId ? Ok(binding) : NotFound();
    }

    [HttpDelete("{clientId}")]
    public async Task<IActionResult> Delete(string clientId, CancellationToken ct)
    {
        var binding = await _store.FindClientAsync(clientId, ct);
        if (binding == null || binding.TenantId != _actor.TenantId)
            return NotFound();

        if (await _applications.FindByClientIdAsync(clientId, ct) is { } application)
            await _applications.DeleteAsync(application, ct);
        await _store.DeleteClientAsync(_actor.TenantId, clientId, ct);

        _audit.Admin("client-deleted", _actor.TenantId, _actor.UserId, $"client={clientId}");
        return NoContent();
    }

    // ── App approvals (consent) of this tenant's clients ───────────────────

    /// <summary>GET {clientId}/approvals[?subject=] — the valid approvals of one of this tenant's clients.</summary>
    [HttpGet("{clientId}/approvals")]
    public async Task<IActionResult> ListApprovals(
        string clientId, [FromQuery] string? subject, [FromServices] SmartAppApprovals approvals, CancellationToken ct)
    {
        if (!await IsOwnClientAsync(clientId, ct))
            return NotFound();
        return Ok(await approvals.ForClientAsync(clientId, subject, ct));
    }

    /// <summary>
    /// DELETE {clientId}/approvals/{approvalId} — withdraws one approval of one
    /// of this tenant's clients, and every token issued under it.
    /// </summary>
    [HttpDelete("{clientId}/approvals/{approvalId}")]
    public async Task<IActionResult> RevokeApproval(
        string clientId, string approvalId, [FromServices] SmartAppApprovals approvals, CancellationToken ct)
    {
        if (!await IsOwnClientAsync(clientId, ct))
            return NotFound();
        var approval = await approvals.FindAsync(approvalId, ct);
        if (approval is null || approval.ClientId != clientId || !await approvals.RevokeAsync(approvalId, ct))
            return NotFound();

        _audit.ConsentRevoked(approval.Subject, clientId, "admin", _actor.UserId);
        _audit.Admin("consent-revoked", _actor.TenantId, _actor.UserId, $"client={clientId} approval={approvalId}");
        return NoContent();
    }

    /// <summary>
    /// DELETE {clientId}/approvals[?subject=] — withdraws every approval of one
    /// of this tenant's clients (of one person, with <c>subject</c>).
    /// </summary>
    [HttpDelete("{clientId}/approvals")]
    public async Task<IActionResult> RevokeApprovals(
        string clientId, [FromQuery] string? subject, [FromServices] SmartAppApprovals approvals, CancellationToken ct)
    {
        if (!await IsOwnClientAsync(clientId, ct))
            return NotFound();
        var revoked = 0;
        foreach (var approval in await approvals.ForClientAsync(clientId, subject, ct))
        {
            if (!await approvals.RevokeAsync(approval.Id, ct)) continue;
            revoked++;
            _audit.ConsentRevoked(approval.Subject, clientId, "admin", _actor.UserId);
        }
        _audit.Admin("consent-revoked", _actor.TenantId, _actor.UserId, $"client={clientId} count={revoked}");
        return Ok(new { revoked });
    }

    private async Task<bool> IsOwnClientAsync(string clientId, CancellationToken ct)
        => await _store.FindClientAsync(clientId, ct) is { } binding && binding.TenantId == _actor.TenantId;

    private static string? ValidateScopes(string kind, IReadOnlyList<string> scopes)
    {
        if (scopes.Count == 0) return "scopes is required.";
        foreach (var scope in scopes)
        {
            if (!SmartScopes.Registered.Contains(scope))
                return $"Unknown scope '{scope}'.";

            var allowed = kind switch
            {
                SmartClientKind.Backend => scope.StartsWith("system/", StringComparison.Ordinal),
                SmartClientKind.PatientApp => scope is Scopes.OpenId or SmartScopes.OfflineAccess or SmartScopes.FhirUser or SmartScopes.LaunchPatient
                                              || scope.StartsWith("patient/", StringComparison.Ordinal),
                SmartClientKind.ProviderApp => scope is Scopes.OpenId or SmartScopes.OfflineAccess or SmartScopes.FhirUser or SmartScopes.Launch
                                                   or SmartScopes.LaunchPatient or SmartScopes.LaunchEncounter
                                               || scope.StartsWith("user/", StringComparison.Ordinal)
                                               || scope.StartsWith("patient/", StringComparison.Ordinal),
                _ => false,
            };
            if (!allowed) return $"Scope '{scope}' is not available to a {kind} client.";
        }
        return null;
    }

    private bool IsAcceptableRedirect(string? raw, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var parsed) || !string.IsNullOrEmpty(parsed.Fragment))
            return false;
        uri = parsed;
        if (parsed.Scheme == Uri.UriSchemeHttps) return true;
        return _environment.IsDevelopment() && parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback;
    }
}
