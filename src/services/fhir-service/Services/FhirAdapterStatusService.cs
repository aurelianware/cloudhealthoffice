using FhirService.Models;
using Microsoft.Extensions.Options;

namespace FhirService.Services;

public sealed record FhirAdapterResourceStatus(
    string Resource,
    string Mode,
    string Source,
    string BuyerSafeWording);

public sealed record FhirAdapterStatusReport(
    string ConfiguredMode,
    string EffectiveMode,
    string DataClassification,
    string TenantId,
    string BuyerSafeLabel,
    string AttestationNote,
    IReadOnlyList<FhirAdapterResourceStatus> Resources);

public interface IFhirAdapterStatusService
{
    FhirAdapterStatusReport GetStatus();
}

/// <summary>
/// Canonical adapter-mode report for buyer demos and the
/// <c>/fhir/r4/adapter-status</c> endpoint. Combines configured
/// <see cref="FhirAdapterOptions"/> with live wiring signals
/// (for example <c>Appeals:UseMockAdapter</c>) so the report cannot
/// claim Live while the mock adapter is still registered.
/// </summary>
public sealed class FhirAdapterStatusService : IFhirAdapterStatusService
{
    public const string HeaderMode = "X-CHO-Adapter-Mode";
    public const string HeaderDataClass = "X-CHO-Data-Class";
    public const string HeaderLabel = "X-CHO-Adapter-Label";

    private static readonly string AttestationNote =
        "This report is implementation evidence, not legal attestation. " +
        "Production CMS-0057-F readiness depends on payer source-system " +
        "integration, operating procedures, and compliance review.";

    private static readonly (string Resource, string DefaultMode, string Source)[] Defaults =
    [
        ("Patient", FhirAdapterModes.Demo, "MockFhirDataAdapter"),
        ("Coverage", FhirAdapterModes.Demo, "MockFhirDataAdapter"),
        ("Encounter", FhirAdapterModes.Demo, "MockFhirDataAdapter"),
        ("Claim", FhirAdapterModes.Demo, "MockFhirDataAdapter"),
        ("ExplanationOfBenefit", FhirAdapterModes.Hybrid, "claims-service FHIR proxy"),
        ("Practitioner", FhirAdapterModes.Hybrid, "provider-service FHIR proxy"),
        ("PractitionerRole", FhirAdapterModes.Hybrid, "provider-service FHIR proxy"),
        ("Organization", FhirAdapterModes.Hybrid, "provider-service FHIR proxy"),
        ("InsurancePlan", FhirAdapterModes.Hybrid, "benefit-plan-service FHIR proxy"),
        ("Appeal", FhirAdapterModes.Demo, "MockFhirAppealAdapter"),
        ("PriorAuthorization", FhirAdapterModes.Demo, "PasAutoAdjudicator"),
        ("CRD", FhirAdapterModes.Demo, "CrdService"),
        ("DTR", FhirAdapterModes.Demo, "DtrService"),
        ("BulkExport", FhirAdapterModes.Demo, "BulkExportService scaffold"),
        ("Consent", FhirAdapterModes.Demo, "consent-service building block"),
        // Payer-to-Payer over CHO-owned data: inbound respond (P2P-01),
        // member-match (P2P-04), outbound initiation (P2P-02), and durable
        // ingestion of what comes back. This is NOT a complete Payer-to-Payer
        // capability: dedicated P2P consent semantics (P2P-03) remain partial,
        // ingestion covers only the resource types this FHIR surface serves
        // (others are archived, not ingested), imported data is not yet projected
        // into the read APIs, outbound transport to a specific payer depends on
        // that payer's onboarding (credentials/endpoint configuration), and no
        // external-core (QNXT/Facets/HealthEdge) P2P integration exists.
        ("PayerToPayer", FhirAdapterModes.Demo,
            "PayerToPayerExchangeService (inbound respond) + PayerToPayerMemberMatchService ($member-match) "
            + "+ PayerToPayerOutboundService (outbound initiation; remote payer endpoints per configuration) "
            + "+ PayerToPayerPackageIngestionService (durable import of supported resource types)"),
    ];

    private readonly FhirAdapterOptions _options;
    private readonly IConfiguration _config;

    public FhirAdapterStatusService(IOptions<FhirAdapterOptions> options, IConfiguration config)
    {
        _options = options.Value;
        _config = config;
    }

    public FhirAdapterStatusReport GetStatus()
    {
        var resources = new List<FhirAdapterResourceStatus>(Defaults.Length);
        foreach (var (resource, defaultMode, source) in Defaults)
        {
            var mode = ResolveMode(resource, defaultMode);
            var resolvedSource = source;
            if (resource == "Appeal")
            {
                var useMock = _config.GetValue("Appeals:UseMockAdapter", true);
                mode = useMock ? FhirAdapterModes.Demo : CoalesceLiveOrHybrid(mode);
                resolvedSource = useMock
                    ? "MockFhirAppealAdapter"
                    : "HttpFhirAppealAdapter → appeals-service";
            }

            resources.Add(new FhirAdapterResourceStatus(
                resource,
                mode,
                resolvedSource,
                BuyerWording(mode)));
        }

        var configured = NormalizeMode(_options.Mode, FhirAdapterModes.Demo);
        var effective = DeriveEffectiveMode(configured, resources);
        var dataClass = string.IsNullOrWhiteSpace(_options.DataClassification)
            ? FhirAdapterDataClasses.Synthetic
            : _options.DataClassification.Trim().ToLowerInvariant();
        var tenantId = string.IsNullOrWhiteSpace(_options.TenantId)
            ? "demo-tenant"
            : _options.TenantId.Trim();

        return new FhirAdapterStatusReport(
            ConfiguredMode: configured,
            EffectiveMode: effective,
            DataClassification: dataClass,
            TenantId: tenantId,
            BuyerSafeLabel: BuyerWording(effective),
            AttestationNote: AttestationNote,
            Resources: resources);
    }

    private string ResolveMode(string resource, string defaultMode)
    {
        if (_options.Resources.TryGetValue(resource, out var configured)
            && !string.IsNullOrWhiteSpace(configured))
        {
            return NormalizeMode(configured, defaultMode);
        }

        return NormalizeMode(defaultMode, FhirAdapterModes.Demo);
    }

    private static string CoalesceLiveOrHybrid(string configured)
        => string.Equals(configured, FhirAdapterModes.Live, StringComparison.OrdinalIgnoreCase)
            ? FhirAdapterModes.Live
            : FhirAdapterModes.Hybrid;

    private static string DeriveEffectiveMode(
        string configured,
        IReadOnlyList<FhirAdapterResourceStatus> resources)
    {
        var inScope = resources
            .Select(r => r.Mode)
            .Where(m => !string.Equals(m, FhirAdapterModes.OutOfScope, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (inScope.Count == 0)
            return configured;
        if (inScope.Count == 1)
            return inScope[0];
        return FhirAdapterModes.Hybrid;
    }

    private static string NormalizeMode(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        if (string.Equals(value, FhirAdapterModes.Demo, StringComparison.OrdinalIgnoreCase))
            return FhirAdapterModes.Demo;
        if (string.Equals(value, FhirAdapterModes.Hybrid, StringComparison.OrdinalIgnoreCase))
            return FhirAdapterModes.Hybrid;
        if (string.Equals(value, FhirAdapterModes.Live, StringComparison.OrdinalIgnoreCase))
            return FhirAdapterModes.Live;
        if (string.Equals(value, FhirAdapterModes.OutOfScope, StringComparison.OrdinalIgnoreCase))
            return FhirAdapterModes.OutOfScope;
        return fallback;
    }

    public static string BuyerWording(string mode) => mode switch
    {
        FhirAdapterModes.Live =>
            "Backed by payer source-system integration for this pilot scope.",
        FhirAdapterModes.Hybrid =>
            "Pilot wiring in progress; source labels shown per resource.",
        FhirAdapterModes.OutOfScope =>
            "Not in current pilot scope.",
        _ =>
            "Demonstrates technical behavior with synthetic data.",
    };
}
