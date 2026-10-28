namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// Where CHO is, when CHO is the system under test.
///
/// <para>
/// Two sets of addresses, and conflating them is the mistake this type exists
/// to prevent. A conformance kit runs <em>inside</em> the Compose network and
/// reaches CHO by service name; the harness and the seeder run on the host and
/// reach it through the published loopback port. Handing a kit a
/// <c>127.0.0.1</c> URL points it at its own container, where nothing answers —
/// and the kit reports that as CHO failing every test, which is the most
/// misleading result this harness could produce.
/// </para>
///
/// <para>
/// Every value here mirrors a default in interop/docker-compose.interop.yml and
/// honours the same environment variable the compose file reads, so a port
/// moved for a local run moves on both sides at once.
/// <c>InteropChoProfileAddressTests</c> fails the build if a default here stops
/// matching the compose file.
/// </para>
/// </summary>
public static class ChoUnderTest
{
    /// <summary>The Compose profile that starts CHO's own services.</summary>
    public const string Profile = "interop-cho";

    /// <summary>fhir-service, as a kit on the Compose network addresses it.</summary>
    public const string ContainerFhirBaseUrl = "http://cho-fhir-service:8080/fhir/r4";

    /// <summary>fhir-service, from the host.</summary>
    public static string HostFhirBaseUrl => $"{Host("INTEROP_CHO_FHIR_PORT", 18023)}/fhir/r4";

    /// <summary>
    /// smart-auth-service, as a kit on the Compose network addresses it. It is
    /// also the issuer (<c>SmartAuth__Issuer</c> in the compose file), so a
    /// kit's token request and assertion audience name the same address the
    /// tokens are issued under.
    /// </summary>
    public const string ContainerSmartAuthUrl = "http://cho-smart-auth-service:8080";

    /// <summary>The token endpoint, as a kit on the Compose network addresses it.</summary>
    public const string ContainerTokenUrl = ContainerSmartAuthUrl + "/connect/token";

    /// <summary>smart-auth-service, from the host (its admin API).</summary>
    public static string HostSmartAuthUrl => Host("INTEROP_SMART_AUTH_PORT", 18024);

    /// <summary>
    /// The public Provider Directory the profile publishes (G-36). Its id must
    /// match <c>PublicProviderDirectory__Directories__interop</c> on
    /// cho-fhir-service in the compose file.
    /// </summary>
    public const string PublicDirectoryId = "interop";

    /// <summary>The public directory, as a kit on the Compose network addresses it.</summary>
    public const string ContainerPublicDirectoryUrl = ContainerFhirBaseUrl + "/directory/" + PublicDirectoryId;

    /// <summary>The public directory, from the host.</summary>
    public static string HostPublicDirectoryUrl => $"{HostFhirBaseUrl}/directory/{PublicDirectoryId}";

    /// <summary>The base a kit is given for <paramref name="surface"/> (see <see cref="ChoSurfaces"/>).</summary>
    public static string ContainerBaseUrl(string surface) => surface switch
    {
        ChoSurfaces.Fhir => ContainerFhirBaseUrl,
        ChoSurfaces.PublicDirectory => ContainerPublicDirectoryUrl,
        _ => throw new InteropEnvironmentException($"Unknown CHO surface '{surface}' in interop/scenarios.json."),
    };

    /// <summary>The same base, as the harness reaches it from the host.</summary>
    public static string HostBaseUrl(string surface) => surface switch
    {
        ChoSurfaces.Fhir => HostFhirBaseUrl,
        ChoSurfaces.PublicDirectory => HostPublicDirectoryUrl,
        _ => throw new InteropEnvironmentException($"Unknown CHO surface '{surface}' in interop/scenarios.json."),
    };

    /// <summary>
    /// The seeder's service URLs, as the environment variables
    /// scripts/interop/seed-cho.mjs reads.
    /// </summary>
    public static IReadOnlyDictionary<string, string> SeedServiceUrls => new Dictionary<string, string>
    {
        ["INTEROP_MEMBER_URL"] = Host("INTEROP_CHO_MEMBER_PORT", 18025),
        ["INTEROP_COVERAGE_URL"] = Host("INTEROP_CHO_COVERAGE_PORT", 18026),
        ["INTEROP_CLAIMS_URL"] = Host("INTEROP_CHO_CLAIMS_PORT", 18027),
        ["INTEROP_FHIR_URL"] = Host("INTEROP_CHO_FHIR_PORT", 18023),
        ["INTEROP_PROVIDER_URL"] = Host("INTEROP_CHO_PROVIDER_PORT", 18029),
        ["INTEROP_BENEFIT_PLAN_URL"] = Host("INTEROP_CHO_BENEFIT_PLAN_PORT", 18030),
    };

    /// <summary>The compose port variables and their defaults, for the guard test.</summary>
    public static IReadOnlyDictionary<string, int> PortDefaults => new Dictionary<string, int>
    {
        ["INTEROP_CHO_FHIR_PORT"] = 18023,
        ["INTEROP_SMART_AUTH_PORT"] = 18024,
        ["INTEROP_CHO_MEMBER_PORT"] = 18025,
        ["INTEROP_CHO_COVERAGE_PORT"] = 18026,
        ["INTEROP_CHO_CLAIMS_PORT"] = 18027,
        ["INTEROP_CHO_PROVIDER_PORT"] = 18029,
        ["INTEROP_CHO_BENEFIT_PLAN_PORT"] = 18030,
    };

    private static string Host(string variable, int fallback)
    {
        var port = int.TryParse(Environment.GetEnvironmentVariable(variable), out var configured) && configured > 0
            ? configured
            : fallback;

        return $"http://127.0.0.1:{port}";
    }
}
