using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace CloudHealthOffice.Infrastructure.Security;

/// <summary>
/// Settings under <c>ChoAuth:Outbound</c> that name the hosts a service may send
/// CHO credentials to.
///
/// <code>
/// "ChoAuth": {
///   "Outbound": {
///     "Hosts":            [ "claims-service", "tenant-service" ],
///     "Suffixes":         [ ".cho-staging.svc.cluster.local" ],
///     "ExcludedServices": [ "Nppes" ],
///     "DevelopmentHosts": [ "localhost" ]
///   }
/// }
/// </code>
/// </summary>
public sealed class ChoOutboundOptions
{
    public const string SectionName = ChoAuthOptions.SectionName + ":Outbound";

    /// <summary>
    /// Suffixes that always name a CHO service: the <c>cloudhealthoffice</c>
    /// namespace, by its short and cluster-qualified names.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultSuffixes =
    [
        ".cloudhealthoffice",
        ".cloudhealthoffice.svc",
        ".cloudhealthoffice.svc.cluster.local",
    ];

    /// <summary><c>Services:*</c> entries that are never CHO backend services.</summary>
    public static readonly IReadOnlyList<string> DefaultExcludedServices =
    [
        "TokenService",
        "ArgoWorkflows",
        "Prometheus",
    ];

    /// <summary>Exact host names (no scheme, no port) of CHO services this service calls.</summary>
    public List<string> Hosts { get; set; } = new();

    /// <summary>Host suffixes, in addition to <see cref="DefaultSuffixes"/>, that name CHO services.</summary>
    public List<string> Suffixes { get; set; } = new();

    /// <summary><c>Services:*</c> keys, in addition to <see cref="DefaultExcludedServices"/>, whose host is not a CHO service.</summary>
    public List<string> ExcludedServices { get; set; } = new();

    /// <summary>
    /// Exact hosts honoured only in the Development and Testing environments
    /// (docker-compose service names, <c>localhost</c>). Ignored everywhere else.
    /// </summary>
    public List<string> DevelopmentHosts { get; set; } = new();
}

/// <summary>
/// Decides whether a URL names a CHO service, and so may receive a CHO token
/// and <c>X-Tenant-ID</c>. A host is a CHO host only when it is:
/// <list type="bullet">
///   <item>listed in <c>ChoAuth:Outbound:Hosts</c> (or, in Development/Testing,
///   <c>ChoAuth:Outbound:DevelopmentHosts</c>);</item>
///   <item>under one of <see cref="ChoOutboundOptions.DefaultSuffixes"/> or
///   <c>ChoAuth:Outbound:Suffixes</c>; or</item>
///   <item>the host of a <c>Services:*</c> URL whose key is not excluded.</item>
/// </list>
/// IP literals, <c>localhost</c> and other loopback names count only when
/// listed in Hosts/DevelopmentHosts: never by suffix and never because a
/// <c>Services:*</c> URL points at them. Nothing is inferred from the shape of a
/// host name (a dot-less name is not "internal").
/// </summary>
public sealed class ChoOutboundHosts
{
    private readonly HashSet<string> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _suffixes;

    public ChoOutboundHosts(IConfiguration configuration, IHostEnvironment? environment = null)
    {
        var options = new ChoOutboundOptions();
        configuration.GetSection(ChoOutboundOptions.SectionName).Bind(options);

        foreach (var host in options.Hosts)
            AddExact(host);

        if (environment != null && ChoAuthenticationExtensions.AllowsSymmetricKeys(environment))
        {
            foreach (var host in options.DevelopmentHosts)
                AddExact(host);
        }

        _suffixes = ChoOutboundOptions.DefaultSuffixes
            .Concat(options.Suffixes)
            .Select(s => s.Trim())
            .Where(s => s.Length > 1)
            .Select(s => s.StartsWith('.') ? s : "." + s)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var excluded = new HashSet<string>(
            ChoOutboundOptions.DefaultExcludedServices.Concat(options.ExcludedServices), StringComparer.OrdinalIgnoreCase);
        foreach (var entry in configuration.GetSection("Services").GetChildren())
        {
            if (excluded.Contains(entry.Key))
                continue;
            if (Uri.TryCreate(entry.Value, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                && !IsLoopbackOrIp(uri.Host))
            {
                _hosts.Add(Normalize(uri.Host));
            }
        }
    }

    /// <summary>The exact hosts admitted (for diagnostics and tests).</summary>
    public IReadOnlyCollection<string> ExactHosts => _hosts;

    /// <summary>Whether <paramref name="uri"/> names a CHO service.</summary>
    public bool IsChoService(Uri? uri)
    {
        if (uri == null || !uri.IsAbsoluteUri
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return false;

        var host = Normalize(uri.Host);
        if (_hosts.Contains(host))
            return true;

        if (IsLoopbackOrIp(host))
            return false;

        return _suffixes.Any(s => host.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }

    private void AddExact(string? host)
    {
        if (!string.IsNullOrWhiteSpace(host))
            _hosts.Add(Normalize(host.Trim()));
    }

    /// <summary>Lower-case, without IPv6 brackets or a trailing root dot.</summary>
    private static string Normalize(string host)
        => host.Trim().TrimStart('[').TrimEnd(']').TrimEnd('.').ToLowerInvariant();

    private static bool IsLoopbackOrIp(string host)
    {
        var h = Normalize(host);
        return h == "localhost"
               || h.EndsWith(".localhost", StringComparison.Ordinal)
               || IPAddress.TryParse(h, out _);
    }
}
