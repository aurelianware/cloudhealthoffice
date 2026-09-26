using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace ProviderEligibilityApi.Security;

/// <summary>
/// Authenticates provider applications and binds each request to a tenant the
/// caller's credential is allowed to use. Fails closed: with no usable client
/// configured every API request is refused.
///
/// Tenant comes only from the <c>X-Tenant-ID</c> header and is accepted only
/// when it is on the authenticated client's allow-list. It is never read from
/// the query string or the request body.
/// </summary>
public sealed class ProviderApiAuthenticationMiddleware
{
    public const string ApiKeyHeader = "X-Api-Key";
    public const string TenantHeader = "X-Tenant-ID";
    public const string TenantItemKey = "TenantId";
    public const string ClientItemKey = "ProviderClient";

    private readonly RequestDelegate _next;
    private readonly ILogger<ProviderApiAuthenticationMiddleware> _logger;

    public ProviderApiAuthenticationMiddleware(
        RequestDelegate next,
        ILogger<ProviderApiAuthenticationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IOptions<ProviderApiOptions> configured)
    {
        if (IsHealthPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var clients = configured.Value.Clients.Where(c => c.IsUsable).ToList();
        if (clients.Count == 0)
        {
            _logger.LogError("Provider API has no usable client credentials configured; refusing request");
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var supplied = context.Request.Headers[ApiKeyHeader].FirstOrDefault();
        var client = clients.FirstOrDefault(c => FixedTimeEquals(supplied, c.ApiKey));
        if (client is null)
        {
            _logger.LogWarning("Provider API request rejected: missing or invalid credential");
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var tenantId = context.Request.Headers[TenantHeader].FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "X-Tenant-ID header is required." });
            return;
        }

        if (!client.Tenants.Any(t => string.Equals(t.Trim(), tenantId, StringComparison.Ordinal)))
        {
            _logger.LogWarning(
                "Provider API client {Client} is not allowed to act for the requested tenant",
                client.Name);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        context.Items[TenantItemKey] = tenantId;
        context.Items[ClientItemKey] = client.Name;
        await _next(context);
    }

    private static bool IsHealthPath(PathString path) =>
        path.StartsWithSegments("/health") ||
        path.StartsWithSegments("/ready") ||
        path.StartsWithSegments("/live");

    private static bool FixedTimeEquals(string? supplied, string expected)
    {
        if (supplied is null) return false;
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return suppliedBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
    }
}
