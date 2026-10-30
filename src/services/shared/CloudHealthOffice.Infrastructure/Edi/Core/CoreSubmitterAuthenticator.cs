using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CloudHealthOffice.Infrastructure.Middleware;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Edi.Core;

/// <summary>A submitter whose credential checked out, and the tenant its call acts within.</summary>
public sealed record CoreSubmitter(string SenderId, string TenantId, string? Name, CoreAuthenticationMethod Method, IReadOnlyList<string> PayloadTypes);

/// <summary>Why a CORE call was not authenticated. Never says which part of a credential was wrong.</summary>
public sealed record CoreAuthenticationFailure(string Reason);

public interface ICoreSubmitterAuthenticator
{
    /// <summary>
    /// Finds the submitter registration the request's credential belongs to.
    /// A client certificate is checked first, then a bearer token, then the
    /// envelope's username and password. Whichever is used, the registration
    /// must also carry the envelope's SenderID.
    /// </summary>
    CoreSubmitter? Authenticate(HttpContext http, CoreEnvelopeRequest envelope, string requiredPermission, out CoreAuthenticationFailure? failure);
}

public sealed class CoreSubmitterAuthenticator : ICoreSubmitterAuthenticator
{
    private readonly IOptionsMonitor<CoreConnectivityOptions> _options;

    public CoreSubmitterAuthenticator(IOptionsMonitor<CoreConnectivityOptions> options) => _options = options;

    public CoreSubmitter? Authenticate(HttpContext http, CoreEnvelopeRequest envelope, string requiredPermission, out CoreAuthenticationFailure? failure)
    {
        var options = _options.CurrentValue;
        var bySender = options.Submitters
            .Where(s => s.Enabled && string.Equals(s.SenderId, envelope.SenderId, StringComparison.Ordinal))
            .ToList();

        var certificate = ClientCertificate(http, options);
        if (certificate is not null)
        {
            var thumbprint = Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256));
            var now = DateTime.UtcNow;
            if (now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime())
                return Fail("client certificate outside its validity period", out failure);

            var match = bySender.FirstOrDefault(s => s.CertificateThumbprints.Any(t => string.Equals(Normalize(t), thumbprint, StringComparison.Ordinal)));
            return match is null
                ? Fail("client certificate not registered for this SenderID", out failure)
                : Ok(match, CoreAuthenticationMethod.ClientCertificate, out failure);
        }

        if (http.User.Identity?.IsAuthenticated == true)
        {
            if (!options.AllowBearerTokens)
                return Fail("bearer tokens are not accepted", out failure);

            var clientId = http.User.FindFirstValue(ChoClaimTypes.AuthorizedParty) ?? http.User.FindFirstValue("client_id");
            var tokenTenant = http.GetTenantIdOrDefault();
            if (clientId is null || tokenTenant is null)
                return Fail("token has no client or tenant", out failure);
            if (!ChoPrincipal.HasPermission(http.User, requiredPermission))
                return Fail("token lacks the required permission", out failure);

            var match = bySender.FirstOrDefault(s =>
                s.OAuthClientIds.Contains(clientId, StringComparer.Ordinal)
                && string.Equals(s.TenantId, tokenTenant, StringComparison.Ordinal));
            return match is null
                ? Fail("token client not registered for this SenderID", out failure)
                : Ok(match, CoreAuthenticationMethod.BearerToken, out failure);
        }

        if (!string.IsNullOrEmpty(envelope.UserName) && envelope.Password is not null)
        {
            var match = bySender.FirstOrDefault(s =>
                s.UserName is not null
                && CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(s.UserName), System.Text.Encoding.UTF8.GetBytes(envelope.UserName)));
            // Verify even with no match so the response time does not say whether the username exists.
            var verified = CorePasswordHash.Verify(envelope.Password, match?.PasswordHash);
            return match is not null && verified
                ? Ok(match, CoreAuthenticationMethod.UserNamePassword, out failure)
                : Fail("username or password not accepted", out failure);
        }

        return Fail("no credential", out failure);
    }

    private static X509Certificate2? ClientCertificate(HttpContext http, CoreConnectivityOptions options)
    {
        if (http.Connection.ClientCertificate is { } direct)
            return direct;

        if (string.IsNullOrEmpty(options.ClientCertificateHeader)
            || !http.Request.Headers.TryGetValue(options.ClientCertificateHeader, out var header)
            || string.IsNullOrWhiteSpace(header.ToString()))
            return null;

        try
        {
            var pem = Uri.UnescapeDataString(header.ToString());
            return X509Certificate2.CreateFromPem(pem);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or UriFormatException)
        {
            return null;
        }
    }

    private static string Normalize(string thumbprint) =>
        new string(thumbprint.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

    private static CoreSubmitter? Ok(CoreSubmitterRegistration s, CoreAuthenticationMethod method, out CoreAuthenticationFailure? failure)
    {
        failure = null;
        return new CoreSubmitter(s.SenderId, s.TenantId, s.Name, method, s.PayloadTypes);
    }

    private static CoreSubmitter? Fail(string reason, out CoreAuthenticationFailure? failure)
    {
        failure = new CoreAuthenticationFailure(reason);
        return null;
    }
}

/// <summary>
/// PBKDF2-SHA256 password hashes for CORE submitter registrations, in the
/// form <c>pbkdf2-sha256$iterations$base64salt$base64hash</c>.
/// </summary>
public static class CorePasswordHash
{
    private const string Prefix = "pbkdf2-sha256";
    private const int DefaultIterations = 210_000;

    // A fixed hash to verify against when no registration matched, so a miss costs the same as a hit.
    private static readonly string Dummy = Create("dummy-password-for-timing", iterations: DefaultIterations);

    public static string Create(string password, int iterations = DefaultIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        return $"{Prefix}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string? stored)
    {
        var parts = stored?.Split('$');
        var usable = parts is { Length: 4 } && parts[0] == Prefix && int.TryParse(parts[1], out var n) && n > 0;
        if (!usable)
            parts = Dummy.Split('$');

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts![2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            salt = Convert.FromBase64String(Dummy.Split('$')[2]);
            expected = Convert.FromBase64String(Dummy.Split('$')[3]);
            usable = false;
        }

        var iterations = int.Parse(parts![1], System.Globalization.CultureInfo.InvariantCulture);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected) && usable;
    }
}
