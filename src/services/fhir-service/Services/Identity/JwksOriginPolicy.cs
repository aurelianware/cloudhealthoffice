using System.Net;
using System.Net.Sockets;

namespace FhirService.Services.Identity;

/// <summary>
/// Where CHO is willing to fetch trust material from.
///
/// This is the SSRF boundary. The attack it forecloses is not hypothetical: a
/// resource server that resolves keys by reading a token's <c>iss</c> and
/// fetching whatever that host serves has handed an unauthenticated caller both
/// an outbound request primitive against its internal network AND the ability
/// to nominate the key its own token is verified against. Trust-on-first-use is
/// not a weaker version of trust; it is the absence of it.
///
/// So the rule is inverted from "block known-bad": a fetch target is refused
/// unless configuration already named it.
/// </summary>
public static class JwksOriginPolicy
{
    /// <summary>
    /// True when <paramref name="target"/> may be fetched for this issuer:
    /// its own host, or a host an administrator listed in
    /// <see cref="TrustedIssuerOptions.AdditionalJwksHosts"/>.
    /// </summary>
    public static bool IsAllowedHost(Uri target, TrustedIssuerOptions issuer, bool isDevelopmentHost)
    {
        if (!isDevelopmentHost && IsPrivateOrLoopback(target))
            return false;

        var issuerUri = issuer.IssuerUri();
        if (issuerUri != null &&
            string.Equals(target.Host, issuerUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return issuer.AdditionalJwksHosts.Any(
            host => string.Equals(host, target.Host, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Literal loopback, link-local, and RFC1918 targets — the addresses that
    /// make SSRF worth attempting. Only checked for literal IPs: a DNS name
    /// resolving into private space is a network-egress concern that belongs to
    /// the platform, and re-resolving here would be a TOCTOU check that reads
    /// as protection without being any.
    /// </summary>
    public static bool IsPrivateOrLoopback(Uri target)
    {
        if (string.Equals(target.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!IPAddress.TryParse(target.Host.Trim('[', ']'), out var ip))
            return false;

        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var octets = ip.GetAddressBytes();
            return octets[0] switch
            {
                10 => true,                                       // 10.0.0.0/8
                127 => true,                                      // 127.0.0.0/8
                169 when octets[1] == 254 => true,                // 169.254.0.0/16 link-local (IMDS)
                172 when octets[1] >= 16 && octets[1] <= 31 => true, // 172.16.0.0/12
                192 when octets[1] == 168 => true,                // 192.168.0.0/16
                _ => false,
            };
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;

        return false;
    }
}
