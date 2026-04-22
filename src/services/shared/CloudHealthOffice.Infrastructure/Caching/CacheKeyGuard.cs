using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace CloudHealthOffice.Infrastructure.Caching;

/// <summary>
/// Validates and rewrites cache keys before they hit the backend.
///
/// Two compliance controls:
///   1. <b>Tenant prefixing</b> — every key becomes
///      <c>{env}:{tenantId}:{logicalKey}</c>. Cross-tenant cache pollution
///      has been a real incident class in multi-tenant platforms; this
///      guard makes it unreachable by construction. Global scope
///      (<see cref="CacheScope.Global"/>) is the explicit opt-out.
///   2. <b>PHI rejection</b> — cache keys surface in Redis SLOWLOG, ops
///      dashboards, traces, and monitoring. Raw PHI in a key is a
///      compliance issue even if the value is hashed. Tokens like
///      <c>ssn</c>, <c>mbi</c>, <c>dob</c>, <c>memberId</c>, <c>patientId</c>,
///      <c>ssnHash</c> trigger an <see cref="ArgumentException"/> at
///      runtime — fail fast, not silently. The hashed form
///      <c>memberIdHash</c> is permitted (hashes of member IDs are not
///      PHI under CHO's current risk model); <c>ssnHash</c> is not.
/// </summary>
public sealed class CacheKeyGuard
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly string _envPrefix;

    // Token blocklist — matched case-insensitively against each token in
    // the logical key (tokens are bounded by colons, hyphens, underscores,
    // dots, or slashes). `memberId` rejects both raw and colon-qualified
    // uses without also rejecting the permitted `memberIdHash`.
    private static readonly HashSet<string> PhiTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "ssn", "mbi", "dob", "memberid", "patientid", "ssnhash"
    };

    private static readonly char[] Separators = { ':', '-', '_', '.', '/' };

    public CacheKeyGuard(IHttpContextAccessor httpContextAccessor, IHostEnvironment env)
    {
        _httpContextAccessor = httpContextAccessor;
        _envPrefix           = env.EnvironmentName.ToLowerInvariant();
    }

    /// <summary>
    /// Validate <paramref name="logicalKey"/> and return the final Redis
    /// key. Throws <see cref="ArgumentException"/> if the key contains
    /// a PHI token, whitespace, a newline, or a null character — or if
    /// <paramref name="scope"/> is <see cref="CacheScope.Tenant"/> and no
    /// tenant is resolvable from the ambient HttpContext.
    /// </summary>
    public string Build(string logicalKey, CacheScope scope = CacheScope.Tenant)
    {
        if (string.IsNullOrEmpty(logicalKey))
            throw new ArgumentException("Cache key cannot be null or empty.", nameof(logicalKey));

        ValidateShape(logicalKey);
        ValidateNoPhi(logicalKey);

        var tenant = scope == CacheScope.Global
            ? "_global"
            : ResolveTenantOrThrow();

        return $"{_envPrefix}:{tenant}:{logicalKey}";
    }

    /// <summary>Bulk-apply <see cref="Build"/> to a collection of keys.</summary>
    public IReadOnlyCollection<string> BuildMany(
        IReadOnlyCollection<string> logicalKeys, CacheScope scope = CacheScope.Tenant)
    {
        var output = new List<string>(logicalKeys.Count);
        foreach (var k in logicalKeys) output.Add(Build(k, scope));
        return output;
    }

    /// <summary>
    /// Return just the prefix the guard would prepend for a given scope —
    /// <c>{env}:{tenantId}:</c> or <c>{env}:_global:</c>. Consumers that
    /// need to construct a Redis SCAN pattern against already-stored
    /// (prefixed) keys use this to anchor the pattern on the correct
    /// environment + scope, instead of a leading-wildcard <c>"*..."</c>
    /// pattern that would force a full-keyspace SCAN.
    /// </summary>
    public string BuildPrefix(CacheScope scope = CacheScope.Tenant)
    {
        var tenant = scope == CacheScope.Global ? "_global" : ResolveTenantOrThrow();
        return $"{_envPrefix}:{tenant}:";
    }

    private static void ValidateShape(string key)
    {
        foreach (var c in key)
        {
            if (c == '\0' || c == '\r' || c == '\n' || char.IsWhiteSpace(c))
                // Do NOT echo the raw key. ExceptionHandlingMiddleware logs
                // exception.Message in production and may surface it to
                // clients in Development; a rejected key can contain PHI
                // (that's why we're rejecting it). Describe the problem
                // without quoting the input.
                throw new ArgumentException(
                    "Cache key contains whitespace, newline, or null characters. " +
                    "Keys must be printable ASCII; whitespace is disallowed to prevent " +
                    "log-forging via user-controlled cache keys.",
                    nameof(key));
        }
    }

    private static void ValidateNoPhi(string key)
    {
        var tokens = key.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            if (PhiTokens.Contains(token))
                // Token name is not PHI (it's the identifier class, not a
                // value); safe to include. The surrounding key value is
                // NOT included — see docs/architecture/shared-cache.md.
                throw new ArgumentException(
                    $"Cache key contains PHI token '{token.ToLowerInvariant()}'. " +
                    "Hash, pseudonymize, or redesign the key. See docs/architecture/shared-cache.md.",
                    nameof(key));
        }
    }

    private string ResolveTenantOrThrow()
    {
        var ctx = _httpContextAccessor.HttpContext;
        if (ctx is not null && ctx.Items.TryGetValue("TenantId", out var raw) && raw is string s && !string.IsNullOrWhiteSpace(s))
            return s;

        throw new InvalidOperationException(
            "CacheKeyGuard: tenant-scoped cache operation requested but no " +
            "TenantId is present on HttpContext.Items. " +
            "Either run inside a request that went through TenantMiddleware, " +
            "or pass CacheScope.Global at the call site.");
    }
}
