using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace TenantService.CpcdsSftp;

/// <summary>
/// The hosted CPCDS drop's SFTP server (SFTPGo on GKE, deploy/gcp/sftpgo).
/// See docs/integration/CPCDS-SFTP-DROP-GCP.md.
/// </summary>
public sealed class CpcdsSftpOptions
{
    public const string SectionName = "CpcdsSftp";

    /// <summary>Off unless the hosted drop runs in this deployment.</summary>
    public bool Enabled { get; set; }

    /// <summary>SFTPGo's cluster-internal REST API, e.g. <c>http://sftpgo-api.cho-sftp:8080</c>.</summary>
    public string ApiBaseUrl { get; set; } = "http://sftpgo-api.cho-sftp:8080";

    /// <summary>From the <c>sftpgo-admin</c> secret apply.sh creates.</summary>
    public string AdminUsername { get; set; } = string.Empty;

    public string AdminPassword { get; set; } = string.Empty;

    /// <summary>The drop bucket each plan's filesystem is a prefix of (Terraform output cpcds_drop_bucket).</summary>
    public string Bucket { get; set; } = string.Empty;

    /// <summary>What plans connect to (the static address, or a DNS name for it).</summary>
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 22;

    /// <summary>The most keys one plan may register (a primary and a rotation key, typically).</summary>
    public int MaxKeys { get; set; } = 5;
}

/// <summary>A plan's SFTP login as the API reports it. Never a secret: there is none.</summary>
public sealed class CpcdsSftpAccount
{
    public string TenantId { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public bool Enabled { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Username { get; set; } = string.Empty;
    public string InboundFolder { get; set; } = "/inbound";
    public IReadOnlyList<string> ReadOnlyFolders { get; set; } = ["/processed", "/failed", "/outbound"];
    public IReadOnlyList<string> KeyFingerprints { get; set; } = [];
}

public interface ICpcdsSftpAccounts
{
    Task<CpcdsSftpAccount> GetAsync(string tenantId, CancellationToken ct = default);

    /// <summary>Creates the tenant's login, or replaces its keys and enables it.</summary>
    Task<CpcdsSftpAccount> UpsertAsync(string tenantId, IReadOnlyList<SshPublicKey> keys, CancellationToken ct = default);

    /// <summary>Turns the login off; files already uploaded stay where they are.</summary>
    Task<CpcdsSftpAccount> DisableAsync(string tenantId, CancellationToken ct = default);
}

/// <summary>The SFTP server could not be reached or refused the change.</summary>
public sealed class CpcdsSftpUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// <see cref="ICpcdsSftpAccounts"/> over SFTPGo's REST API. Each tenant is one
/// SFTPGo user named after the tenant, whose filesystem is the drop bucket
/// under <c>{tenant}/</c>: <c>/inbound</c> read-write, <c>/processed</c>,
/// <c>/failed</c> and <c>/outbound</c> (the extracts CHO delivers to the plan)
/// read-only, nothing else. Key sign-in only. A login created before
/// <c>/outbound</c> existed gets it the next time its keys are saved.
///
/// <para>
/// The admin credential stays inside this class: it buys a short-lived API
/// token per call (<c>GET /api/v2/token</c>), and that token is sent only to
/// <see cref="CpcdsSftpOptions.ApiBaseUrl"/>. This client is not one of the
/// factory clients that carry CHO tokens.
/// </para>
/// </summary>
public sealed class SftpGoCpcdsSftpAccounts : ICpcdsSftpAccounts
{
    public const string HttpClientName = "SftpGoApi";

    /// <summary>SFTPGo usernames and the drop's folder names both accept these.</summary>
    internal static readonly Regex TenantIdPattern = new("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IHttpClientFactory _http;
    private readonly CpcdsSftpOptions _options;

    public SftpGoCpcdsSftpAccounts(IHttpClientFactory http, IOptions<CpcdsSftpOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public async Task<CpcdsSftpAccount> GetAsync(string tenantId, CancellationToken ct = default)
    {
        Validate(tenantId);
        var (client, _) = await ClientAsync(ct);
        using (client)
            return Describe(tenantId, await ReadUserAsync(client, tenantId, ct));
    }

    public async Task<CpcdsSftpAccount> UpsertAsync(string tenantId, IReadOnlyList<SshPublicKey> keys, CancellationToken ct = default)
    {
        Validate(tenantId);
        if (keys.Count == 0)
            throw new ArgumentException("At least one public key is required.", nameof(keys));

        var (client, _) = await ClientAsync(ct);
        using (client)
        {
            var existing = await ReadUserAsync(client, tenantId, ct);
            var user = UserFor(tenantId, keys);

            using var response = existing is null
                ? await client.PostAsJsonAsync("api/v2/users", user, Json, ct)
                : await client.PutAsJsonAsync($"api/v2/users/{Uri.EscapeDataString(tenantId)}", user, Json, ct);
            await EnsureSuccessAsync(response, existing is null ? "create" : "update");

            return Describe(tenantId, await ReadUserAsync(client, tenantId, ct));
        }
    }

    public async Task<CpcdsSftpAccount> DisableAsync(string tenantId, CancellationToken ct = default)
    {
        Validate(tenantId);
        var (client, _) = await ClientAsync(ct);
        using (client)
        {
            var existing = await ReadUserAsync(client, tenantId, ct);
            if (existing is null)
                return Describe(tenantId, null);

            existing.Status = 0;
            using var response = await client.PutAsJsonAsync(
                $"api/v2/users/{Uri.EscapeDataString(tenantId)}", existing, Json, ct);
            await EnsureSuccessAsync(response, "disable");
            return Describe(tenantId, await ReadUserAsync(client, tenantId, ct));
        }
    }

    /// <summary>The SFTPGo user for a tenant: the drop's layout and nothing more.</summary>
    internal SftpGoUser UserFor(string tenantId, IReadOnlyList<SshPublicKey> keys) => new()
    {
        Username = tenantId,
        Status = 1,
        PublicKeys = keys.Select(k => k.Line).Distinct(StringComparer.Ordinal).ToList(),
        HomeDir = $"/var/lib/sftpgo/users/{tenantId}",
        Permissions = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["/"] = ["list"],
            ["/inbound"] = ["list", "upload", "overwrite", "rename", "delete"],
            ["/processed"] = ["list", "download"],
            ["/failed"] = ["list", "download"],
            // Warehouse extracts claims-service writes for the plan to collect.
            ["/outbound"] = ["list", "download"],
        },
        Filesystem = new SftpGoFilesystem
        {
            Provider = 2,
            GcsConfig = new SftpGoGcsConfig { Bucket = _options.Bucket, KeyPrefix = tenantId + "/", AutomaticCredentials = 1 },
        },
        Filters = new SftpGoFilters { DeniedLoginMethods = ["password", "keyboard-interactive"] },
        Description = "CHO CPCDS drop",
    };

    private CpcdsSftpAccount Describe(string tenantId, SftpGoUser? user) => new()
    {
        TenantId = tenantId,
        Exists = user is not null,
        Enabled = user?.Status == 1,
        Host = _options.Host,
        Port = _options.Port,
        Username = tenantId,
        KeyFingerprints = (user?.PublicKeys ?? [])
            .Select(line => SshPublicKey.Parse(line).Key?.Fingerprint)
            .OfType<string>()
            .ToList(),
    };

    private static async Task<SftpGoUser?> ReadUserAsync(HttpClient client, string tenantId, CancellationToken ct)
    {
        using var response = await client.GetAsync($"api/v2/users/{Uri.EscapeDataString(tenantId)}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, "read");
        return await response.Content.ReadFromJsonAsync<SftpGoUser>(Json, ct);
    }

    /// <summary>A client bearing a fresh API token bought with the admin credential.</summary>
    private async Task<(HttpClient Client, string Token)> ClientAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
            throw new CpcdsSftpUnavailableException("The hosted CPCDS SFTP drop is not enabled in this deployment.");

        var client = _http.CreateClient(HttpClientName);
        client.BaseAddress = new Uri(_options.ApiBaseUrl.TrimEnd('/') + "/");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/v2/token");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.AdminUsername}:{_options.AdminPassword}")));
            using var response = await client.SendAsync(request, ct);
            await EnsureSuccessAsync(response, "authenticate");
            var token = (await response.Content.ReadFromJsonAsync<SftpGoToken>(Json, ct))?.AccessToken;
            if (string.IsNullOrEmpty(token))
                throw new CpcdsSftpUnavailableException("The SFTP server issued no API token.");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return (client, token);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string action)
    {
        if (response.IsSuccessStatusCode)
            return;
        // SFTPGo's error bodies name the field at fault and never echo a credential.
        var body = await response.Content.ReadAsStringAsync();
        if (body.Length > 300) body = body[..300];
        throw new CpcdsSftpUnavailableException(
            $"The SFTP server refused to {action} the login (HTTP {(int)response.StatusCode}): {body}");
    }

    private static void Validate(string tenantId)
    {
        if (!TenantIdPattern.IsMatch(tenantId))
            throw new ArgumentException("This tenant's id cannot name an SFTP login or drop folder.", nameof(tenantId));
    }
}

internal sealed class SftpGoToken
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
}

/// <summary>The parts of SFTPGo's user object CHO sets. Unknown fields are kept on a read-modify-write.</summary>
internal sealed class SftpGoUser
{
    [JsonPropertyName("username")] public string Username { get; set; } = string.Empty;
    [JsonPropertyName("status")] public int Status { get; set; }
    [JsonPropertyName("public_keys")] public List<string>? PublicKeys { get; set; }
    [JsonPropertyName("home_dir")] public string? HomeDir { get; set; }
    [JsonPropertyName("permissions")] public Dictionary<string, List<string>>? Permissions { get; set; }
    [JsonPropertyName("filesystem")] public SftpGoFilesystem? Filesystem { get; set; }
    [JsonPropertyName("filters")] public SftpGoFilters? Filters { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

internal sealed class SftpGoFilesystem
{
    [JsonPropertyName("provider")] public int Provider { get; set; }
    [JsonPropertyName("gcsconfig")] public SftpGoGcsConfig? GcsConfig { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

internal sealed class SftpGoGcsConfig
{
    [JsonPropertyName("bucket")] public string Bucket { get; set; } = string.Empty;
    [JsonPropertyName("key_prefix")] public string KeyPrefix { get; set; } = string.Empty;
    [JsonPropertyName("automatic_credentials")] public int AutomaticCredentials { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

internal sealed class SftpGoFilters
{
    [JsonPropertyName("denied_login_methods")] public List<string>? DeniedLoginMethods { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
