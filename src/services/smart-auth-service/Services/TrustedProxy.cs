using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace SmartAuthService.Services;

/// <summary>
/// TLS ends at the ingress, so every request reaches the pod as plain HTTP.
/// OpenIddict refuses non-HTTPS requests to its endpoints (and the cookies are
/// Secure), so the scheme the client actually used must come from the
/// ingress's <c>X-Forwarded-Proto</c>.
///
/// Only that header is honoured, and only from the networks listed in
/// <c>SmartAuth:TrustedProxyNetworks</c> (the ingress controller's pod or node
/// CIDRs). Nothing else is taken from forwarded headers: the issuer, discovery
/// URLs and the external login's redirect URI are fixed by configuration, and
/// the client address is not used for any decision. With no networks listed,
/// no forwarded header is trusted from anyone.
/// </summary>
public static class TrustedProxy
{
    public const string ConfigKey = "SmartAuth:TrustedProxyNetworks";

    public static IServiceCollection AddSmartTrustedProxy(this IServiceCollection services, IConfiguration configuration)
    {
        var networks = Parse(configuration.GetSection(ConfigKey).Get<string[]>() ?? []);

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            // Empty proxy lists mean "any sender" to the middleware, so with no
            // networks configured nothing is honoured at all.
            options.ForwardedHeaders = networks.Count > 0 ? ForwardedHeaders.XForwardedProto : ForwardedHeaders.None;
            options.KnownProxies.Clear();
            options.KnownNetworks.Clear();
            foreach (var network in networks)
                options.KnownNetworks.Add(network);
        });
        return services;
    }

    /// <summary>Parses CIDR entries; an invalid one fails startup rather than being ignored.</summary>
    public static IReadOnlyList<Microsoft.AspNetCore.HttpOverrides.IPNetwork> Parse(IEnumerable<string> entries)
    {
        var networks = new List<Microsoft.AspNetCore.HttpOverrides.IPNetwork>();
        foreach (var raw in entries.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()))
        {
            var parts = raw.Split('/');
            if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address)
                || !int.TryParse(parts[1], out var prefix) || prefix < 0
                || prefix > (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32))
            {
                throw new InvalidOperationException(
                    $"{ConfigKey} entry '{raw}' is not a CIDR network (for example 10.244.0.0/16).");
            }
            networks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(address, prefix));
        }
        return networks;
    }
}
