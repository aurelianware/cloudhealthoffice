using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// tenant-service's invitation and identity-link administration for the
/// current tenant (users:manage). Calls go through the portal's HttpClient,
/// which carries the user's CHO token.
/// </summary>
public interface ITenantInvitationService
{
    Task<IReadOnlyList<InvitationItem>> ListAsync(string tenantId, CancellationToken ct = default);
    Task<InvitationCallResult<IssuedInvitation>> CreateAsync(string tenantId, InviteUserRequest request, CancellationToken ct = default);
    Task<InvitationCallResult<IssuedInvitation>> ResendAsync(string tenantId, string invitationId, CancellationToken ct = default);
    Task<InvitationCallResult<InvitationItem>> RevokeAsync(string tenantId, string invitationId, CancellationToken ct = default);

    /// <summary>Clears a user's Entra link.</summary>
    Task<InvitationCallResult<bool>> UnlinkUserAsync(string tenantId, string userId, CancellationToken ct = default);
}

public sealed class TenantInvitationService : ITenantInvitationService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TenantInvitationService> _logger;

    public TenantInvitationService(HttpClient http, IConfiguration configuration, ILogger<TenantInvitationService> logger)
    {
        _http = http;
        _configuration = configuration;
        _logger = logger;
    }

    private string BaseUrl => (_configuration["Services:TenantService"] ?? "http://tenant-service.cho-svcs/api").TrimEnd('/');

    private string Invitations(string tenantId) => $"{BaseUrl}/v1/tenants/{Uri.EscapeDataString(tenantId)}/invitations";

    public async Task<IReadOnlyList<InvitationItem>> ListAsync(string tenantId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync(Invitations(tenantId), ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Invitation list returned {Status}", (int)response.StatusCode);
            return Array.Empty<InvitationItem>();
        }
        return await response.Content.ReadFromJsonAsync<List<InvitationItem>>(cancellationToken: ct) ?? new List<InvitationItem>();
    }

    public Task<InvitationCallResult<IssuedInvitation>> CreateAsync(string tenantId, InviteUserRequest request, CancellationToken ct = default)
        => SendAsync<IssuedInvitation>(HttpMethod.Post, Invitations(tenantId), request, ct);

    public Task<InvitationCallResult<IssuedInvitation>> ResendAsync(string tenantId, string invitationId, CancellationToken ct = default)
        => SendAsync<IssuedInvitation>(HttpMethod.Post, $"{Invitations(tenantId)}/{Uri.EscapeDataString(invitationId)}/resend", null, ct);

    public Task<InvitationCallResult<InvitationItem>> RevokeAsync(string tenantId, string invitationId, CancellationToken ct = default)
        => SendAsync<InvitationItem>(HttpMethod.Post, $"{Invitations(tenantId)}/{Uri.EscapeDataString(invitationId)}/revoke", null, ct);

    public async Task<InvitationCallResult<bool>> UnlinkUserAsync(string tenantId, string userId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/v1/tenants/{Uri.EscapeDataString(tenantId)}/users/{Uri.EscapeDataString(userId)}/unlink";
        using var response = await _http.PostAsync(url, null, ct);
        return response.IsSuccessStatusCode
            ? InvitationCallResult<bool>.Ok(true)
            : InvitationCallResult<bool>.Fail(await ReadErrorAsync(response, ct));
    }

    private async Task<InvitationCallResult<T>> SendAsync<T>(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body != null)
            request.Content = JsonContent.Create(body);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return InvitationCallResult<T>.Fail(await ReadErrorAsync(response, ct));

        var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
        return value == null ? InvitationCallResult<T>.Fail("The service returned an empty response.") : InvitationCallResult<T>.Ok(value);
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorBody>(cancellationToken: ct);
            var text = error?.Message ?? error?.Error;
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }
        catch (Exception)
        {
            // fall through
        }
        return $"The request failed ({(int)response.StatusCode}).";
    }

    private sealed class ErrorBody
    {
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }
}

public sealed class InvitationCallResult<T>
{
    public bool Succeeded { get; private init; }
    public T? Value { get; private init; }
    public string? Error { get; private init; }

    public static InvitationCallResult<T> Ok(T value) => new() { Succeeded = true, Value = value };
    public static InvitationCallResult<T> Fail(string error) => new() { Error = error };
}

public sealed class InviteUserRequest
{
    [JsonPropertyName("email")] public string Email { get; set; } = string.Empty;
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = string.Empty;
    [JsonPropertyName("firstName")] public string FirstName { get; set; } = string.Empty;
    [JsonPropertyName("lastName")] public string LastName { get; set; } = string.Empty;
    [JsonPropertyName("department")] public string Department { get; set; } = string.Empty;
    [JsonPropertyName("roles")] public List<string> Roles { get; set; } = new();
}

public sealed class InvitationItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("userId")] public string UserId { get; set; } = string.Empty;
    [JsonPropertyName("email")] public string Email { get; set; } = string.Empty;
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = string.Empty;
    [JsonPropertyName("department")] public string Department { get; set; } = string.Empty;
    [JsonPropertyName("roles")] public List<string> Roles { get; set; } = new();
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("expiresAt")] public DateTime ExpiresAt { get; set; }
    [JsonPropertyName("redeemedAt")] public DateTime? RedeemedAt { get; set; }
    [JsonPropertyName("revokedAt")] public DateTime? RevokedAt { get; set; }
}

/// <summary>The answer to create/resend: the code and link are shown once.</summary>
public sealed class IssuedInvitation
{
    [JsonPropertyName("invitation")] public InvitationItem Invitation { get; set; } = new();
    [JsonPropertyName("code")] public string Code { get; set; } = string.Empty;
    [JsonPropertyName("redemptionUrl")] public string RedemptionUrl { get; set; } = string.Empty;
}
