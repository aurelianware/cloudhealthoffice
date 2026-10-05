using System.Text.Json;
using System.Text.RegularExpressions;
using CloudHealthOffice.Infrastructure.Security;

namespace CloudHealthOffice.TokenService.Workload;

/// <summary>
/// Bound from <c>WorkloadTokens</c>. Governs which Kubernetes service-account
/// tokens <c>POST /v1/token/workload</c> accepts and what each registered
/// workload may receive. See docs/security/argo-service-tokens.md.
/// </summary>
public sealed class WorkloadTokenOptions
{
    public const string SectionName = "WorkloadTokens";

    /// <summary>Off by default: the endpoint is not mapped until a cluster issuer and registrations are configured.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The cluster's service-account issuer, exactly as it appears in <c>iss</c>
    /// (AKS: the OIDC issuer URL, <c>az aks show --query oidcIssuerProfile.issuerUrl</c>).
    /// </summary>
    public string KubernetesIssuer { get; set; } = string.Empty;

    /// <summary>
    /// OIDC discovery document for the issuer's signing keys. Defaults to
    /// <c>{KubernetesIssuer}/.well-known/openid-configuration</c>. Ignored when a
    /// static JWKS is configured.
    /// </summary>
    public string? MetadataAddress { get; set; }

    /// <summary>
    /// The issuer's JWKS as JSON, for a cluster whose discovery endpoint
    /// token-service cannot reach (<c>kubectl get --raw /openid/v1/jwks</c>).
    /// It must be updated when the cluster rotates its service-account key.
    /// </summary>
    public string? JwksJson { get; set; }

    /// <summary>A file holding the JWKS (for example a mounted ConfigMap). Read once at startup.</summary>
    public string? JwksFile { get; set; }

    /// <summary>The audience the projected service-account token must carry.</summary>
    public string Audience { get; set; } = "cho-token-service";

    /// <summary>
    /// Longest lifetime (<c>exp - iat</c>) accepted on a service-account token,
    /// so only short-lived projected tokens are exchanged.
    /// </summary>
    public TimeSpan MaxSourceTokenLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Clock skew tolerated on the service-account token.</summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// <c>iss</c> of issued workload tokens. Services trust it with
    /// <c>AllowWorkloadIdentity: true</c> and the token-service public key. It
    /// must differ from <c>TokenSigning:Issuer</c>.
    /// </summary>
    public string Issuer { get; set; } = "cho-workload";

    /// <summary>Lifetime of an issued workload token.</summary>
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Registered workloads, from configuration (appsettings, environment, Key Vault).</summary>
    public List<WorkloadRegistration> Registrations { get; set; } = new();

    /// <summary>
    /// A JSON file with more registrations (an array of registrations), for a
    /// mounted ConfigMap or Key Vault secret. Read once at startup.
    /// </summary>
    public string? RegistrationsFile { get; set; }

    /// <summary>Tenant ids a workload can never be issued a token for.</summary>
    public static readonly IReadOnlySet<string> ReservedTenants =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cho-platform" };

    internal static readonly Regex TenantIdPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.CultureInvariant);
    private static readonly Regex Dns1123Label = new(@"^[a-z0-9]([-a-z0-9]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant);
    private static readonly Regex Dns1123Subdomain = new(@"^[a-z0-9]([-a-z0-9.]{0,251}[a-z0-9])?$", RegexOptions.CultureInvariant);
    private static readonly Regex ClientIdPattern = new(@"^wf-[a-z0-9]([-a-z0-9]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant);
    private static readonly Regex PermissionPattern = new(@"^[a-z0-9-]+:[a-z0-9-]+$", RegexOptions.CultureInvariant);

    /// <summary>Registrations from configuration and from <see cref="RegistrationsFile"/>.</summary>
    public IReadOnlyList<WorkloadRegistration> AllRegistrations()
    {
        var all = new List<WorkloadRegistration>(Registrations);
        if (!string.IsNullOrWhiteSpace(RegistrationsFile))
        {
            var fromFile = JsonSerializer.Deserialize<List<WorkloadRegistration>>(
                File.ReadAllText(RegistrationsFile), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            all.AddRange(fromFile ?? new List<WorkloadRegistration>());
        }
        return all;
    }

    /// <summary>The configured JWKS, if any (inline, else from <see cref="JwksFile"/>).</summary>
    public string? StaticJwks()
        => !string.IsNullOrWhiteSpace(JwksJson) ? JwksJson
            : !string.IsNullOrWhiteSpace(JwksFile) ? File.ReadAllText(JwksFile)
            : null;

    /// <summary>Fails startup on a configuration that would issue broader tokens than intended.</summary>
    public void Validate(string userTokenIssuer, bool isDevelopment)
    {
        if (!Enabled)
            return;

        if (string.IsNullOrWhiteSpace(KubernetesIssuer))
            throw new InvalidOperationException($"{SectionName}:KubernetesIssuer is required.");
        if (string.IsNullOrWhiteSpace(Audience))
            throw new InvalidOperationException($"{SectionName}:Audience is required.");
        if (string.Equals(Audience, ChoDevelopmentAuth.Audience, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{SectionName}:Audience must be token-service's own audience, not the CHO API audience.");
        if (string.IsNullOrWhiteSpace(Issuer))
            throw new InvalidOperationException($"{SectionName}:Issuer is required.");
        if (string.Equals(Issuer, userTokenIssuer, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{SectionName}:Issuer must differ from TokenSigning:Issuer: services trust only the workload issuer to name a workload.");
        if (TokenLifetime <= TimeSpan.Zero || TokenLifetime > TimeSpan.FromMinutes(15))
            throw new InvalidOperationException($"{SectionName}:TokenLifetime must be between 0 and 15 minutes.");
        if (MaxSourceTokenLifetime <= TimeSpan.Zero || MaxSourceTokenLifetime > TimeSpan.FromHours(24))
            throw new InvalidOperationException($"{SectionName}:MaxSourceTokenLifetime must be between 0 and 24 hours.");
        if (ClockSkew < TimeSpan.Zero || ClockSkew > TimeSpan.FromMinutes(5))
            throw new InvalidOperationException($"{SectionName}:ClockSkew must be between 0 and 5 minutes.");

        if (StaticJwks() == null)
        {
            var metadata = string.IsNullOrWhiteSpace(MetadataAddress) ? KubernetesIssuer : MetadataAddress;
            if (!Uri.TryCreate(metadata, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && !isDevelopment))
                throw new InvalidOperationException(
                    $"{SectionName}: set JwksJson/JwksFile, or an https KubernetesIssuer/MetadataAddress for OIDC discovery.");
        }

        var registrations = AllRegistrations();
        if (registrations.Count == 0)
            throw new InvalidOperationException($"{SectionName}:Registrations is empty.");

        var accounts = new HashSet<string>(StringComparer.Ordinal);
        var clients = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in registrations)
        {
            var label = $"{SectionName} registration '{r.ClientId}'";
            if (!Dns1123Label.IsMatch(r.Namespace ?? string.Empty))
                throw new InvalidOperationException($"{label}: Namespace is not a Kubernetes namespace name.");
            if (!Dns1123Subdomain.IsMatch(r.ServiceAccount ?? string.Empty))
                throw new InvalidOperationException($"{label}: ServiceAccount is not a Kubernetes service account name.");
            if (!ClientIdPattern.IsMatch(r.ClientId ?? string.Empty))
                throw new InvalidOperationException(
                    $"{label}: ClientId must start with '{ChoWorkloadRole.ClientIdPrefix}' (lower case, letters, digits, hyphens).");
            if (!accounts.Add(r.Namespace + "/" + r.ServiceAccount))
                throw new InvalidOperationException($"{label}: service account {r.Namespace}/{r.ServiceAccount} is registered twice.");
            if (!clients.Add(r.ClientId!))
                throw new InvalidOperationException($"{label}: client id is registered twice.");

            if (r.AllowedTenants.Count == 0)
                throw new InvalidOperationException($"{label}: AllowedTenants is empty.");
            if (r.AllowsAnyTenant && r.AllowedTenants.Count != 1)
                throw new InvalidOperationException($"{label}: '*' must be the only AllowedTenants entry.");
            if (!r.AllowsAnyTenant && r.AllowedTenants.Any(t => !TenantIdPattern.IsMatch(t) || ReservedTenants.Contains(t)))
                throw new InvalidOperationException($"{label}: AllowedTenants holds an invalid or reserved tenant id.");

            if (r.Permissions.Count == 0)
                throw new InvalidOperationException($"{label}: Permissions is empty.");
            foreach (var p in r.Permissions)
            {
                // Exact permissions only: no wildcards, and never a platform permission.
                if (!PermissionPattern.IsMatch(p ?? string.Empty) || ChoRolePermissions.IsReserved(p!))
                    throw new InvalidOperationException(
                        $"{label}: permission '{p}' is not allowed (exact resource:action, no wildcards, no platform:*).");
            }
        }
    }
}

/// <summary>One workload: a Kubernetes service account and what its tokens may carry.</summary>
public sealed class WorkloadRegistration
{
    public string? Namespace { get; set; }
    public string? ServiceAccount { get; set; }

    /// <summary>The CHO client id (<c>sub</c> and <c>azp</c>), starting with <c>wf-</c>.</summary>
    public string? ClientId { get; set; }

    /// <summary>Tenant ids this workload may act in, or the single entry <c>*</c> for any tenant.</summary>
    public List<string> AllowedTenants { get; set; } = new();

    /// <summary>Exact permissions written into every token for this workload.</summary>
    public List<string> Permissions { get; set; } = new();

    internal bool AllowsAnyTenant => AllowedTenants.Count == 1 && AllowedTenants[0] == "*";

    internal bool AllowsTenant(string tenantId)
        => AllowsAnyTenant || AllowedTenants.Contains(tenantId, StringComparer.Ordinal);
}
