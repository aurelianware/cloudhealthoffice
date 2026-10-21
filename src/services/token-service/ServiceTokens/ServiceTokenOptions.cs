using System.Text.Json;
using System.Text.RegularExpressions;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.TokenService.Workload;

namespace CloudHealthOffice.TokenService.ServiceTokens;

/// <summary>
/// Bound from <c>ServiceTokens</c>. Governs <c>POST /v1/token/service</c>: which
/// workload-identity tokens are accepted (Entra on Azure, or Kubernetes
/// service-account tokens on any cluster), which CHO service each identity is,
/// and what the issued service tokens look like. See
/// docs/security/portal-token-service.md ("Service tokens").
/// </summary>
public sealed class ServiceTokenOptions
{
    public const string SectionName = "ServiceTokens";

    /// <summary>Off by default: the endpoint is not mapped until a directory, audience and registry are configured.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// How services prove who they are: <see cref="ServiceIdentityProvider.Entra"/>
    /// (default; Azure workload identity) or <see cref="ServiceIdentityProvider.Kubernetes"/>
    /// (projected service-account tokens; any conformant cluster). One provider per deployment.
    /// </summary>
    public ServiceIdentityProvider IdentityProvider { get; set; } = ServiceIdentityProvider.Entra;

    /// <summary>The cluster's service-account issuer. <see cref="ServiceIdentityProvider.Kubernetes"/> only.</summary>
    public KubernetesServiceIdentityOptions Kubernetes { get; set; } = new();

    /// <summary>
    /// CHO's own Entra directory (tenant id), where every service's managed
    /// identity lives. Tokens from any other directory are refused.
    /// </summary>
    public string EntraTenantId { get; set; } = string.Empty;

    /// <summary>
    /// Audiences the Entra token must carry: the service-token API app
    /// registration's App ID URI (<c>api://&lt;app id&gt;</c>, v1 tokens) and its
    /// client id (v2 tokens). Services request <c>api://&lt;app id&gt;/.default</c>.
    /// </summary>
    public List<string> Audiences { get; set; } = new();

    /// <summary>
    /// App role (in <c>roles</c>) the token must carry, when set. Assign it to
    /// each service's managed identity and set "assignment required" on the app,
    /// so Entra issues tokens for this API to those identities only.
    /// </summary>
    public string? RequiredAppRole { get; set; }

    /// <summary>
    /// OIDC discovery document for Entra's signing keys. Defaults to
    /// <c>https://login.microsoftonline.com/{EntraTenantId}/v2.0/.well-known/openid-configuration</c>.
    /// Ignored when a static JWKS is configured.
    /// </summary>
    public string? MetadataAddress { get; set; }

    /// <summary>A JWKS as JSON instead of discovery (tests, or an isolated network).</summary>
    public string? JwksJson { get; set; }

    /// <summary>Clock skew tolerated on the Entra token.</summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// <c>iss</c> of issued service tokens. Services trust it with <c>Kind: Service</c>
    /// and token-service's public key. It must differ from <c>TokenSigning:Issuer</c>
    /// and <c>WorkloadTokens:Issuer</c>, so the key's three kinds of token stay apart.
    /// </summary>
    public string Issuer { get; set; } = "cho-token-service-svc";

    /// <summary>Lifetime of an issued service token (at most 10 minutes).</summary>
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The registry: which managed identity is which CHO service.</summary>
    public List<ServiceClientRegistration> ServiceClients { get; set; } = new();

    /// <summary>A JSON file with more registrations (an array), for a mounted ConfigMap. Read once at startup.</summary>
    public string? ServiceClientsFile { get; set; }

    private static readonly Regex ClientIdPattern = new(@"^[a-z0-9]([-a-z0-9]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant);

    /// <summary>Registrations from configuration and from <see cref="ServiceClientsFile"/>.</summary>
    public IReadOnlyList<ServiceClientRegistration> AllServiceClients()
    {
        var all = new List<ServiceClientRegistration>(ServiceClients);
        if (!string.IsNullOrWhiteSpace(ServiceClientsFile))
        {
            var fromFile = JsonSerializer.Deserialize<List<ServiceClientRegistration>>(
                File.ReadAllText(ServiceClientsFile), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            all.AddRange(fromFile ?? new List<ServiceClientRegistration>());
        }
        return all;
    }

    internal string EffectiveMetadataAddress => string.IsNullOrWhiteSpace(MetadataAddress)
        ? $"https://login.microsoftonline.com/{EntraTenantId}/v2.0/.well-known/openid-configuration"
        : MetadataAddress!;

    /// <summary>The two Entra issuer forms for <see cref="EntraTenantId"/> (v2 and v1 tokens).</summary>
    internal string[] EntraIssuers =>
    [
        $"https://login.microsoftonline.com/{EntraTenantId}/v2.0",
        $"https://sts.windows.net/{EntraTenantId}/",
    ];

    /// <summary>Fails startup on a configuration that would issue service tokens to anyone but the registered identities.</summary>
    public void Validate(string userTokenIssuer, string workloadTokenIssuer, bool isDevelopment)
    {
        if (!Enabled)
            return;

        if (string.IsNullOrWhiteSpace(Issuer))
            throw new InvalidOperationException($"{SectionName}:Issuer is required.");
        if (string.Equals(Issuer, userTokenIssuer, StringComparison.Ordinal)
            || string.Equals(Issuer, workloadTokenIssuer, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{SectionName}:Issuer must differ from TokenSigning:Issuer and WorkloadTokens:Issuer: services trust it as a Service issuer only.");
        if (TokenLifetime <= TimeSpan.Zero || TokenLifetime > TimeSpan.FromMinutes(10))
            throw new InvalidOperationException($"{SectionName}:TokenLifetime must be between 0 and 10 minutes.");

        if (IdentityProvider == ServiceIdentityProvider.Kubernetes)
        {
            ValidateKubernetes(isDevelopment);
            return;
        }

        if (!Guid.TryParse(EntraTenantId, out _))
            throw new InvalidOperationException($"{SectionName}:EntraTenantId must be CHO's Entra directory id (a GUID).");
        if (Audiences.Count == 0 || Audiences.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException($"{SectionName}:Audiences must list the service-token API's App ID URI and/or client id.");
        if (Audiences.Contains(ChoDevelopmentAuth.Audience, StringComparer.Ordinal))
            throw new InvalidOperationException($"{SectionName}:Audiences must be an Entra application, not the CHO API audience.");
        if (ClockSkew < TimeSpan.Zero || ClockSkew > TimeSpan.FromMinutes(5))
            throw new InvalidOperationException($"{SectionName}:ClockSkew must be between 0 and 5 minutes.");
        if (string.IsNullOrWhiteSpace(JwksJson)
            && (!Uri.TryCreate(EffectiveMetadataAddress, UriKind.Absolute, out var metadata)
                || (metadata.Scheme != Uri.UriSchemeHttps && !isDevelopment)))
            throw new InvalidOperationException($"{SectionName}:MetadataAddress must be an https URL (or set JwksJson).");

        var clients = AllServiceClients();
        if (clients.Count == 0)
            throw new InvalidOperationException($"{SectionName}:ServiceClients is empty.");

        var objectIds = new HashSet<Guid>();
        var clientIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in clients)
        {
            var label = $"{SectionName} service client '{c.ClientId}'";
            if (!ClientIdPattern.IsMatch(c.ClientId ?? string.Empty))
                throw new InvalidOperationException($"{label}: ClientId must be a lower-case service name (letters, digits, hyphens).");
            if (ChoWorkloadRole.IsWorkloadClientId(c.ClientId!))
                throw new InvalidOperationException(
                    $"{label}: client ids starting with '{ChoWorkloadRole.ClientIdPrefix}' are workloads (WorkloadTokens), never services.");
            if (!string.IsNullOrWhiteSpace(c.Namespace) || !string.IsNullOrWhiteSpace(c.ServiceAccount))
                throw new InvalidOperationException(
                    $"{label}: Namespace and ServiceAccount are Kubernetes identities; set IdentityProvider=Kubernetes or register ObjectId.");
            // Exactly one managed identity per entry: no wildcard, no list.
            if (!Guid.TryParse(c.ObjectId, out var oid) || oid == Guid.Empty)
                throw new InvalidOperationException($"{label}: ObjectId must be the managed identity's object (principal) id.");
            if (!string.IsNullOrWhiteSpace(c.AppId) && !Guid.TryParse(c.AppId, out _))
                throw new InvalidOperationException($"{label}: AppId, when set, must be the managed identity's client id (a GUID).");
            if (!objectIds.Add(oid))
                throw new InvalidOperationException($"{label}: object id {oid} is registered twice.");
            if (!clientIds.Add(c.ClientId!))
                throw new InvalidOperationException($"{label}: client id is registered twice.");
        }
    }

    private static readonly Regex Dns1123Label = new(@"^[a-z0-9]([-a-z0-9]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant);
    private static readonly Regex Dns1123Subdomain = new(@"^[a-z0-9]([-a-z0-9.]{0,251}[a-z0-9])?$", RegexOptions.CultureInvariant);

    private void ValidateKubernetes(bool isDevelopment)
    {
        var k = Kubernetes;
        if (string.IsNullOrWhiteSpace(k.KubernetesIssuer))
            throw new InvalidOperationException($"{SectionName}:Kubernetes:KubernetesIssuer is required.");
        if (string.IsNullOrWhiteSpace(k.Audience))
            throw new InvalidOperationException($"{SectionName}:Kubernetes:Audience is required.");
        if (string.Equals(k.Audience, ChoDevelopmentAuth.Audience, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{SectionName}:Kubernetes:Audience must be token-service's own audience, not the CHO API audience.");
        if (k.MaxSourceTokenLifetime <= TimeSpan.Zero || k.MaxSourceTokenLifetime > TimeSpan.FromHours(24))
            throw new InvalidOperationException($"{SectionName}:Kubernetes:MaxSourceTokenLifetime must be between 0 and 24 hours.");
        if (k.ClockSkew < TimeSpan.Zero || k.ClockSkew > TimeSpan.FromMinutes(5))
            throw new InvalidOperationException($"{SectionName}:Kubernetes:ClockSkew must be between 0 and 5 minutes.");
        if (k.ToValidatorOptions().StaticJwks() == null)
        {
            var metadata = string.IsNullOrWhiteSpace(k.MetadataAddress) ? k.KubernetesIssuer : k.MetadataAddress;
            if (!Uri.TryCreate(metadata, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && !isDevelopment))
                throw new InvalidOperationException(
                    $"{SectionName}:Kubernetes: set JwksJson/JwksFile, or an https KubernetesIssuer/MetadataAddress for OIDC discovery.");
        }

        var clients = AllServiceClients();
        if (clients.Count == 0)
            throw new InvalidOperationException($"{SectionName}:ServiceClients is empty.");

        var accounts = new HashSet<string>(StringComparer.Ordinal);
        var clientIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in clients)
        {
            var label = $"{SectionName} service client '{c.ClientId}'";
            if (!ClientIdPattern.IsMatch(c.ClientId ?? string.Empty))
                throw new InvalidOperationException($"{label}: ClientId must be a lower-case service name (letters, digits, hyphens).");
            if (ChoWorkloadRole.IsWorkloadClientId(c.ClientId!))
                throw new InvalidOperationException(
                    $"{label}: client ids starting with '{ChoWorkloadRole.ClientIdPrefix}' are workloads (WorkloadTokens), never services.");
            if (!string.IsNullOrWhiteSpace(c.ObjectId) || !string.IsNullOrWhiteSpace(c.AppId))
                throw new InvalidOperationException(
                    $"{label}: ObjectId and AppId are Entra identities; with IdentityProvider=Kubernetes register Namespace and ServiceAccount.");
            // Exactly one service account per entry: no wildcard, no list.
            if (!Dns1123Label.IsMatch(c.Namespace ?? string.Empty))
                throw new InvalidOperationException($"{label}: Namespace is not a Kubernetes namespace name.");
            if (!Dns1123Subdomain.IsMatch(c.ServiceAccount ?? string.Empty))
                throw new InvalidOperationException($"{label}: ServiceAccount is not a Kubernetes service account name.");
            if (!accounts.Add(c.Namespace + "/" + c.ServiceAccount))
                throw new InvalidOperationException($"{label}: service account {c.Namespace}/{c.ServiceAccount} is registered twice.");
            if (!clientIds.Add(c.ClientId!))
                throw new InvalidOperationException($"{label}: client id is registered twice.");
        }
    }
}

/// <summary>
/// The Kubernetes service-account issuer whose projected tokens prove a
/// service's identity (<c>ServiceTokens:Kubernetes</c>). GKE: the cluster's
/// issuer is <c>https://container.googleapis.com/v1/projects/{p}/locations/{l}/clusters/{c}</c>;
/// AKS: the OIDC issuer URL.
/// </summary>
public sealed class KubernetesServiceIdentityOptions
{
    /// <summary>The cluster's service-account issuer, exactly as it appears in <c>iss</c>.</summary>
    public string KubernetesIssuer { get; set; } = string.Empty;

    /// <summary>OIDC discovery document; defaults to <c>{KubernetesIssuer}/.well-known/openid-configuration</c>.</summary>
    public string? MetadataAddress { get; set; }

    /// <summary>The issuer's JWKS as JSON (<c>kubectl get --raw /openid/v1/jwks</c>), instead of discovery.</summary>
    public string? JwksJson { get; set; }

    /// <summary>A file holding the JWKS (a mounted ConfigMap). Read once at startup.</summary>
    public string? JwksFile { get; set; }

    /// <summary>
    /// The audience the projected token must carry. Services project their token
    /// with this audience; it is token-service's own, never the CHO API audience.
    /// </summary>
    public string Audience { get; set; } = "cho-token-service";

    /// <summary>Longest lifetime (<c>exp - iat</c>) accepted, so only short-lived projected tokens are exchanged.</summary>
    public TimeSpan MaxSourceTokenLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Clock skew tolerated on the service-account token.</summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The same checks workload tokens use (<see cref="KubernetesTokenValidator"/>).</summary>
    internal WorkloadTokenOptions ToValidatorOptions() => new()
    {
        KubernetesIssuer = KubernetesIssuer,
        MetadataAddress = MetadataAddress,
        JwksJson = JwksJson,
        JwksFile = JwksFile,
        Audience = Audience,
        MaxSourceTokenLifetime = MaxSourceTokenLifetime,
        ClockSkew = ClockSkew,
    };
}

/// <summary>
/// One CHO service and the workload identity allowed to be it: an Azure managed
/// identity (<see cref="ObjectId"/>, Entra) or a Kubernetes service account
/// (<see cref="Namespace"/> and <see cref="ServiceAccount"/>, Kubernetes).
/// </summary>
public sealed class ServiceClientRegistration
{
    /// <summary>The CHO client id (<c>sub</c> and <c>azp</c> of its service tokens), e.g. <c>claims-service</c>.</summary>
    public string? ClientId { get; set; }

    /// <summary>The managed identity's object (principal) id: the Entra token's <c>oid</c>.</summary>
    public string? ObjectId { get; set; }

    /// <summary>Optional: the managed identity's client id, which must then also match <c>appid</c>/<c>azp</c>.</summary>
    public string? AppId { get; set; }

    /// <summary>The service's Kubernetes namespace. Kubernetes provider only.</summary>
    public string? Namespace { get; set; }

    /// <summary>The service's Kubernetes service account. Kubernetes provider only.</summary>
    public string? ServiceAccount { get; set; }
}
