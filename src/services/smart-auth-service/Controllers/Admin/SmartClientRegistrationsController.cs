using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
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
    bool Confidential = false,
    string? PayerId = null,
    JsonElement? Jwks = null,
    string? JwksUri = null);

/// <summary>Body of <c>PUT {clientId}/payer</c>.</summary>
public sealed record ClientPayerRequest(string? PayerId);

/// <summary>
/// Body of <c>PUT {clientId}/keys</c>: exactly one of a JWK Set or a
/// <c>jwks_uri</c>.
/// </summary>
public sealed record ClientKeysRequest(JsonElement? Jwks, string? JwksUri);

/// <summary>
/// SMART client registrations. The registering administrator's CHO token
/// decides the tenant the client belongs to; there is no tenant field. A
/// client_credentials token is issued for that tenant and no other, and an
/// interactive app issues tokens only to users mapped to that tenant.
///
/// A backend client authenticates either with a client secret (issued once,
/// in the registration response) or, when it is registered with a JWK Set
/// (<c>jwks</c>) or a <c>jwks_uri</c>, with a signed JWT assertion
/// (<c>private_key_jwt</c>, SMART Backend Services; G-42). An asymmetric
/// client is never given a secret.
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

        var payerId = string.IsNullOrWhiteSpace(request.PayerId) ? null : request.PayerId.Trim();
        var payerError = ValidatePayer(kind, payerId, grantsPayerToPayer: scopes.Contains(SmartScopes.SystemPayerToPayerRead));
        if (payerError != null) return BadRequest(new { error = payerError });

        var keys = await ResolveKeysAsync(kind, request.Jwks, request.JwksUri, required: false, ct);
        if (keys.Error != null) return BadRequest(new { error = keys.Error });
        var asymmetric = keys.Set != null;

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
        var secret = confidential && !asymmetric ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) : null;
        var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? clientId : request.DisplayName.Trim();

        // Binding first: a client id another tenant already holds is refused
        // before anything is created.
        var binding = await _store.CreateClientAsync(new ClientTenantRegistration
        {
            ClientId = clientId,
            TenantId = _actor.TenantId,
            Kind = kind,
            DisplayName = displayName,
            PayerId = payerId,
            JwksUri = keys.Uri?.AbsoluteUri,
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
            JsonWebKeySet = keys.Set,
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

        var authMethod = TokenEndpointAuthMethod(confidential, asymmetric);
        _audit.Admin("client-registered", _actor.TenantId, _actor.UserId,
            $"client={clientId} kind={kind} auth={authMethod ?? "none"}" + (payerId is null ? "" : $" payer={payerId}"));
        return StatusCode(201, new
        {
            clientId,
            kind,
            tenantId = _actor.TenantId,
            displayName,
            clientSecret = secret,
            tokenEndpointAuthMethod = authMethod,
            jwksUri = keys.Uri?.AbsoluteUri,
            redirectUris = redirectUris.Select(u => u.ToString()),
            scopes,
            payerId,
        });
    }

    /// <summary>
    /// PUT {clientId}/payer — sets the payer one of this tenant's backend
    /// clients acts for in Payer-to-Payer exchange (D-13). A client granted
    /// <c>system/PayerToPayer.read</c> must keep one, so it cannot be cleared
    /// on such a client. The change applies to tokens issued from now on;
    /// access tokens already issued keep the payer they were minted with
    /// until they expire.
    /// </summary>
    [HttpPut("{clientId}/payer")]
    public async Task<IActionResult> SetPayer(string clientId, [FromBody] ClientPayerRequest request, CancellationToken ct)
    {
        var binding = await _store.FindClientAsync(clientId, ct);
        if (binding == null || binding.TenantId != _actor.TenantId)
            return NotFound();

        var payerId = string.IsNullOrWhiteSpace(request.PayerId) ? null : request.PayerId.Trim();
        var application = await _applications.FindByClientIdAsync(clientId, ct);
        var grantsPayerToPayer = application is not null
            && await _applications.HasPermissionAsync(application, Permissions.Prefixes.Scope + SmartScopes.SystemPayerToPayerRead, ct);
        var payerError = ValidatePayer(binding.Kind, payerId, grantsPayerToPayer);
        if (payerError != null) return BadRequest(new { error = payerError });

        if (!await _store.SetClientPayerIdAsync(_actor.TenantId, clientId, payerId, ct))
            return NotFound();

        _audit.Admin("client-payer-set", _actor.TenantId, _actor.UserId,
            $"client={clientId} previousPayer={binding.PayerId ?? "(none)"} payer={payerId ?? "(none)"}");
        return Ok(await _store.FindClientAsync(clientId, ct));
    }

    /// <summary>
    /// PUT {clientId}/keys — replaces the keys one of this tenant's
    /// <c>private_key_jwt</c> backend clients authenticates with: a new JWK
    /// Set, or a (new) <c>jwks_uri</c>. Assertions signed with a key no longer
    /// registered are refused from now on; access tokens already issued run to
    /// expiry. A secret client is not converted here: register a new client.
    /// </summary>
    [HttpPut("{clientId}/keys")]
    public async Task<IActionResult> SetKeys(string clientId, [FromBody] ClientKeysRequest request, CancellationToken ct)
    {
        var binding = await _store.FindClientAsync(clientId, ct);
        if (binding == null || binding.TenantId != _actor.TenantId)
            return NotFound();
        var application = await _applications.FindByClientIdAsync(clientId, ct);
        if (application is null)
            return NotFound();
        if (binding.Kind != SmartClientKind.Backend
            || await _applications.GetJsonWebKeySetAsync(application, ct) is not { Keys.Count: > 0 })
            return BadRequest(new { error = "Only a backend client registered with jwks or jwksUri has keys to replace." });

        var keys = await ResolveKeysAsync(binding.Kind, request.Jwks, request.JwksUri, required: true, ct);
        if (keys.Error != null) return BadRequest(new { error = keys.Error });

        var descriptor = new OpenIddictApplicationDescriptor();
        await _applications.PopulateAsync(descriptor, application, ct);
        descriptor.JsonWebKeySet = keys.Set;
        await _applications.UpdateAsync(application, descriptor, ct);
        await _store.SetClientJwksUriAsync(_actor.TenantId, clientId, keys.Uri?.AbsoluteUri, ct);

        _audit.Admin("client-keys-set", _actor.TenantId, _actor.UserId,
            $"client={clientId} source={(keys.Uri is null ? "jwks" : "jwks_uri")} kids={string.Join(',', keys.Set!.Keys.Select(k => k.Kid))}");
        return Ok(new
        {
            clientId,
            tokenEndpointAuthMethod = ClientAuthenticationMethods.PrivateKeyJwt,
            jwksUri = keys.Uri?.AbsoluteUri,
            kids = keys.Set!.Keys.Select(k => k.Kid),
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

    private sealed record ResolvedKeys(JsonWebKeySet? Set, Uri? Uri, string? Error);

    /// <summary>
    /// The keys of an asymmetric (<c>private_key_jwt</c>) backend client:
    /// an inline JWK Set, or the set its <c>jwks_uri</c> serves now (fetched
    /// so a wrong or unreachable URI is refused at registration, and so the
    /// OpenIddict application holds a key). Neither, when not
    /// <paramref name="required"/>, means a secret client.
    /// </summary>
    private async Task<ResolvedKeys> ResolveKeysAsync(
        string kind, JsonElement? jwks, string? jwksUri, bool required, CancellationToken ct)
    {
        var hasJwks = jwks is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) };
        var hasUri = !string.IsNullOrWhiteSpace(jwksUri);
        if (!hasJwks && !hasUri)
            return new(null, null, required ? "Provide jwks or jwksUri." : null);
        if (kind != SmartClientKind.Backend)
            return new(null, null, "jwks and jwksUri are available to backend clients only.");
        if (hasJwks && hasUri)
            return new(null, null, "Provide jwks or jwksUri, not both.");

        if (hasJwks)
        {
            var (set, error) = SmartClientJwks.Parse(jwks!.Value.GetRawText());
            return new(set, null, error);
        }

        if (!SmartClientJwks.IsAcceptableJwksUri(jwksUri, out var uri))
            return new(null, null, "jwksUri must be an absolute HTTPS URL without user info or fragment.");
        var fetcher = HttpContext.RequestServices.GetRequiredService<SmartJwksFetcher>();
        var (fetched, fetchError) = await fetcher.FetchAsync(uri, ct);
        return fetched is null
            ? new(null, null, $"jwksUri: {fetchError}")
            : new(fetched, uri, null);
    }

    private static string? TokenEndpointAuthMethod(bool confidential, bool asymmetric)
        => !confidential ? null
            : asymmetric ? ClientAuthenticationMethods.PrivateKeyJwt
            : ClientAuthenticationMethods.ClientSecretBasic;

    /// <summary>
    /// A payer id belongs to backend clients only, must be well formed, and is
    /// required for a client granted <c>system/PayerToPayer.read</c>: that
    /// scope is never granted to a backend client that does not say which
    /// payer it is.
    /// </summary>
    private static string? ValidatePayer(string kind, string? payerId, bool grantsPayerToPayer)
    {
        if (payerId != null && kind != SmartClientKind.Backend)
            return "payerId is available to backend clients only.";
        if (payerId != null && !SmartIdentifiers.IsPayerId(payerId))
            return "payerId must be 1-64 letters, digits, '.', '-' or '_', starting with a letter or digit.";
        if (payerId == null && grantsPayerToPayer)
            return $"payerId is required for a client granted {SmartScopes.SystemPayerToPayerRead}.";
        return null;
    }

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
