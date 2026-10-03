using System.Net;
using System.Text.Json.Serialization;

namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// 200 response of the CHO token service's <c>POST /v1/token/exchange</c>.
/// The token service is the source of truth for the user's tenant, roles and
/// permissions; the portal displays and forwards them, it never decides them.
/// </summary>
public sealed class ChoTokenExchangeResponse
{
    [JsonPropertyName("access_token")] public string AccessToken { get; set; } = string.Empty;
    [JsonPropertyName("token_type")] public string TokenType { get; set; } = "Bearer";
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    [JsonPropertyName("tenant_id")] public string TenantId { get; set; } = string.Empty;
    [JsonPropertyName("tenant_name")] public string? TenantName { get; set; }
    [JsonPropertyName("roles")] public List<string> Roles { get; set; } = new();
    [JsonPropertyName("permissions")] public List<string> Permissions { get; set; } = new();
    [JsonPropertyName("user")] public ChoTokenUser? User { get; set; }
}

public sealed class ChoTokenUser
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    [JsonPropertyName("firstName")] public string? FirstName { get; set; }
    [JsonPropertyName("lastName")] public string? LastName { get; set; }
    [JsonPropertyName("department")] public string? Department { get; set; }
}

/// <summary>A CHO tenant the token service says the signed-in user may act in.</summary>
public sealed class ChoTenantInfo
{
    [JsonPropertyName("tenantId")] public string TenantId { get; set; } = string.Empty;
    [JsonPropertyName("tenantName")] public string? TenantName { get; set; }
    [JsonPropertyName("azureTenantId")] public string? AzureTenantId { get; set; }
}

internal sealed class ChoTokenErrorResponse
{
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("tenants")] public List<ChoTenantInfo>? Tenants { get; set; }
}

public enum ChoTokenStatus
{
    /// <summary>A CHO token is available.</summary>
    Success,
    /// <summary>Nobody is signed in.</summary>
    NotAuthenticated,
    /// <summary>The token service rejected the user's Entra token (401 invalid_token).</summary>
    InvalidToken,
    /// <summary>The user has no access to the requested (or any) CHO tenant (403 no_access).</summary>
    NoAccess,
    /// <summary>The user belongs to several tenants and none was chosen (409 tenant_required).</summary>
    TenantRequired,
    /// <summary>The token service is unreachable, misconfigured or failed (503, network, bad response).</summary>
    Unavailable,
    /// <summary>Entra needs the user to sign in again or consent before a token can be issued.</summary>
    ConsentRequired,
}

/// <summary>The outcome of asking for a CHO token. Only <see cref="ChoTokenStatus.Success"/> carries a token.</summary>
public sealed class ChoTokenResult
{
    private ChoTokenResult(ChoTokenStatus status, ChoTokenExchangeResponse? token, IReadOnlyList<ChoTenantInfo>? tenants)
    {
        Status = status;
        Token = token;
        Tenants = tenants ?? Array.Empty<ChoTenantInfo>();
    }

    public ChoTokenStatus Status { get; }
    public ChoTokenExchangeResponse? Token { get; }

    /// <summary>For <see cref="ChoTokenStatus.TenantRequired"/>: the tenants the service offered.</summary>
    public IReadOnlyList<ChoTenantInfo> Tenants { get; }

    public bool Succeeded => Status == ChoTokenStatus.Success && Token != null;

    public static ChoTokenResult Success(ChoTokenExchangeResponse token) => new(ChoTokenStatus.Success, token, null);
    public static ChoTokenResult Failure(ChoTokenStatus status, IReadOnlyList<ChoTenantInfo>? tenants = null)
        => new(status, null, tenants);
}

/// <summary>
/// Thrown by <see cref="ChoBearerTokenHandler"/> instead of sending a request to a
/// CHO service when no CHO token could be obtained. It derives from
/// <see cref="HttpRequestException"/> so existing service-client error handling
/// (which maps those to <see cref="ServiceUnavailableException"/>) still applies.
/// </summary>
public sealed class ChoTokenUnavailableException : HttpRequestException
{
    public ChoTokenUnavailableException(ChoTokenStatus status, string? host)
        : base(
            $"No CHO access token is available ({status}); the request to {host ?? "a CHO service"} was not sent.",
            null,
            status == ChoTokenStatus.NoAccess ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized)
    {
        Status = status;
    }

    public ChoTokenStatus Status { get; }
}
