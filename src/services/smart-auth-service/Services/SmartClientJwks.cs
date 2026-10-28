using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Logging;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;

namespace SmartAuthService.Services;

/// <summary>
/// The public keys a SMART backend client authenticates with (G-42,
/// <c>private_key_jwt</c>). A tenant administrator registers them either as a
/// JWK Set (<c>jwks</c>) or as the client's own <c>jwks_uri</c>, which this
/// server fetches and caches so the client can rotate keys without a
/// re-registration.
///
/// Only public signing keys are accepted: a set with any private parameter
/// (<c>d</c>, the RSA CRT values, or a symmetric <c>k</c>) is refused outright
/// rather than stripped, because a client that pasted its private key has
/// leaked it and must make a new one.
/// </summary>
public static class SmartClientJwks
{
    public const int MaxKeys = 10;
    public const int MaxDocumentBytes = 64 * 1024;
    public const int MinRsaKeyBits = 2048;

    private static readonly string[] PrivateMembers = ["d", "p", "q", "dp", "dq", "qi", "oth", "k"];
    private static readonly string[] Curves = ["P-256", "P-384", "P-521"];

    /// <summary>
    /// Parses and checks a JWK Set. Every key must be an RSA (2048 bits or
    /// more) or EC (P-256/384/521) public key with a <c>kid</c>, for signing.
    /// </summary>
    public static (JsonWebKeySet? Set, string? Error) Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return (null, "The JWK Set is empty.");
        if (json.Length > MaxDocumentBytes)
            return (null, $"The JWK Set is larger than {MaxDocumentBytes / 1024} KB.");

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(json).RootElement;
        }
        catch (JsonException)
        {
            return (null, "The JWK Set is not valid JSON.");
        }
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Array)
            return (null, "A JWK Set is an object with a \"keys\" array.");
        if (keys.GetArrayLength() == 0)
            return (null, "The JWK Set has no keys.");
        if (keys.GetArrayLength() > MaxKeys)
            return (null, $"The JWK Set has more than {MaxKeys} keys.");

        var kids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys.EnumerateArray())
        {
            if (key.ValueKind != JsonValueKind.Object)
                return (null, "Every entry of \"keys\" must be a JWK object.");
            foreach (var member in PrivateMembers)
            {
                if (key.TryGetProperty(member, out _))
                    return (null, "The JWK Set contains private or symmetric key material. Register public keys only, and replace the exposed key.");
            }

            var kid = String(key, "kid");
            if (string.IsNullOrEmpty(kid))
                return (null, "Every key needs a \"kid\".");
            if (!kids.Add(kid))
                return (null, $"The kid '{kid}' appears more than once.");

            var use = String(key, "use");
            if (use is not null && use != "sig")
                return (null, $"Key '{kid}' has use '{use}'; client authentication keys are for signing (\"sig\").");

            switch (String(key, "kty"))
            {
                case "RSA":
                    var modulus = String(key, "n");
                    if (string.IsNullOrEmpty(modulus) || string.IsNullOrEmpty(String(key, "e")))
                        return (null, $"RSA key '{kid}' needs \"n\" and \"e\".");
                    if (!TryBase64UrlLength(modulus, out var bytes) || bytes * 8 < MinRsaKeyBits)
                        return (null, $"RSA key '{kid}' must be at least {MinRsaKeyBits} bits.");
                    break;
                case "EC":
                    if (!Curves.Contains(String(key, "crv")))
                        return (null, $"EC key '{kid}' must use P-256, P-384 or P-521.");
                    if (string.IsNullOrEmpty(String(key, "x")) || string.IsNullOrEmpty(String(key, "y")))
                        return (null, $"EC key '{kid}' needs \"x\" and \"y\".");
                    break;
                default:
                    return (null, $"Key '{kid}' must be an RSA or EC key.");
            }
        }

        try
        {
            return (new JsonWebKeySet(json), null);
        }
        catch (ArgumentException)
        {
            return (null, "The JWK Set could not be read.");
        }
    }

    /// <summary>
    /// A client's <c>jwks_uri</c>: absolute HTTPS, no user info or fragment.
    /// Where it may point (no private or loopback addresses) is enforced when
    /// it is fetched, by <see cref="SmartJwksFetcher"/>.
    /// </summary>
    public static bool IsAcceptableJwksUri(string? raw, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(raw?.Trim(), UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || !string.IsNullOrEmpty(parsed.Fragment))
            return false;
        uri = parsed;
        return true;
    }

    private static string? String(JsonElement key, string name)
        => key.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool TryBase64UrlLength(string value, out int bytes)
    {
        bytes = 0;
        try
        {
            bytes = Base64UrlEncoder.DecodeBytes(value).SkipWhile(b => b == 0).Count();
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>
/// Fetches and caches clients' <c>jwks_uri</c> documents.
///
/// A set is reused for <see cref="FreshFor"/>. An assertion naming a
/// <c>kid</c> the cached set lacks triggers one refetch (no more often than
/// <see cref="RefetchAfter"/>), which is how a client's key rotation is picked
/// up. If a refetch fails, the last good set is kept for
/// <see cref="StaleFor"/> so a brief outage at the client does not stop its
/// tokens; after that the client cannot authenticate until its keys are
/// reachable again.
///
/// The fetch refuses to connect to loopback, private, link-local or other
/// non-public addresses (checked on the resolved address, at connect time),
/// does not follow redirects, and reads at most 64 KB.
/// </summary>
public sealed class SmartJwksFetcher
{
    public const string HttpClientName = "smart-client-jwks";
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RefetchAfter = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan StaleFor = TimeSpan.FromHours(1);

    private readonly IHttpClientFactory _http;
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _time;
    private readonly ILogger<SmartJwksFetcher> _logger;

    public SmartJwksFetcher(IHttpClientFactory http, IMemoryCache cache, ILogger<SmartJwksFetcher> logger, TimeProvider? time = null)
    {
        _http = http;
        _cache = cache;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Fetches and checks the set now, bypassing the cache (registration).</summary>
    public async Task<(JsonWebKeySet? Set, string? Error)> FetchAsync(Uri uri, CancellationToken ct)
    {
        string body;
        try
        {
            using var response = await _http.CreateClient(HttpClientName).GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return (null, $"The jwks_uri answered HTTP {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength > SmartClientJwks.MaxDocumentBytes)
                return (null, "The jwks_uri document is too large.");
            body = await ReadLimitedAsync(response.Content, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            // The exception text can carry what an internal host said; keep it
            // in the service log, not in the administrator's response.
            _logger.LogWarning(ex, "SMART client jwks_uri fetch failed: {Uri}", LogSanitizer.Sanitize(uri.AbsoluteUri));
            return (null, "The jwks_uri could not be fetched.");
        }

        var (set, error) = SmartClientJwks.Parse(body);
        if (set is not null)
            _cache.Set(CacheKey(uri), new CachedSet(set, _time.GetUtcNow()), StaleFor);
        return (set, error);
    }

    /// <summary>The client's current keys, from cache where fresh enough. Null when none can be had.</summary>
    public async Task<JsonWebKeySet?> GetAsync(Uri uri, string? kid, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        _cache.TryGetValue(CacheKey(uri), out CachedSet? cached);
        if (cached is not null)
        {
            var age = now - cached.FetchedAt;
            var knowsKid = kid is null || cached.Set.Keys.Any(k => k.Kid == kid);
            if ((age < FreshFor && knowsKid) || age < RefetchAfter)
                return cached.Set;
        }

        var (set, _) = await FetchAsync(uri, ct);
        if (set is not null)
            return set;
        return cached is not null && now - cached.FetchedAt < StaleFor ? cached.Set : null;
    }

    private static string CacheKey(Uri uri) => "smart-jwks:" + uri.AbsoluteUri;

    private static async Task<string> ReadLimitedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        var buffer = new byte[SmartClientJwks.MaxDocumentBytes + 1];
        var read = 0;
        int n;
        while (read < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(read), ct)) > 0)
            read += n;
        if (read > SmartClientJwks.MaxDocumentBytes)
            throw new InvalidDataException("jwks_uri document exceeds the size limit.");
        return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
    }

    private sealed record CachedSet(JsonWebKeySet Set, DateTimeOffset FetchedAt);

    /// <summary>
    /// The handler behind <see cref="HttpClientName"/>: no redirects, a short
    /// timeout, and a connect callback that refuses non-public addresses
    /// unless <paramref name="allowPrivateNetworks"/> (Development only).
    /// </summary>
    public static SocketsHttpHandler CreateHandler(bool allowPrivateNetworks) => new()
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            var allowed = addresses.Where(a => allowPrivateNetworks || IsPublic(a)).ToArray();
            if (allowed.Length == 0)
                throw new HttpRequestException($"jwks_uri host '{context.DnsEndPoint.Host}' does not resolve to a public address.");

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    /// <summary>False for loopback, private, link-local, CGNAT, unique-local and unspecified addresses.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.Broadcast))
            return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 10
                     || b[0] == 0
                     || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                     || (b[0] == 192 && b[1] == 168)
                     || (b[0] == 169 && b[1] == 254)
                     || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                     || b[0] >= 224);
        }

        return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
                 || (address.GetAddressBytes()[0] & 0xFE) == 0xFC);
    }
}
