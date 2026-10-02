using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace CloudHealthOffice.Infrastructure.Security;

/// <summary>
/// Who is acting on the current request, read from the validated token and
/// nothing else. Writes record <see cref="UserId"/> as the actor; request bodies
/// and headers that claim an actor are ignored.
/// </summary>
public interface ICurrentActor
{
    bool IsAuthenticated { get; }

    /// <summary>The token subject: a CHO user id, or a service client id.</summary>
    string UserId { get; }

    string? DisplayName { get; }

    string? Email { get; }

    /// <summary>The tenant from the token.</summary>
    string TenantId { get; }

    bool IsService { get; }

    IReadOnlyCollection<string> Roles { get; }

    bool HasPermission(string permission);
}

public sealed class HttpContextCurrentActor : ICurrentActor
{
    private readonly IHttpContextAccessor _accessor;

    public HttpContextCurrentActor(IHttpContextAccessor accessor) => _accessor = accessor;

    private ClaimsPrincipal Principal =>
        _accessor.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());

    public bool IsAuthenticated => Principal.Identity?.IsAuthenticated == true;

    public string UserId => Principal.FindFirst(ChoClaimTypes.Subject)?.Value
        ?? throw new UnauthorizedAccessException("No authenticated actor on this request.");

    public string? DisplayName => Principal.FindFirst(ChoClaimTypes.Name)?.Value;

    public string? Email => Principal.FindFirst(ChoClaimTypes.Email)?.Value;

    public string TenantId => _accessor.HttpContext?.Items["TenantId"] as string
        ?? throw new UnauthorizedAccessException("No tenant context on this request.");

    public bool IsService => ChoPrincipal.IsService(Principal);

    public IReadOnlyCollection<string> Roles => ChoPrincipal.Roles(Principal);

    public bool HasPermission(string permission) => ChoPrincipal.HasPermission(Principal, permission);
}

/// <summary>Claim reading rules shared by the actor, the policy handler and tests.</summary>
public static class ChoPrincipal
{
    /// <summary>Set by authentication when the issuer may mint service tokens.</summary>
    internal const string ServiceIssuerMarker = "cho_service_issuer";

    public static IReadOnlyCollection<string> Roles(ClaimsPrincipal principal)
        => principal.FindAll(ChoClaimTypes.Role).Select(c => c.Value).ToArray();

    /// <summary>
    /// A service identity requires both the reserved role and an issuer trusted
    /// to mint it. A user-token issuer writing <c>cho.service</c> gets nothing.
    /// </summary>
    public static bool IsService(ClaimsPrincipal principal)
        => principal.HasClaim(ChoClaimTypes.Role, ChoServiceRole.Name)
           && principal.HasClaim(ServiceIssuerMarker, "true");

    public static bool HasPermission(ClaimsPrincipal principal, string permission)
    {
        if (principal.Identity?.IsAuthenticated != true)
            return false;

        // Services act on behalf of the platform pipeline; user-level checks
        // were applied where the work entered the system.
        if (IsService(principal))
            return true;

        var explicitPermissions = principal.FindAll(ChoClaimTypes.Permission).Select(c => c.Value).ToList();
        var granted = explicitPermissions.Count > 0
            ? explicitPermissions
            : ChoRolePermissions.Expand(Roles(principal)).ToList();

        return ChoRolePermissions.Satisfies(granted, permission);
    }
}
