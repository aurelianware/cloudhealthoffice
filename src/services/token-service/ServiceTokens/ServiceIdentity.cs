using CloudHealthOffice.TokenService.Workload;

namespace CloudHealthOffice.TokenService.ServiceTokens;

/// <summary>Where a CHO service's workload identity comes from.</summary>
public enum ServiceIdentityProvider
{
    /// <summary>An Entra access token from the pod's Azure workload identity (AKS).</summary>
    Entra,

    /// <summary>
    /// The pod's projected Kubernetes service-account token, validated against
    /// the cluster's service-account issuer. Works on any conformant cluster
    /// (GKE, AKS, EKS, on premises) with no cloud identity service involved.
    /// </summary>
    Kubernetes,
}

/// <summary>
/// The workload identity behind a service-token request, whichever provider
/// proved it. Exactly one of <see cref="Entra"/> or <see cref="Kubernetes"/> is set.
/// </summary>
public sealed record ServiceIdentity(EntraWorkloadIdentity? Entra, KubernetesIdentity? Kubernetes)
{
    public static ServiceIdentity From(EntraWorkloadIdentity identity) => new(identity, null);
    public static ServiceIdentity From(KubernetesIdentity identity) => new(null, identity);
}

public sealed record ServiceIdentityResult(ServiceIdentity? Identity, string? Reason)
{
    public static ServiceIdentityResult Refused(string reason) => new(null, reason);
}

/// <summary>Proves which workload presented a service-token request.</summary>
public interface IServiceIdentityValidator
{
    Task<ServiceIdentityResult> ValidateAsync(string token, CancellationToken ct);
}

/// <summary>Entra workload identity (Azure).</summary>
public sealed class EntraServiceIdentityValidator(IEntraWorkloadTokenValidator inner) : IServiceIdentityValidator
{
    public async Task<ServiceIdentityResult> ValidateAsync(string token, CancellationToken ct)
    {
        var result = await inner.ValidateAsync(token, ct);
        return result.Identity == null
            ? ServiceIdentityResult.Refused(result.Reason ?? "invalid_token")
            : new ServiceIdentityResult(ServiceIdentity.From(result.Identity), null);
    }
}

/// <summary>Kubernetes projected service-account tokens (any conformant cluster).</summary>
public sealed class KubernetesServiceIdentityValidator(IKubernetesTokenValidator inner) : IServiceIdentityValidator
{
    public async Task<ServiceIdentityResult> ValidateAsync(string token, CancellationToken ct)
    {
        var result = await inner.ValidateAsync(token, ct);
        return result.Identity == null
            ? ServiceIdentityResult.Refused(result.Reason ?? "invalid_token")
            : new ServiceIdentityResult(ServiceIdentity.From(result.Identity), null);
    }
}
