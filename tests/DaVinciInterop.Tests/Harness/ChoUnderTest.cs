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
    /// The seeder's service URLs, as the environment variables
    /// scripts/interop/seed-cho.mjs reads.
    /// </summary>
    public static IReadOnlyDictionary<string, string> SeedServiceUrls => new Dictionary<string, string>
    {
        ["INTEROP_MEMBER_URL"] = Host("INTEROP_CHO_MEMBER_PORT", 18025),
        ["INTEROP_COVERAGE_URL"] = Host("INTEROP_CHO_COVERAGE_PORT", 18026),
        ["INTEROP_CLAIMS_URL"] = Host("INTEROP_CHO_CLAIMS_PORT", 18027),
        ["INTEROP_FHIR_URL"] = Host("INTEROP_CHO_FHIR_PORT", 18023),
    };

    /// <summary>The compose port variables and their defaults, for the guard test.</summary>
    public static IReadOnlyDictionary<string, int> PortDefaults => new Dictionary<string, int>
    {
        ["INTEROP_CHO_FHIR_PORT"] = 18023,
        ["INTEROP_CHO_MEMBER_PORT"] = 18025,
        ["INTEROP_CHO_COVERAGE_PORT"] = 18026,
        ["INTEROP_CHO_CLAIMS_PORT"] = 18027,
    };

    private static string Host(string variable, int fallback)
    {
        var port = int.TryParse(Environment.GetEnvironmentVariable(variable), out var configured) && configured > 0
            ? configured
            : fallback;

        return $"http://127.0.0.1:{port}";
    }
}
