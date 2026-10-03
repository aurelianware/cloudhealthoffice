using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using CloudHealthOffice.Infrastructure.Security;

namespace CloudHealthOffice.TokenService.Directory;

/// <summary>
/// tenant-service's identity read model (<c>/internal/v1/identity</c>). Any
/// failure to reach it surfaces as <see cref="TenantDirectoryUnavailableException"/>,
/// never as an empty answer, so a lookup outage cannot be mistaken for "no
/// membership" or, worse, produce a token on partial data.
/// </summary>
public interface ITenantDirectory
{
    Task<IReadOnlyList<DirectoryMembership>> GetMembershipsAsync(string tid, string oid, CancellationToken ct);
    Task<DirectoryTenant?> GetTenantAsync(string tenantId, CancellationToken ct);
    Task<IReadOnlyList<DirectoryTenant>> GetTenantsAsync(string? azureTenantId, CancellationToken ct);
    Task<DirectoryUser?> FindUserByEmailAsync(string tenantId, string email, CancellationToken ct);

    /// <summary>Records oid+tid on an unlinked user. Null when the user is gone or already linked elsewhere.</summary>
    Task<DirectoryUser?> LinkAsync(string tenantId, string userId, string oid, string tid, CancellationToken ct);
}

public sealed class TenantDirectoryUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

public sealed class DirectoryMembership
{
    [JsonPropertyName("user")] public DirectoryUser User { get; set; } = new();
    [JsonPropertyName("tenant")] public DirectoryTenant? Tenant { get; set; }
}

public sealed class DirectoryUser
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("tenantId")] public string TenantId { get; set; } = string.Empty;
    [JsonPropertyName("email")] public string Email { get; set; } = string.Empty;
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = string.Empty;
    [JsonPropertyName("firstName")] public string FirstName { get; set; } = string.Empty;
    [JsonPropertyName("lastName")] public string LastName { get; set; } = string.Empty;
    [JsonPropertyName("department")] public string Department { get; set; } = string.Empty;
    [JsonPropertyName("roles")] public List<string>? Roles { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("azureAdObjectId")] public string? AzureAdObjectId { get; set; }
    [JsonPropertyName("azureAdTenantId")] public string? AzureAdTenantId { get; set; }

    public bool IsActive => string.Equals(Status, "Active", StringComparison.OrdinalIgnoreCase);
}

public sealed class DirectoryTenant
{
    [JsonPropertyName("tenantId")] public string TenantId { get; set; } = string.Empty;
    [JsonPropertyName("tenantName")] public string TenantName { get; set; } = string.Empty;
    [JsonPropertyName("azureTenantId")] public string? AzureTenantId { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("isActive")] public bool IsActive { get; set; }
}

/// <summary>Talks to tenant-service through the <see cref="ClientName"/> factory client.</summary>
public sealed class HttpTenantDirectory : ITenantDirectory
{
    public const string ClientName = "tenant-service";
    private const string Base = "internal/v1/identity";

    private readonly IHttpClientFactory _factory;

    public HttpTenantDirectory(IHttpClientFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<DirectoryMembership>> GetMembershipsAsync(string tid, string oid, CancellationToken ct)
        => await SendAsync<List<DirectoryMembership>>(HttpMethod.Get,
               $"{Base}/memberships?tid={Uri.EscapeDataString(tid)}&oid={Uri.EscapeDataString(oid)}",
               TenantServiceTokenHandler.CrossTenantScope, null, ct)
           ?? throw new TenantDirectoryUnavailableException("tenant-service returned no membership list.");

    public Task<DirectoryTenant?> GetTenantAsync(string tenantId, CancellationToken ct)
        => SendAsync<DirectoryTenant>(HttpMethod.Get, $"{Base}/tenants/{Uri.EscapeDataString(tenantId)}",
            tenantId, null, ct, notFoundIsNull: true);

    public async Task<IReadOnlyList<DirectoryTenant>> GetTenantsAsync(string? azureTenantId, CancellationToken ct)
    {
        var path = string.IsNullOrEmpty(azureTenantId)
            ? $"{Base}/tenants"
            : $"{Base}/tenants?azureTenantId={Uri.EscapeDataString(azureTenantId)}";
        return await SendAsync<List<DirectoryTenant>>(HttpMethod.Get, path, TenantServiceTokenHandler.CrossTenantScope, null, ct)
               ?? throw new TenantDirectoryUnavailableException("tenant-service returned no tenant list.");
    }

    public Task<DirectoryUser?> FindUserByEmailAsync(string tenantId, string email, CancellationToken ct)
        => SendAsync<DirectoryUser>(HttpMethod.Post,
            $"{Base}/tenants/{Uri.EscapeDataString(tenantId)}/users/find-by-email",
            tenantId, new { email }, ct, notFoundIsNull: true);

    public Task<DirectoryUser?> LinkAsync(string tenantId, string userId, string oid, string tid, CancellationToken ct)
        => SendAsync<DirectoryUser>(HttpMethod.Post,
            $"{Base}/tenants/{Uri.EscapeDataString(tenantId)}/users/{Uri.EscapeDataString(userId)}/entra-link",
            tenantId, new { azureAdObjectId = oid, azureAdTenantId = tid }, ct,
            notFoundIsNull: true, conflictIsNull: true);

    private async Task<T?> SendAsync<T>(
        HttpMethod method, string path, string tenantScope, object? body, CancellationToken ct,
        bool notFoundIsNull = false, bool conflictIsNull = false) where T : class
    {
        try
        {
            using var request = new HttpRequestMessage(method, path);
            request.Options.Set(TenantServiceTokenHandler.TenantScopeKey, tenantScope);
            if (body != null)
                request.Content = JsonContent.Create(body);

            using var response = await _factory.CreateClient(ClientName).SendAsync(request, ct);
            if (notFoundIsNull && response.StatusCode == HttpStatusCode.NotFound)
                return null;
            if (conflictIsNull && response.StatusCode == HttpStatusCode.Conflict)
                return null;
            if (!response.IsSuccessStatusCode)
                throw new TenantDirectoryUnavailableException(
                    $"tenant-service answered {(int)response.StatusCode} for {method} {path.Split('?')[0]}.");

            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
        }
        catch (TenantDirectoryUnavailableException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
                                       or NotSupportedException or InvalidOperationException)
        {
            if (ex is TaskCanceledException && ct.IsCancellationRequested)
                throw;
            throw new TenantDirectoryUnavailableException("tenant-service is unreachable.", ex);
        }
    }
}

/// <summary>
/// Authenticates token-service's calls to tenant-service with a CHO service
/// token (subject <c>token-service</c>).
///
/// This replaces the shared <see cref="ChoOutboundTokenHandler"/> for this one
/// client on purpose: inside a request, the shared handler forwards the
/// caller's bearer token, which here is the user's Entra token. That token must
/// never travel onwards, and tenant-service would not accept it anyway. The
/// token is always minted here and names the tenant the call is about, or
/// <see cref="CrossTenantScope"/> for lookups that span tenants.
/// </summary>
public sealed class TenantServiceTokenHandler : DelegatingHandler
{
    /// <summary>
    /// Tenant named by service tokens for cross-tenant identity lookups. When
    /// tenant-service adopts CHO authentication, its identity endpoints must
    /// authorize the token-service identity itself, not this tenant value.
    /// </summary>
    public const string CrossTenantScope = "cho-platform";

    public static readonly HttpRequestOptionsKey<string> TenantScopeKey = new("cho.tenant-scope");

    private readonly ServiceTokenSource _source;

    public TenantServiceTokenHandler(ServiceTokenSource source) => _source = source;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Whatever was set before is discarded: only a freshly minted service token goes out.
        request.Headers.Authorization = null;
        if (_source.Issuer != null
            && request.Options.TryGetValue(TenantScopeKey, out var tenant) && !string.IsNullOrEmpty(tenant))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", _source.Issuer.IssueServiceToken(_source.ClientId, tenant));
            if (tenant != CrossTenantScope)
                request.Headers.TryAddWithoutValidation("X-Tenant-ID", tenant);
        }
        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>The service-token issuer for calls to tenant-service, from <c>ChoAuth:ServiceToken</c>.</summary>
public sealed class ServiceTokenSource
{
    public ServiceTokenSource(ChoTokenIssuer? issuer, string clientId)
    {
        Issuer = issuer;
        ClientId = clientId;
    }

    public ChoTokenIssuer? Issuer { get; }
    public string ClientId { get; }
}
