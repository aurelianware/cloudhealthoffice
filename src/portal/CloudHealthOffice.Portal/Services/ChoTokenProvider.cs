using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json.Serialization;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web;

namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// Supplies the CHO access token the portal sends to CHO backend services for
/// the signed-in user, by exchanging the user's Entra token at the CHO token
/// service. Scoped: one instance per Blazor circuit (or per HTTP request).
/// </summary>
public interface IChoTokenProvider
{
    /// <summary>The CHO tenant the current token acts in, once one has been obtained.</summary>
    string? CurrentTenantId { get; }

    /// <summary>A CHO token for the current tenant (cached until shortly before it expires).</summary>
    Task<ChoTokenResult> GetTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Drop the cached token, but only if it is still <paramref name="rejected"/>, so
    /// a concurrent refresh is not thrown away.
    /// </summary>
    void Invalidate(ChoTokenExchangeResponse rejected);

    /// <summary>The tenants the token service lists for the user, or null when it cannot be reached.</summary>
    Task<IReadOnlyList<ChoTenantInfo>?> GetTenantsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Exchange for <paramref name="tenantId"/> and make it current. Refused without
    /// calling the exchange when the token service did not list that tenant.
    /// </summary>
    Task<ChoTokenResult> SwitchTenantAsync(string tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Redeem an invitation code for the signed-in user at the token service
    /// (with the same Entra token as the exchange). On success the returned CHO
    /// token's tenant becomes the current tenant. The code is never logged.
    /// </summary>
    Task<ChoInvitationResult> RedeemInvitationAsync(string code, CancellationToken cancellationToken = default);
}

/// <summary>Sends the user to Entra again (re-sign-in or incremental consent).</summary>
public interface IChoReauthenticationHandler
{
    void Challenge(MicrosoftIdentityWebChallengeUserException exception, ClaimsPrincipal user);
}

/// <summary>
/// Uses the handler registered by <c>AddMicrosoftIdentityConsentHandler()</c>, which in
/// Blazor Server navigates (force-load) to <c>/MicrosoftIdentity/Account/Challenge</c>
/// with the scopes and claims Entra asked for, then returns to the current page.
/// </summary>
public sealed class MicrosoftIdentityReauthenticationHandler : IChoReauthenticationHandler
{
    private readonly IServiceProvider _services;
    private readonly ILogger<MicrosoftIdentityReauthenticationHandler> _logger;

    public MicrosoftIdentityReauthenticationHandler(
        IServiceProvider services, ILogger<MicrosoftIdentityReauthenticationHandler> logger)
    {
        _services = services;
        _logger = logger;
    }

    public void Challenge(MicrosoftIdentityWebChallengeUserException exception, ClaimsPrincipal user)
    {
        var handler = _services.GetService<MicrosoftIdentityConsentAndConditionalAccessHandler>();
        if (handler == null)
        {
            _logger.LogWarning(
                "Entra requires the user to sign in again or consent, but no consent handler is registered; " +
                "the user must sign out and sign in again.");
            return;
        }

        try
        {
            handler.User ??= user;
            handler.HandleException(exception);
        }
        catch (NavigationException)
        {
            throw; // the redirect itself, outside an interactive circuit
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not redirect the user to Entra for re-sign-in/consent");
        }
    }
}

public sealed class ChoTokenProvider : IChoTokenProvider
{
    /// <summary>Named HttpClient for the token service. It never carries a CHO token.</summary>
    public const string TokenServiceClientName = "cho-token-service";

    /// <summary>Roles the Development-only LocalDemo user acts with.</summary>
    public static readonly IReadOnlyList<string> LocalDemoRoles = new[]
    {
        ChoRolePermissions.TenantAdmin,
        ChoRolePermissions.ClaimsSupervisor,
        ChoRolePermissions.MemberServices,
        ChoRolePermissions.ProviderRelations,
        ChoRolePermissions.Finance,
    };

    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LocalDemoTokenLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan TenantPreferenceLifetime = TimeSpan.FromHours(12);
    private const string HomeTenantKey = "@home";

    private readonly AuthenticationStateProvider _authenticationStateProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<ChoTokenProvider> _logger;
    private readonly ITokenAcquisition? _tokenAcquisition;
    private readonly IChoReauthenticationHandler? _reauthentication;
    private readonly IDistributedCache? _tenantPreferences;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromSeconds(10);

    private string? _selectedTenantId;
    private bool _preferenceLoaded;
    private ChoTokenResult? _lastFailure;
    private DateTimeOffset _lastFailureAt;
    private IReadOnlyList<ChoTenantInfo>? _tenants;

    public ChoTokenProvider(
        AuthenticationStateProvider authenticationStateProvider,
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<ChoTokenProvider> logger,
        ITokenAcquisition? tokenAcquisition = null,
        IChoReauthenticationHandler? reauthentication = null,
        IDistributedCache? tenantPreferences = null,
        TimeProvider? time = null)
    {
        _authenticationStateProvider = authenticationStateProvider;
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
        _tokenAcquisition = tokenAcquisition;
        _reauthentication = reauthentication;
        _tenantPreferences = tenantPreferences;
        _time = time ?? TimeProvider.System;
    }

    public string? CurrentTenantId => _selectedTenantId;

    public async Task<ChoTokenResult> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        var user = await GetUserAsync();
        if (user == null)
            return ChoTokenResult.Failure(ChoTokenStatus.NotAuthenticated);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // A failure is remembered briefly so a page making many calls does not
            // hammer the token service; a switch or a later call retries.
            if (_lastFailure != null && _time.GetUtcNow() - _lastFailureAt < FailureRetryDelay)
                return _lastFailure;

            var userKey = UserCacheKey(user);
            var fromPreference = false;
            if (_selectedTenantId == null && !_preferenceLoaded)
            {
                _preferenceLoaded = true;
                _selectedTenantId = await LoadTenantPreferenceAsync(userKey, cancellationToken);
                fromPreference = _selectedTenantId != null;
            }

            var result = await GetOrExchangeAsync(user, userKey, _selectedTenantId, cancellationToken);

            if (fromPreference && result.Status is ChoTokenStatus.NoAccess or ChoTokenStatus.TenantRequired)
            {
                // The remembered tenant is no longer allowed: forget it and use the home tenant.
                await ForgetTenantPreferenceAsync(userKey, cancellationToken);
                _selectedTenantId = null;
                result = await GetOrExchangeAsync(user, userKey, null, cancellationToken);
            }

            if (result.Status == ChoTokenStatus.TenantRequired && _selectedTenantId == null && result.Tenants.Count > 0)
            {
                // Several tenants and no home match: act in the first tenant the service
                // offered (the user can switch). The token service still decides access.
                var first = result.Tenants[0].TenantId;
                _logger.LogInformation("Token service requires a tenant choice; using the first offered tenant {TenantId}", first);
                result = await GetOrExchangeAsync(user, userKey, first, cancellationToken);
            }

            if (result.Succeeded)
            {
                _selectedTenantId = result.Token!.TenantId;
                _lastFailure = null;
            }
            else
            {
                _lastFailure = result;
                _lastFailureAt = _time.GetUtcNow();
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate(ChoTokenExchangeResponse rejected)
    {
        // Only entries holding the rejected token are removed.
        string[] keys;
        lock (_issuedKeys) keys = _issuedKeys.ToArray();
        foreach (var key in keys)
        {
            if (_cache.TryGetValue(key, out ChoTokenExchangeResponse? cached)
                && cached != null
                && string.Equals(cached.AccessToken, rejected.AccessToken, StringComparison.Ordinal))
            {
                _cache.Remove(key);
            }
        }
    }

    public async Task<IReadOnlyList<ChoTenantInfo>?> GetTenantsAsync(CancellationToken cancellationToken = default)
    {
        if (_tenants != null)
            return _tenants;

        var user = await GetUserAsync();
        if (user == null)
            return null;

        if (IsLocalDemo(user))
        {
            if (!LocalDemoAllowed(user))
                return null;
            _tenants = new[]
            {
                new ChoTenantInfo
                {
                    TenantId = LocalDemoTenantId(),
                    TenantName = _configuration["Authentication:LocalDemo:TenantName"] ?? "Local Demo Tenant",
                    AzureTenantId = _configuration["Authentication:LocalDemo:AzureTenantId"] ?? "local-demo",
                }
            };
            return _tenants;
        }

        var baseUrl = TokenServiceBaseUrl();
        if (baseUrl == null)
            return null;

        var entraToken = await AcquireEntraTokenAsync(user);
        if (entraToken.Token == null)
            return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/v1/token/tenants");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", entraToken.Token);
            using var response = await TokenServiceClient().SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Token service tenant list returned {Status}", (int)response.StatusCode);
                return null;
            }

            var tenants = await response.Content.ReadFromJsonAsync<List<ChoTenantInfo>>(cancellationToken: cancellationToken);
            _tenants = (tenants ?? new List<ChoTenantInfo>())
                .Where(t => !string.IsNullOrWhiteSpace(t.TenantId))
                .ToList();
            return _tenants;
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException
                                       || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "Token service tenant list could not be loaded");
            return null;
        }
    }

    public async Task<ChoTokenResult> SwitchTenantAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return ChoTokenResult.Failure(ChoTokenStatus.NoAccess);

        var user = await GetUserAsync();
        if (user == null)
            return ChoTokenResult.Failure(ChoTokenStatus.NotAuthenticated);

        var tenants = await GetTenantsAsync(cancellationToken);
        if (tenants == null)
            return ChoTokenResult.Failure(ChoTokenStatus.Unavailable);

        if (!tenants.Any(t => string.Equals(t.TenantId, tenantId, StringComparison.Ordinal)))
        {
            _logger.LogWarning("Refused switch to tenant {TenantId}: the token service does not list it for this user", tenantId);
            return ChoTokenResult.Failure(ChoTokenStatus.NoAccess);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var userKey = UserCacheKey(user);

            // Drop what is cached for the current and the target tenant so the
            // token service re-checks access for the switch.
            if (userKey != null)
            {
                _cache.Remove(TokenCacheKey(userKey, _selectedTenantId ?? HomeTenantKey));
                _cache.Remove(TokenCacheKey(userKey, HomeTenantKey));
                _cache.Remove(TokenCacheKey(userKey, tenantId));
            }

            var result = await GetOrExchangeAsync(user, userKey, tenantId, cancellationToken);
            if (!result.Succeeded)
                return result;

            _selectedTenantId = result.Token!.TenantId;
            _preferenceLoaded = true;
            _lastFailure = null;
            await SaveTenantPreferenceAsync(userKey, _selectedTenantId, cancellationToken);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ChoInvitationResult> RedeemInvitationAsync(string code, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            return ChoInvitationResult.Failure(ChoInvitationStatus.NotFound);

        var user = await GetUserAsync();
        if (user == null)
            return ChoInvitationResult.Failure(ChoInvitationStatus.NotAuthenticated);

        if (IsLocalDemo(user))
        {
            // LocalDemo has no Entra identity to link.
            _logger.LogWarning("Invitation redemption is not available in LocalDemo mode");
            return ChoInvitationResult.Failure(ChoInvitationStatus.Unavailable);
        }

        var baseUrl = TokenServiceBaseUrl();
        if (baseUrl == null)
            return ChoInvitationResult.Failure(ChoInvitationStatus.Unavailable);

        var entraToken = await AcquireEntraTokenAsync(user);
        if (entraToken.Token == null)
            return ChoInvitationResult.Failure(entraToken.Failure == ChoTokenStatus.ConsentRequired
                ? ChoInvitationStatus.ConsentRequired
                : ChoInvitationStatus.Unavailable);

        ChoTokenExchangeResponse? token;
        try
        {
            // The code travels in the body only; it is not logged here or by the token service.
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/invitations/redeem")
            {
                Content = JsonContent.Create(new RedeemRequest { Code = code.Trim() }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", entraToken.Token);

            using var response = await TokenServiceClient().SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadInvitationErrorAsync(response, cancellationToken);
                var status = (int)response.StatusCode switch
                {
                    401 => ChoInvitationStatus.InvalidToken,
                    429 => ChoInvitationStatus.RateLimited,
                    _ => error?.Error switch
                    {
                        "not_found" or "invalid_request" => ChoInvitationStatus.NotFound,
                        "expired" => ChoInvitationStatus.Expired,
                        "revoked" => ChoInvitationStatus.Revoked,
                        "already_redeemed" => ChoInvitationStatus.AlreadyRedeemed,
                        "email_mismatch" => ChoInvitationStatus.EmailMismatch,
                        "identity_in_use" => ChoInvitationStatus.IdentityInUse,
                        "no_access" => ChoInvitationStatus.NoAccess,
                        _ => ChoInvitationStatus.Unavailable,
                    },
                };
                _logger.LogWarning("Invitation redemption refused: {Status} ({HttpStatus})", status, (int)response.StatusCode);
                return ChoInvitationResult.Failure(status,
                    status == ChoInvitationStatus.EmailMismatch ? error?.InvitedEmail : null);
            }

            token = await response.Content.ReadFromJsonAsync<ChoTokenExchangeResponse>(cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or NotSupportedException
                                       || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning("Token service could not be reached to redeem an invitation ({Error})", ex.GetType().Name);
            return ChoInvitationResult.Failure(ChoInvitationStatus.Unavailable);
        }

        if (token == null || string.IsNullOrEmpty(token.AccessToken) || string.IsNullOrEmpty(token.TenantId))
        {
            _logger.LogError("Token service returned a redemption response without a token or tenant");
            return ChoInvitationResult.Failure(ChoInvitationStatus.Unavailable);
        }

        // The invited tenant becomes the current one, here and (through the
        // preference) in the next circuit after the page reloads.
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var userKey = UserCacheKey(user);
            if (userKey != null)
            {
                var lifetime = TimeSpan.FromSeconds(token.ExpiresIn) - RefreshMargin;
                if (lifetime > TimeSpan.Zero)
                {
                    var key = TokenCacheKey(userKey, token.TenantId);
                    _cache.Set(key, token, _time.GetUtcNow().Add(lifetime));
                    lock (_issuedKeys) _issuedKeys.Add(key);
                }
            }

            _selectedTenantId = token.TenantId;
            _preferenceLoaded = true;
            _lastFailure = null;
            _tenants = null; // the user's tenant list has changed
            await SaveTenantPreferenceAsync(userKey, token.TenantId, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        _logger.LogInformation("Invitation redeemed; now acting in tenant {TenantId}", token.TenantId);
        return ChoInvitationResult.Success(token);
    }

    private static async Task<ChoInvitationErrorResponse?> ReadInvitationErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<ChoInvitationErrorResponse>(cancellationToken: cancellationToken);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed class RedeemRequest
    {
        [JsonPropertyName("code")] public string Code { get; set; } = string.Empty;
    }

    // ------------------------------------------------------------------

    private readonly HashSet<string> _issuedKeys = new(StringComparer.Ordinal);

    private async Task<ChoTokenResult> GetOrExchangeAsync(
        ClaimsPrincipal user, string? userKey, string? tenantId, CancellationToken cancellationToken)
    {
        var cacheKey = userKey == null ? null : TokenCacheKey(userKey, tenantId ?? HomeTenantKey);
        if (cacheKey != null
            && _cache.TryGetValue(cacheKey, out ChoTokenExchangeResponse? cached)
            && cached != null)
        {
            lock (_issuedKeys) _issuedKeys.Add(cacheKey);
            return ChoTokenResult.Success(cached);
        }

        var result = IsLocalDemo(user)
            ? MintLocalDemoToken(user, tenantId)
            : await ExchangeAsync(user, tenantId, cancellationToken);

        if (result.Succeeded && userKey != null)
        {
            var token = result.Token!;
            var lifetime = TimeSpan.FromSeconds(token.ExpiresIn) - RefreshMargin;
            if (lifetime > TimeSpan.Zero)
            {
                var expires = _time.GetUtcNow().Add(lifetime);
                foreach (var key in new[] { cacheKey!, TokenCacheKey(userKey, token.TenantId) }.Distinct())
                {
                    _cache.Set(key, token, expires);
                    lock (_issuedKeys) _issuedKeys.Add(key);
                }
            }
        }

        return result;
    }

    private async Task<ChoTokenResult> ExchangeAsync(ClaimsPrincipal user, string? tenantId, CancellationToken cancellationToken)
    {
        var baseUrl = TokenServiceBaseUrl();
        if (baseUrl == null)
            return ChoTokenResult.Failure(ChoTokenStatus.Unavailable);

        var entraToken = await AcquireEntraTokenAsync(user);
        if (entraToken.Token == null)
            return ChoTokenResult.Failure(entraToken.Failure);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/token/exchange")
            {
                Content = JsonContent.Create(new ExchangeRequest { TenantId = tenantId }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", entraToken.Token);

            using var response = await TokenServiceClient().SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var token = await response.Content.ReadFromJsonAsync<ChoTokenExchangeResponse>(cancellationToken: cancellationToken);
                if (token == null || string.IsNullOrEmpty(token.AccessToken) || string.IsNullOrEmpty(token.TenantId))
                {
                    _logger.LogError("Token service returned an exchange response without a token or tenant");
                    return ChoTokenResult.Failure(ChoTokenStatus.Unavailable);
                }

                if (tenantId != null && !string.Equals(token.TenantId, tenantId, StringComparison.Ordinal))
                {
                    _logger.LogError(
                        "Token service issued a token for tenant {Issued} when {Requested} was requested; discarding it",
                        token.TenantId, tenantId);
                    return ChoTokenResult.Failure(ChoTokenStatus.Unavailable);
                }

                return ChoTokenResult.Success(token);
            }

            var error = await ReadErrorAsync(response, cancellationToken);
            var status = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => ChoTokenStatus.InvalidToken,
                HttpStatusCode.Forbidden => ChoTokenStatus.NoAccess,
                HttpStatusCode.Conflict => ChoTokenStatus.TenantRequired,
                _ => ChoTokenStatus.Unavailable,
            };

            _logger.LogWarning(
                "Token exchange for tenant {TenantId} failed: {Status} {Error}",
                tenantId ?? "(home)", (int)response.StatusCode, error?.Error);

            return ChoTokenResult.Failure(status, status == ChoTokenStatus.TenantRequired ? error?.Tenants : null);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or NotSupportedException
                                       || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "Token service could not be reached for the token exchange");
            return ChoTokenResult.Failure(ChoTokenStatus.Unavailable);
        }
    }

    private async Task<(string? Token, ChoTokenStatus Failure)> AcquireEntraTokenAsync(ClaimsPrincipal user)
    {
        var scope = _configuration["TokenService:Scope"];
        if (string.IsNullOrWhiteSpace(scope))
        {
            _logger.LogError("TokenService:Scope is not configured; no CHO token can be obtained");
            return (null, ChoTokenStatus.Unavailable);
        }

        if (_tokenAcquisition == null)
        {
            _logger.LogError("Entra token acquisition is not configured; no CHO token can be obtained");
            return (null, ChoTokenStatus.Unavailable);
        }

        var scopes = new[] { scope };
        try
        {
            // Blazor Server: there is no HttpContext during circuit activity, so the
            // user principal from AuthenticationStateProvider is passed explicitly.
            var token = await _tokenAcquisition.GetAccessTokenForUserAsync(
                scopes, tenantId: null, userFlow: null, user: user, tokenAcquisitionOptions: null);
            return (token, ChoTokenStatus.Success);
        }
        catch (MicrosoftIdentityWebChallengeUserException ex)
        {
            _logger.LogInformation("Entra requires re-sign-in or consent for the CHO token scope");
            _reauthentication?.Challenge(ex, user);
            return (null, ChoTokenStatus.ConsentRequired);
        }
        catch (MsalUiRequiredException ex)
        {
            _logger.LogInformation("Entra requires re-sign-in or consent for the CHO token scope");
            _reauthentication?.Challenge(new MicrosoftIdentityWebChallengeUserException(ex, scopes), user);
            return (null, ChoTokenStatus.ConsentRequired);
        }
        catch (MsalException ex)
        {
            _logger.LogWarning(ex, "Entra token acquisition failed");
            return (null, ChoTokenStatus.Unavailable);
        }
    }

    /// <summary>
    /// LocalDemo mode has no Entra and no token service: the portal signs a
    /// development token itself with the well-known development key. Only a
    /// Development host in LocalDemo mode, for the LocalDemo cookie user, gets one.
    /// </summary>
    private ChoTokenResult MintLocalDemoToken(ClaimsPrincipal user, string? tenantId)
    {
        if (!LocalDemoAllowed(user))
        {
            _logger.LogError("LocalDemo token requested outside a Development host in LocalDemo mode; refused");
            return ChoTokenResult.Failure(ChoTokenStatus.Unavailable);
        }

        var demoTenant = LocalDemoTenantId();
        if (tenantId != null && !string.Equals(tenantId, demoTenant, StringComparison.Ordinal))
            return ChoTokenResult.Failure(ChoTokenStatus.NoAccess);

        var email = user.FindFirst(ClaimTypes.Email)?.Value ?? user.FindFirst("preferred_username")?.Value;
        var name = user.FindFirst("name")?.Value ?? user.FindFirst(ClaimTypes.Name)?.Value ?? email ?? "Local Demo Admin";
        var subject = user.FindFirst("oid")?.Value ?? "local-demo-admin";
        var roles = LocalDemoRoles.ToList();
        var permissions = ChoRolePermissions.Expand(roles).ToList();

        var accessToken = ChoDevelopmentAuth.UserTokenIssuer(LocalDemoTokenLifetime)
            .IssueUserToken(subject, demoTenant, roles, permissions, name, email);

        return ChoTokenResult.Success(new ChoTokenExchangeResponse
        {
            AccessToken = accessToken,
            TokenType = "Bearer",
            ExpiresIn = (int)LocalDemoTokenLifetime.TotalSeconds,
            TenantId = demoTenant,
            TenantName = _configuration["Authentication:LocalDemo:TenantName"] ?? "Local Demo Tenant",
            Roles = roles,
            Permissions = permissions,
            User = new ChoTokenUser
            {
                Id = subject,
                Email = email,
                DisplayName = name,
                FirstName = name.Split(' ').FirstOrDefault() ?? name,
                LastName = name.Split(' ').Skip(1).FirstOrDefault() ?? string.Empty,
                Department = "Local Evaluation",
            },
        });
    }

    private bool IsLocalDemoMode()
        => string.Equals(_configuration["Authentication:Mode"], "LocalDemo", StringComparison.OrdinalIgnoreCase);

    private bool IsLocalDemo(ClaimsPrincipal user) => IsLocalDemoMode() && user.HasClaim("cho_local_demo", "true");

    private bool LocalDemoAllowed(ClaimsPrincipal user) => _environment.IsDevelopment() && IsLocalDemo(user);

    private string LocalDemoTenantId() => _configuration["Authentication:LocalDemo:TenantId"] ?? "demo";

    private async Task<ClaimsPrincipal?> GetUserAsync()
    {
        var state = await _authenticationStateProvider.GetAuthenticationStateAsync();
        return state.User.Identity?.IsAuthenticated == true ? state.User : null;
    }

    /// <summary>
    /// The cache partition for one signed-in person: Entra tenant + object id.
    /// Without both, nothing is cached for the user.
    /// </summary>
    internal static string? UserCacheKey(ClaimsPrincipal user)
    {
        var tid = user.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid")?.Value
                  ?? user.FindFirst("tid")?.Value;
        var oid = user.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
                  ?? user.FindFirst("oid")?.Value;
        return string.IsNullOrEmpty(tid) || string.IsNullOrEmpty(oid) ? null : $"{tid}|{oid}";
    }

    private static string TokenCacheKey(string userKey, string tenant) => $"cho-token|{userKey}|{tenant}";

    private static string PreferenceKey(string userKey) => $"cho-tenant-pref|{userKey}";

    private async Task<string?> LoadTenantPreferenceAsync(string? userKey, CancellationToken cancellationToken)
    {
        if (userKey == null || _tenantPreferences == null)
            return null;
        try
        {
            var value = await _tenantPreferences.GetStringAsync(PreferenceKey(userKey), cancellationToken);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tenant preference could not be read");
            return null;
        }
    }

    private async Task SaveTenantPreferenceAsync(string? userKey, string tenantId, CancellationToken cancellationToken)
    {
        if (userKey == null || _tenantPreferences == null)
            return;
        try
        {
            await _tenantPreferences.SetStringAsync(PreferenceKey(userKey), tenantId,
                new DistributedCacheEntryOptions { SlidingExpiration = TenantPreferenceLifetime }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tenant preference could not be saved; the switch lasts for this circuit only");
        }
    }

    private async Task ForgetTenantPreferenceAsync(string? userKey, CancellationToken cancellationToken)
    {
        if (userKey == null || _tenantPreferences == null)
            return;
        try
        {
            await _tenantPreferences.RemoveAsync(PreferenceKey(userKey), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tenant preference could not be removed");
        }
    }

    private string? TokenServiceBaseUrl()
    {
        var baseUrl = _configuration["Services:TokenService"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            _logger.LogError("Services:TokenService is not configured; no CHO token can be obtained");
            return null;
        }
        return baseUrl.TrimEnd('/');
    }

    private HttpClient TokenServiceClient() => _httpClientFactory.CreateClient(TokenServiceClientName);

    private static async Task<ChoTokenErrorResponse?> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<ChoTokenErrorResponse>(cancellationToken: cancellationToken);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed class ExchangeRequest
    {
        [JsonPropertyName("tenantId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? TenantId { get; set; }
    }
}
