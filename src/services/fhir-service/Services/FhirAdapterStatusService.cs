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
        // Da Vinci PAS: Claim/$submit (PAS-03), Claim/$inquire (PAS-04), and the
        // CDex additional-information round trip on a pended decision (PAS-07).
        // NOT complete: attachment bytes go through the shared attachment
        // content store, of which fhir-service registers the IN-PROCESS
        // implementation by default, so a deployment must bind a durable one;
        // malware scanning is a seam with no scanner behind it; there is no
        // X12 278 parser; and authorization records carry no concurrency token.
        ("PriorAuthorization", FhirAdapterModes.Demo,
            "PasAutoAdjudicator (Claim/$submit) + PriorAuthorizationInquiryService "
            + "(Claim/$inquire; read-only projection of the stored authorization record) "
            + "+ CDex additional information (Task on the CDex Task Attachment Request "
            + "profile for the request, $submit-attachment for the response, over the "
            + "rfai-service case record; accepted documentation returns the authorization "
            + "to review, never to approved) "
            + "+ authorization-service retention lifecycle (policy-driven, tenant-safe, "
            + "conditional-delete sweeper; disabled by default)"),
        ("CRD", FhirAdapterModes.Demo, "CrdService"),
        ("DTR", FhirAdapterModes.Demo, "DtrService"),
        ("BulkExport", FhirAdapterModes.Demo, "BulkExportService scaffold"),
        // Purpose-scoped consent on one registry: ConsentPurposeOfUse
        // (PayerToPayerExchange / ProviderAccess) evaluated by the shared
        // ConsentAuthorizationPolicy. BOTH purposes are enforced server-side
        // through it — Payer-to-Payer in both directions, and the Provider Access
        // read path via ProviderAccessAuthorizationFilter — reaching their answers
        // through the same IConsentEvaluator and the same policy.
        ("Consent", FhirAdapterModes.Demo,
            "consent-service registry (ConsentPurposeOfUse + ConsentAuthorizationPolicy; "
            + "PHI-free authorization-snapshots projection) enforced for Payer-to-Payer "
            + "and Provider Access through one shared evaluator"),
        // Provider Access over CHO-owned data. NOT a complete capability:
        // attribution panels come from configuration, so no live roster feed from
        // a payer source system is wired up (engagement integration behind
        // IProviderAttributionSource); Payer-to-Payer imported data is not yet
        // projected into these reads; and no external-core (QNXT/Facets/
        // HealthEdge) Provider Access integration exists.
        ("ProviderAccess", FhirAdapterModes.Demo,
            "ProviderAccessAuthorizationService (authentication + SMART scope + provider/member "
            + "attribution + active ProviderAccess-purpose consent, each independent and mandatory, "
            + "composed fail-closed) enforced by a global ProviderAccessAuthorizationFilter over every "
            + "member-scoped FHIR resource"),
        // Payer-to-Payer over CHO-owned data: inbound respond (P2P-01),
        // member-match (P2P-04), outbound initiation (P2P-02), and durable
        // ingestion of what comes back. This is NOT a complete Payer-to-Payer
        // capability: consent must be recorded with the Payer-to-Payer purpose
        // before any exchange is authorized (generic consent is NOT
        // reinterpreted, so a deployment authorizes nothing until purposes are
        // recorded), ingestion covers only the resource types this FHIR surface serves
        // (others are archived, not ingested), imported data is not yet projected
        // into the read APIs, outbound transport to a specific payer depends on
        // that payer's onboarding (credentials/endpoint configuration), and no
        // external-core (QNXT/Facets/HealthEdge) P2P integration exists.
        ("PayerToPayer", FhirAdapterModes.Demo,
            "PayerToPayerExchangeService (inbound respond) + PayerToPayerMemberMatchService ($member-match) "
            + "+ PayerToPayerOutboundService (outbound initiation; remote payer endpoints per configuration) "
            + "+ PayerToPayerPackageIngestionService (durable import of supported resource types) "
            + "+ ConsentRegistryPayerToPayerConsentGate (purpose-scoped consent enforced server-side, both directions)"),
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
