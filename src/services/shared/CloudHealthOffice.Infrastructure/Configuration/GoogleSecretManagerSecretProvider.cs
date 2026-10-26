using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Infrastructure.Configuration;

/// <summary>
/// Google Secret Manager REST adapter. Uses ADC (GKE Workload Identity in the cluster),
/// exact secret-name allowlisting and CRC32C verification. Never discovers project secrets,
/// logs payloads, accepts credential files through configuration, or falls back to plaintext.
/// </summary>
public sealed class GoogleSecretManagerSecretProvider : ISecretProvider, IDisposable
{
    public const string ClientName = "GoogleSecretManager";
    private const string CloudPlatformScope = "https://www.googleapis.com/auth/cloud-platform";
    private static readonly Regex Project = new("^(?:[a-z][a-z0-9-]{4,28}[a-z0-9]|[0-9]{6,20})$", RegexOptions.CultureInvariant);
    private static readonly Regex Secret = new("^[A-Za-z0-9_-]{1,255}$", RegexOptions.CultureInvariant);
    private static readonly Regex Version = new("^(?:[1-9][0-9]{0,18}|[a-z][a-z0-9_-]{0,62})$", RegexOptions.CultureInvariant);
    private readonly string _project;
    private readonly HashSet<string> _allowed;
    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<string>> _token;
    private readonly ILogger<GoogleSecretManagerSecretProvider> _logger;
    private readonly SemaphoreSlim _credentialGate = new(1, 1);
    private GoogleCredential? _credential;

    public GoogleSecretManagerSecretProvider(SecretProviderOptions options, IHttpClientFactory clients,
        ILogger<GoogleSecretManagerSecretProvider> logger)
        : this(options, clients.CreateClient(ClientName), null, logger) { }

    internal GoogleSecretManagerSecretProvider(SecretProviderOptions options, HttpClient http,
        Func<CancellationToken, Task<string>>? token, ILogger<GoogleSecretManagerSecretProvider> logger)
    {
        if (options.GoogleSecretManagerProjectId is not { } project || !Project.IsMatch(project))
            throw new InvalidOperationException("SecretProvider:GoogleSecretManagerProjectId must name a Google project.");
        if (options.GoogleSecretManagerSecretNames.Count == 0 || options.GoogleSecretManagerSecretNames.Any(n => n is null || !Secret.IsMatch(n)))
            throw new InvalidOperationException("SecretProvider:GoogleSecretManagerSecretNames must explicitly list valid secret names.");
        _project = project;
        _allowed = new(options.GoogleSecretManagerSecretNames, StringComparer.Ordinal);
        _http = http;
        _http.BaseAddress = new Uri("https://secretmanager.googleapis.com/v1/");
        _token = token ?? DefaultTokenAsync;
        _logger = logger;
    }

    private async Task<string> DefaultTokenAsync(CancellationToken ct)
    {
        if (_credential is null)
        {
            await _credentialGate.WaitAsync(ct);
            try { _credential ??= (await GoogleCredential.GetApplicationDefaultAsync(ct)).CreateScoped(CloudPlatformScope); }
            finally { _credentialGate.Release(); }
        }
        return await ((ITokenAccess)_credential).GetAccessTokenForRequestAsync(cancellationToken: ct);
    }

    public Task<string?> GetSecretAsync(string secretName, CancellationToken ct = default) =>
        GetSecretByVersionAsync(secretName, "latest", ct);

    public async Task<string?> GetSecretByVersionAsync(string secretName, string version, CancellationToken ct = default)
    {
        var path = SecretPath(secretName);
        if (version is null || !Version.IsMatch(version)) throw new ArgumentException("Invalid Google secret version.", nameof(version));
        using var json = await GetAsync($"{path}/versions/{Uri.EscapeDataString(version)}:access", ct);
        if (json is null) return null;
        if (!json.RootElement.TryGetProperty("payload", out var payload)
            || !payload.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String
            || !payload.TryGetProperty("dataCrc32c", out var checksum))
            throw new InvalidDataException("Secret Manager returned no verifiable payload.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(data.GetString()!); }
        catch (FormatException) { throw new InvalidDataException("Secret Manager returned malformed payload encoding."); }
        var checksumText = checksum.ValueKind == JsonValueKind.String ? checksum.GetString() : checksum.GetRawText();
        if (!uint.TryParse(checksumText, NumberStyles.None, CultureInfo.InvariantCulture, out var expected) || Crc32C(bytes) != expected)
            throw new InvalidDataException("Secret Manager payload checksum did not match.");
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { throw new InvalidDataException("Secret Manager payload is not UTF-8 text."); }
    }

    public async Task<IDictionary<string, string>> GetSecretsAsync(string prefix, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in _allowed.Where(n => n.StartsWith(prefix, StringComparison.Ordinal)))
            if (await GetSecretAsync(name, ct) is { } value) result.Add(name, value);
        return result;
    }

    public async Task<IReadOnlyList<SecretVersionInfo>> ListSecretVersionsAsync(string secretName, CancellationToken ct = default)
    {
        var path = SecretPath(secretName);
        var result = new List<SecretVersionInfo>();
        string? page = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            using var json = await GetAsync($"{path}/versions?pageSize=100" +
                (page is null ? "" : "&pageToken=" + Uri.EscapeDataString(page)), ct);
            if (json is null) return result;
            if (json.RootElement.TryGetProperty("versions", out var versions))
            {
                foreach (var item in versions.EnumerateArray())
                {
                    if (item.GetProperty("state").GetString() != "ENABLED") continue;
                    var version = item.GetProperty("name").GetString()!.Split('/')[^1];
                    if (!ulong.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                        throw new InvalidDataException("Secret Manager returned invalid version metadata.");
                    DateTimeOffset? created = item.TryGetProperty("createTime", out var date) ? date.GetDateTimeOffset() : null;
                    result.Add(new(version, created, null, null, true));
                }
            }
            page = json.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
            if (!string.IsNullOrEmpty(page) && !seen.Add(page)) throw new InvalidDataException("Secret Manager repeated a page token.");
        } while (!string.IsNullOrEmpty(page));
        return result.OrderByDescending(v => v.CreatedOn).ToList();
    }

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        try
        {
            foreach (var name in _allowed)
                if (string.IsNullOrEmpty(await GetSecretAsync(name, ct))) return false;
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            _logger.LogWarning("Google Secret Manager health check failed.");
            return false;
        }
    }

    private string SecretPath(string name)
    {
        if (name is null || !_allowed.Contains(name)) throw new ArgumentException("Secret is not in the configured allowlist.", nameof(name));
        return $"projects/{Uri.EscapeDataString(_project)}/secrets/{Uri.EscapeDataString(name)}";
    }

    private async Task<JsonDocument?> GetAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _token(ct));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Google Secret Manager request failed (HTTP {(int)response.StatusCode}).", null, response.StatusCode);
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(body, cancellationToken: ct);
    }

    internal static uint Crc32C(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0x82F63B78u : 0u);
        }
        return ~crc;
    }

    public void Dispose() { _http.Dispose(); _credentialGate.Dispose(); }
}
