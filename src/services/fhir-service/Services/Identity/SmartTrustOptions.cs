using System.Diagnostics.CodeAnalysis;

namespace FhirService.Services.Identity;

/// <summary>
/// Which identity infrastructure this deployment trusts. Stated, never inferred.
///
/// The distinction exists because the failure it prevents is silent: a
/// deployment that reached production still pointed at the bundled demo
/// authorization server would validate tokens perfectly and trust the wrong
/// issuer. Deriving the mode from "is any external issuer configured?" would
/// make a missing config file a downgrade to demo trust rather than an error,
/// which is exactly the fallback that must not exist.
/// </summary>
public enum SmartTrustMode
{
    /// <summary>
    /// The bundled smart-auth-service. Local development and the acceptance
    /// suite only — <see cref="SmartTrustOptions.Validate"/> refuses it outside
    /// a development host.
    /// </summary>
    Demo,

    /// <summary>
    /// One or more externally managed OAuth/OIDC/SMART authorization servers.
    /// CHO is purely a resource server: it validates tokens and never issues them.
    /// </summary>
    ExternalIssuer,
}

/// <summary>
/// The trust configuration for CHO's FHIR resource server, bound from the
/// <c>SmartAuth</c> configuration section.
///
/// Two invariants shape this type:
///
///   1. Trust is administrator-controlled. Nothing a token says can add an
///      issuer, change an audience, or redirect key retrieval. The registry is
///      fixed at startup from configuration.
///   2. Configuration errors fail closed. <see cref="Validate"/> refuses a
///      deployment that would otherwise run with weaker trust than the operator
///      believes they configured — see <see cref="SmartTrustValidationException"/>.
/// </summary>
public sealed class SmartTrustOptions
{
    public const string SectionName = "SmartAuth";

    /// <summary>Demo or ExternalIssuer. Defaults to Demo so a developer clone runs.</summary>
    public SmartTrustMode Mode { get; set; } = SmartTrustMode.Demo;

    /// <summary>
    /// The trusted issuers. In ExternalIssuer mode at least one is required —
    /// an empty registry means no token can ever validate, so starting up that
    /// way would serve 401s that look like an outage rather than a misconfiguration.
    /// </summary>
    public List<TrustedIssuerOptions> TrustedIssuers { get; set; } = [];

    /// <summary>
    /// Tolerance for clock drift between the issuer and CHO. Capped by
    /// <see cref="MaxClockSkewSeconds"/>: skew large enough to meaningfully
    /// extend a token's life is a lifetime-validation bypass wearing a
    /// clock-drift costume.
    /// </summary>
    public int ClockSkewSeconds { get; set; } = 30;

    public const int MaxClockSkewSeconds = 300;

    // ── Legacy single-issuer fields ───────────────────────────────────────────
    // The shape before multi-issuer trust existed. Kept so an existing Demo
    // deployment and the acceptance suite keep working unchanged; in Demo mode
    // with no TrustedIssuers entry these are folded into one synthetic entry by
    // NormalizedIssuers. They are NOT honoured in ExternalIssuer mode — a
    // production deployment states its trust in the explicit shape.

    public string? Issuer { get; set; }
    public string? Audience { get; set; }
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// The issuer registry as the rest of the system sees it: the explicit
    /// entries, or in Demo mode the legacy fields folded into one.
    /// </summary>
    public IReadOnlyList<TrustedIssuerOptions> NormalizedIssuers()
    {
        if (TrustedIssuers.Count > 0) return TrustedIssuers;

        if (Mode == SmartTrustMode.Demo && !string.IsNullOrWhiteSpace(Issuer))
        {
            return
            [
                new TrustedIssuerOptions
                {
                    Issuer = Issuer!,
                    Audiences = string.IsNullOrWhiteSpace(Audience) ? ["fhir-api"] : [Audience!],
                    RequireHttpsMetadata = RequireHttpsMetadata,
                }
            ];
        }

        return [];
    }

    /// <summary>
    /// Fail-closed configuration check, run at startup before the first request.
    /// Throws rather than returning a result: a trust misconfiguration is not a
    /// condition the host can sensibly continue past.
    /// </summary>
    public void Validate(bool isDevelopmentHost)
    {
        // The fallback that must not exist. A production host running Demo trust
        // is the one failure mode nobody notices, because everything works — it
        // just trusts the wrong authorization server.
        if (Mode == SmartTrustMode.Demo && !isDevelopmentHost)
        {
            throw new SmartTrustValidationException(
                "SmartAuth:Mode is Demo but this is not a development host. Demo mode trusts the "
                + "bundled smart-auth-service and must never validate production tokens. Set "
                + "SmartAuth:Mode to ExternalIssuer and configure SmartAuth:TrustedIssuers.");
        }

        if (ClockSkewSeconds < 0 || ClockSkewSeconds > MaxClockSkewSeconds)
        {
            throw new SmartTrustValidationException(
                $"SmartAuth:ClockSkewSeconds must be between 0 and {MaxClockSkewSeconds}; "
                + $"got {ClockSkewSeconds}. Skew beyond that extends token lifetime materially.");
        }

        var issuers = NormalizedIssuers();
        if (issuers.Count == 0)
        {
            throw new SmartTrustValidationException(
                Mode == SmartTrustMode.ExternalIssuer
                    ? "SmartAuth:Mode is ExternalIssuer but SmartAuth:TrustedIssuers is empty. "
                      + "No token could ever validate."
                    : "SmartAuth:Issuer or SmartAuth:TrustedIssuers must be configured.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var issuer in issuers)
        {
            issuer.Validate(isDevelopmentHost);

            // Two entries for one issuer would make trust resolution depend on
            // list order — the "first matching key wins" behaviour this registry
            // exists to prevent.
            if (!seen.Add(issuer.Issuer))
            {
                throw new SmartTrustValidationException(
                    $"SmartAuth:TrustedIssuers contains '{issuer.Issuer}' more than once. "
                    + "Issuer configuration must resolve to exactly one entry.");
            }
        }
    }
}

/// <summary>Thrown when trust configuration cannot be established safely.</summary>
public sealed class SmartTrustValidationException : Exception
{
    public SmartTrustValidationException(string message) : base(message) { }
}
