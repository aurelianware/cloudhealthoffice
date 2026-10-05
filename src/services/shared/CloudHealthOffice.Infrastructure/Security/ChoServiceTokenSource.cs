using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.Infrastructure.Security;

/// <summary>
/// Where this service gets its own CHO service token (role <c>cho.service</c>,
/// <c>sub</c> = <c>azp</c> = its client id) for a tenant, when it calls another
/// CHO service without a user token to forward.
///
/// <list type="bullet">
///   <item><see cref="ChoServiceTokenSourceKind.TokenService"/> (deployed
///   environments): token-service issues the token after authenticating this
///   service by its Azure workload identity. The service holds no signing key,
///   so it can only ever obtain tokens naming itself.</item>
///   <item><see cref="ChoServiceTokenSourceKind.LocalKey"/> (Development and
///   Testing only): the service signs with a local key, as before.</item>
/// </list>
/// </summary>
public interface IChoServiceTokenSource
{
    /// <summary>This service's client id: <c>sub</c> and <c>azp</c> of its tokens.</summary>
    string ClientId { get; }

    /// <summary>
    /// A service token for <paramref name="tenantId"/>. Throws
    /// <see cref="ChoServiceTokenUnavailableException"/> when none can be obtained.
    /// </summary>
    ValueTask<string> GetTokenAsync(string tenantId, CancellationToken cancellationToken = default);
}

/// <summary>How <c>ChoAuth:ServiceToken</c> obtains service tokens.</summary>
public enum ChoServiceTokenSourceKind
{
    /// <summary>Sign locally with <c>PrivateKeyPem</c>/<c>SymmetricKey</c>. Development and Testing only.</summary>
    LocalKey,

    /// <summary>Exchange this service's Azure workload identity at token-service (<c>POST /v1/token/service</c>).</summary>
    TokenService,
}

/// <summary>
/// No service token could be obtained (token-service or Entra unreachable,
/// or this identity is not registered). An <see cref="HttpRequestException"/>,
/// so callers that treat a failed downstream call as unavailable do the same here.
/// </summary>
public sealed class ChoServiceTokenUnavailableException : HttpRequestException
{
    public ChoServiceTokenUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

public static class ChoServiceTokens
{
    /// <summary>
    /// This service's token source: the registered <see cref="IChoServiceTokenSource"/>,
    /// or (for hosts that register a <see cref="ChoTokenIssuer"/> and
    /// <c>ChoAuth:ServiceToken</c> directly, as some tests do) a local-key source
    /// over them. Null when no service token is configured.
    /// </summary>
    public static IChoServiceTokenSource? Resolve(IServiceProvider services)
    {
        var source = services.GetService<IChoServiceTokenSource>();
        if (source != null)
            return source;

        var issuer = services.GetService<ChoTokenIssuer>();
        var clientId = services.GetService<ChoAuthOptions>()?.ServiceToken?.ClientId;
        return issuer != null && !string.IsNullOrEmpty(clientId)
            ? new LocalKeyChoServiceTokenSource(issuer, clientId)
            : null;
    }

    /// <summary>
    /// Registers the token source <c>ChoAuth:ServiceToken</c> selects.
    /// <see cref="ChoAuthOptions.Validate"/> has already refused a local key
    /// outside Development/Testing. Called by <c>AddChoAuthentication</c>; a host
    /// that does not use it (a CLI) calls it directly.
    /// </summary>
    public static IServiceCollection AddChoServiceTokenSource(this IServiceCollection services, ChoAuthOptions options)
    {
        var st = options.ServiceToken;
        if (st == null)
            return services;

        if (st.EffectiveSource == ChoServiceTokenSourceKind.LocalKey)
        {
            services.TryAddSingleton(ChoTokenIssuer.FromKeys(
                st.Issuer, options.Audience, st.PrivateKeyPem, st.SymmetricKey, st.Lifetime));
            services.TryAddSingleton<IChoServiceTokenSource>(sp =>
                new LocalKeyChoServiceTokenSource(sp.GetRequiredService<ChoTokenIssuer>(), st.ClientId));
            return services;
        }

        services.AddHttpClient(TokenServiceChoServiceTokenSource.HttpClientName, client =>
            {
                client.BaseAddress = new Uri(st.TokenServiceUrl!.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(10);
            })
            // This client carries the Entra token itself. The shared CHO handler
            // must never add (or try to obtain) a CHO token for it.
            .ConfigureAdditionalHttpMessageHandlers((handlers, _) =>
            {
                for (var i = handlers.Count - 1; i >= 0; i--)
                {
                    if (handlers[i] is ChoOutboundTokenHandler)
                        handlers.RemoveAt(i);
                }
            });

        services.TryAddSingleton<IChoServiceTokenSource>(sp => new TokenServiceChoServiceTokenSource(
            st,
            sp.GetService<ChoServiceTokenCredential>()?.Credential ?? DefaultCredential(st),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(TokenServiceChoServiceTokenSource.HttpClientName),
            sp.GetService<ILogger<TokenServiceChoServiceTokenSource>>(),
            sp.GetService<TimeProvider>()));
        return services;
    }

    private static TokenCredential DefaultCredential(ChoServiceTokenOptions st)
    {
        // In AKS the workload identity webhook sets AZURE_CLIENT_ID,
        // AZURE_TENANT_ID and AZURE_FEDERATED_TOKEN_FILE for the pod's service
        // account; DefaultAzureCredential picks WorkloadIdentityCredential.
        var clientId = string.IsNullOrWhiteSpace(st.ManagedIdentityClientId) ? null : st.ManagedIdentityClientId;
        return new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = clientId,
            WorkloadIdentityClientId = clientId,
            ExcludeInteractiveBrowserCredential = true,
            ExcludeVisualStudioCredential = true,
            ExcludeVisualStudioCodeCredential = true,
            ExcludeSharedTokenCacheCredential = true,
        });
    }
}

/// <summary>
/// Overrides the Azure credential the token-service source authenticates with
/// (tests). Without it, <see cref="DefaultAzureCredential"/> is used.
/// </summary>
public sealed record ChoServiceTokenCredential(TokenCredential Credential);

/// <summary>Signs service tokens locally. Development and Testing only.</summary>
public sealed class LocalKeyChoServiceTokenSource : IChoServiceTokenSource
{
    private readonly ChoTokenIssuer _issuer;

    public LocalKeyChoServiceTokenSource(ChoTokenIssuer issuer, string clientId)
    {
        _issuer = issuer;
        ClientId = clientId;
    }

    public string ClientId { get; }

    public ValueTask<string> GetTokenAsync(string tenantId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_issuer.IssueServiceToken(ClientId, tenantId));
}

/// <summary>
/// Gets service tokens from token-service: an Entra access token for
/// token-service's API, obtained with this pod's workload identity, is
/// exchanged at <c>POST /v1/token/service</c> for a CHO service token for one
/// tenant. token-service decides the client id from the Entra identity; the
/// response must name the client id this service is configured with.
///
/// Tokens are cached per tenant until <see cref="ChoServiceTokenOptions.RefreshBefore"/>
/// before they expire. Concurrent requests for the same tenant share one exchange.
/// </summary>
public sealed class TokenServiceChoServiceTokenSource : IChoServiceTokenSource
{
    public const string HttpClientName = "cho-service-token";
    public const string ExchangePath = "v1/token/service";

    /// <summary>Most tenants cached at once; beyond it the cache starts over.</summary>
    private const int MaxCachedTenants = 1024;

    private readonly ChoServiceTokenOptions _options;
    private readonly TokenCredential _credential;
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, CachedToken> _tokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public TokenServiceChoServiceTokenSource(
        ChoServiceTokenOptions options,
        TokenCredential credential,
        HttpClient http,
        ILogger<TokenServiceChoServiceTokenSource>? logger = null,
        TimeProvider? time = null)
    {
        _options = options;
        _credential = credential;
        _http = http;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
    }

    public string ClientId => _options.ClientId;

    public async ValueTask<string> GetTokenAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("Tenant is required.", nameof(tenantId));

        if (TryCached(tenantId, out var cached))
            return cached;

        var gate = _gates.GetOrAdd(tenantId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (TryCached(tenantId, out cached))
                return cached;

            var fresh = await ExchangeAsync(tenantId, cancellationToken);
            if (_tokens.Count >= MaxCachedTenants)
                _tokens.Clear();
            _tokens[tenantId] = fresh;
            return fresh.Token;
        }
        finally
        {
            gate.Release();
        }
    }

    private bool TryCached(string tenantId, out string token)
    {
        if (_tokens.TryGetValue(tenantId, out var entry) && _time.GetUtcNow() < entry.RefreshAt)
        {
            token = entry.Token;
            return true;
        }
        token = string.Empty;
        return false;
    }

    private async Task<CachedToken> ExchangeAsync(string tenantId, CancellationToken ct)
    {
        string entraToken;
        try
        {
            var access = await _credential.GetTokenAsync(new TokenRequestContext([_options.EntraScope!]), ct);
            entraToken = access.Token;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError("Service token for {ClientId} unavailable: no Entra token for {Scope} ({Error})",
                _options.ClientId, _options.EntraScope, ex.GetType().Name);
            throw new ChoServiceTokenUnavailableException(
                "No Entra workload-identity token could be obtained for token-service.", ex);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, ExchangePath)
        {
            Content = JsonContent.Create(new { tenantId }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", entraToken);

        var requestedAt = _time.GetUtcNow();
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogError("Service token for {ClientId} unavailable: token-service unreachable ({Error})",
                _options.ClientId, ex.GetType().Name);
            throw new ChoServiceTokenUnavailableException("token-service could not be reached.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "Service token for {ClientId} refused by token-service: {Status}. A 403 means this workload " +
                    "identity is not registered for this client id (ServiceTokens:ServiceClients).",
                    _options.ClientId, (int)response.StatusCode);
                throw new ChoServiceTokenUnavailableException(
                    $"token-service answered {(int)response.StatusCode} to the service-token request.");
            }

            ServiceTokenResponse? body;
            try
            {
                body = await response.Content.ReadFromJsonAsync<ServiceTokenResponse>(ct);
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new ChoServiceTokenUnavailableException("token-service returned a malformed service-token response.", ex);
            }

            if (body == null || string.IsNullOrEmpty(body.AccessToken) || body.ExpiresIn <= 0)
                throw new ChoServiceTokenUnavailableException("token-service returned no service token.");

            // token-service decides who this pod is. A different client id means
            // the identity is registered under another name: a misconfiguration,
            // never something to send on.
            if (!string.Equals(body.ClientId, _options.ClientId, StringComparison.Ordinal)
                || !string.Equals(body.TenantId, tenantId, StringComparison.Ordinal))
            {
                _logger.LogError(
                    "token-service issued a service token for client {Issued} / tenant {IssuedTenant}, " +
                    "but this service is {ClientId} asking for {Tenant}; the token is not used",
                    body.ClientId, body.TenantId, _options.ClientId, tenantId);
                throw new ChoServiceTokenUnavailableException(
                    "token-service issued a service token for a different client or tenant.");
            }

            var lifetime = TimeSpan.FromSeconds(body.ExpiresIn);
            var margin = _options.RefreshBefore < lifetime / 2 ? _options.RefreshBefore : lifetime / 2;
            return new CachedToken(body.AccessToken, requestedAt + lifetime - margin);
        }
    }

    private sealed record CachedToken(string Token, DateTimeOffset RefreshAt);

    private sealed class ServiceTokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("client_id")] public string? ClientId { get; set; }
        [JsonPropertyName("tenant_id")] public string? TenantId { get; set; }
    }
}
