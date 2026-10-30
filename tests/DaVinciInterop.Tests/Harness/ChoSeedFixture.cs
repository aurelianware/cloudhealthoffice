using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// The development identity the interop harness presents to CHO, as
/// <c>interop/cho-seed.json</c> declares it.
///
/// <para>
/// It is read rather than restated because <c>scripts/interop/seed-cho.mjs</c>
/// mints the same token from the same block. Two copies of an issuer or a
/// permission list would drift into an opaque 401 — the service cannot say
/// which of the two sides is wrong — where one file read twice drifts into a
/// diff.
/// </para>
/// </summary>
public sealed record ChoSeedAuth
{
    [JsonPropertyName("issuer")] public string Issuer { get; init; } = "";
    [JsonPropertyName("audience")] public string Audience { get; init; } = "";
    [JsonPropertyName("subjectPrefix")] public string SubjectPrefix { get; init; } = "";
    [JsonPropertyName("lifetimeSeconds")] public int LifetimeSeconds { get; init; }

    /// <summary>Environment variable that overrides <see cref="SigningKeyBase64"/>.</summary>
    [JsonPropertyName("signingKeyEnvVar")] public string SigningKeyEnvVar { get; init; } = "";

    /// <summary>
    /// The public development symmetric key every service already carries in its
    /// appsettings.Development.json, base64 as
    /// <c>ChoAuthOptions.SigningCredentialsFrom</c> reads it.
    /// <c>ChoAuthOptions.Validate</c> refuses symmetric keys on any host that is
    /// not Development or Testing, so a token minted from it cannot authenticate
    /// against anything deployed.
    /// </summary>
    [JsonPropertyName("signingKeyBase64")] public string SigningKeyBase64 { get; init; } = "";

    [JsonPropertyName("permissions")] public List<string> Permissions { get; init; } = new();
}

/// <summary>The synthetic record set the interop-cho profile serves.</summary>
public sealed record ChoSeedFixture
{
    [JsonPropertyName("tenantId")] public string TenantId { get; init; } = "";
    [JsonPropertyName("auth")] public ChoSeedAuth Auth { get; init; } = new();
    [JsonPropertyName("member")] public ChoSeedMember Member { get; init; } = new();
    [JsonPropertyName("backendClient")] public ChoSeedBackendClient BackendClient { get; init; } = new();
    [JsonPropertyName("patientApp")] public ChoSeedPatientApp PatientApp { get; init; } = new();

    public static ChoSeedFixture Load() => LoadFrom(InteropPaths.ChoSeedFixture);

    public static ChoSeedFixture LoadFrom(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Seed fixture not found at '{path}'.", path);
        }

        return JsonSerializer.Deserialize<ChoSeedFixture>(
                   File.ReadAllText(path),
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException($"'{path}' did not deserialize to a seed fixture.");
    }

    /// <summary>
    /// Mints the bearer token the harness hands to an Inferno suite as its
    /// <c>auth_info</c> input.
    ///
    /// <para>
    /// Hand-rolled HS256 rather than a JWT library: the test project takes no
    /// dependency on one, and the seeder signs the same three segments the same
    /// way. <see cref="System.Security.Cryptography.HMACSHA256"/> is the whole
    /// of it.
    /// </para>
    /// </summary>
    public string DevelopmentToken(DateTimeOffset? now = null)
    {
        var issuedAt = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var key = Environment.GetEnvironmentVariable(Auth.SigningKeyEnvVar) ?? Auth.SigningKeyBase64;

        var header = JsonSerializer.Serialize(new { alg = "HS256", typ = "JWT" });
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["iss"] = Auth.Issuer,
            ["aud"] = Auth.Audience,
            ["sub"] = $"{Auth.SubjectPrefix}-{TenantId}",
            ["jti"] = $"{issuedAt}-{Guid.NewGuid():N}",
            ["iat"] = issuedAt,
            ["nbf"] = issuedAt,
            ["exp"] = issuedAt + Auth.LifetimeSeconds,
            ["tenant_id"] = TenantId,
            ["roles"] = Array.Empty<string>(),
            ["permissions"] = Auth.Permissions,
            ["name"] = "Interop Harness",
        });

        var signingInput = $"{Base64Url(Encoding.UTF8.GetBytes(header))}.{Base64Url(Encoding.UTF8.GetBytes(payload))}";
        using var hmac = new HMACSHA256(Convert.FromBase64String(key));
        var signature = hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput));

        return $"{signingInput}.{Base64Url(signature)}";
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>The one seeded member. Its id is what a suite is told to search for.</summary>
public sealed record ChoSeedMember
{
    [JsonPropertyName("memberId")] public string MemberId { get; init; } = "";
}

/// <summary>
/// The SMART Backend Services client a kit that authenticates itself is
/// registered as. Keys are not part of it: the harness registers the public
/// keys the kit publishes, so the private half stays in the kit.
/// </summary>
public sealed record ChoSeedBackendClient
{
    [JsonPropertyName("clientId")] public string ClientId { get; init; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; init; } = "";

    /// <summary>The provider the client acts for: the Group a kit exports.</summary>
    [JsonPropertyName("providerId")] public string ProviderId { get; init; } = "";

    [JsonPropertyName("scopes")] public List<string> Scopes { get; init; } = new();
}

/// <summary>
/// The SMART patient app a kit that needs a member-bound token is authorised
/// as. The harness drives the launch itself (see <c>InfernoPatientApp</c>).
/// </summary>
public sealed record ChoSeedPatientApp
{
    [JsonPropertyName("clientId")] public string ClientId { get; init; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; init; } = "";
    [JsonPropertyName("redirectUri")] public string RedirectUri { get; init; } = "";

    /// <summary>The Development login's username the member's identity is bound to.</summary>
    [JsonPropertyName("username")] public string Username { get; init; } = "";

    [JsonPropertyName("scopes")] public List<string> Scopes { get; init; } = new();
}
