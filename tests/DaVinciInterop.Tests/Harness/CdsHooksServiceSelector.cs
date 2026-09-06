namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// Picks the CDS Hooks service a scenario should invoke from what an external
/// implementation actually advertised.
///
/// Selection is by hook and by advertised capability — never by position in the
/// discovery list. A server is free to reorder, rename or add services between
/// releases, and a scenario that indexed into the array would silently start
/// testing a different service after an upstream change rather than failing
/// honestly.
/// </summary>
public static class CdsHooksServiceSelector
{
    /// <summary>
    /// The discovery extension through which Da Vinci CRD advertises the IG
    /// version a service implements.
    /// </summary>
    public const string CrdVersionExtension = "davinci-crd.version";

    /// <summary>
    /// Selects the single service implementing <paramref name="hook"/>.
    /// </summary>
    /// <param name="requireCrd">
    /// When true, only services advertising the CRD version extension qualify, so
    /// a plain CDS Hooks service on the same hook is not mistaken for a CRD one.
    /// </param>
    /// <exception cref="CdsHooksServiceSelectionException">
    /// No service matches, or more than one matches and the choice would be arbitrary.
    /// </exception>
    public static CdsHooksService Select(CdsHooksDiscovery discovery, string hook, bool requireCrd = true)
    {
        var candidates = discovery.Services
            .Where(service => service.Hook == hook)
            .Where(service => !requireCrd || service.AdvertisedCrdVersions.Count > 0)
            .ToList();

        if (candidates.Count == 0)
        {
            var advertised = discovery.Services.Count == 0
                ? "(none)"
                : string.Join(", ", discovery.Services.Select(s => $"{s.Id}[{s.Hook}]"));
            throw new CdsHooksServiceSelectionException(
                $"No {(requireCrd ? "CRD " : string.Empty)}service for hook '{hook}' was advertised. " +
                $"Advertised services: {advertised}.");
        }

        if (candidates.Count > 1)
        {
            throw new CdsHooksServiceSelectionException(
                $"{candidates.Count} services advertise hook '{hook}' " +
                $"({string.Join(", ", candidates.Select(c => c.Id))}). The scenario must disambiguate " +
                "rather than let the harness pick one arbitrarily.");
        }

        var selected = candidates[0];
        if (string.IsNullOrWhiteSpace(selected.Id))
        {
            throw new CdsHooksServiceSelectionException(
                $"The service advertised for hook '{hook}' has no id, so it cannot be invoked.");
        }

        return selected;
    }

    /// <summary>Every service that advertises a CRD version extension.</summary>
    public static IReadOnlyList<CdsHooksService> CrdServices(CdsHooksDiscovery discovery) =>
        discovery.Services.Where(service => service.AdvertisedCrdVersions.Count > 0).ToList();

    /// <summary>
    /// The distinct CRD IG versions advertised across all CRD services, so evidence
    /// records the version actually in play rather than one assumed from a pin.
    /// </summary>
    public static IReadOnlyList<string> AdvertisedCrdVersions(CdsHooksDiscovery discovery) =>
        CrdServices(discovery)
            .SelectMany(service => service.AdvertisedCrdVersions)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(version => version, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The prefetch keys a service advertises as templates. A scenario supplies
    /// these so the server has no reason to call back to the FHIR server.
    /// </summary>
    public static IReadOnlyList<string> AdvertisedPrefetchKeys(CdsHooksService service) =>
        service.Prefetch.Keys.OrderBy(key => key, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Structural problems with a discovery document, per the CDS Hooks
    /// specification. Empty means the document is well formed.
    /// </summary>
    public static IReadOnlyList<string> DiscoveryViolations(CdsHooksDiscovery? discovery)
    {
        if (discovery is null)
        {
            return new[] { "discovery document could not be parsed as JSON" };
        }

        var problems = new List<string>();
        if (discovery.Services.Count == 0)
        {
            problems.Add("discovery document advertises no services");
        }

        foreach (var (service, index) in discovery.Services.Select((s, i) => (s, i)))
        {
            if (string.IsNullOrWhiteSpace(service.Id))
            {
                problems.Add($"services[{index}] has no id");
            }

            if (string.IsNullOrWhiteSpace(service.Hook))
            {
                problems.Add($"services[{index}] ('{service.Id}') has no hook");
            }
        }

        return problems;
    }
}

/// <summary>Raised when the scenario's target service cannot be resolved from discovery.</summary>
public sealed class CdsHooksServiceSelectionException : Exception
{
    public CdsHooksServiceSelectionException(string message) : base(message) { }
}
