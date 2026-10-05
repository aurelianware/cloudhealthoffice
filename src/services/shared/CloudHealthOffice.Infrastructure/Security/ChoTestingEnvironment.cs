using Microsoft.Extensions.Hosting;

namespace CloudHealthOffice.Infrastructure.Security;

/// <summary>
/// The "Testing" host environment exists for automated tests. Like
/// Development it accepts the public development keys and certificates, so a
/// deployed host must never run as Testing by accident. A process that runs
/// as Testing inside Kubernetes (<c>KUBERNETES_SERVICE_HOST</c> is set) refuses
/// to start unless <c>CHO_ALLOW_TESTING_ENVIRONMENT=true</c> says it is
/// deliberate (an in-cluster test run), and then warns loudly on stderr.
/// </summary>
public static class ChoTestingEnvironment
{
    public const string Name = "Testing";

    /// <summary>Set to <c>true</c> to run a Testing host inside Kubernetes on purpose.</summary>
    public const string OverrideVariable = "CHO_ALLOW_TESTING_ENVIRONMENT";

    /// <summary>Present in every Kubernetes pod; the marker of a deployed host.</summary>
    public const string KubernetesMarkerVariable = "KUBERNETES_SERVICE_HOST";

    /// <summary>
    /// Throws when <paramref name="environment"/> is Testing on a Kubernetes
    /// host without the explicit override. Any other environment passes.
    /// </summary>
    public static void EnsureNotDeployed(IHostEnvironment environment, Func<string, string?>? variables = null)
    {
        if (!environment.IsEnvironment(Name))
            return;

        variables ??= Environment.GetEnvironmentVariable;
        if (string.IsNullOrEmpty(variables(KubernetesMarkerVariable)))
            return;

        if (!string.Equals(variables(OverrideVariable)?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"ASPNETCORE_ENVIRONMENT is '{Name}' on a Kubernetes host ({KubernetesMarkerVariable} is set). " +
                $"'{Name}' accepts the public development keys and certificates and is for automated tests only. " +
                $"Set the environment to Production/Staging, or set {OverrideVariable}=true for a deliberate in-cluster test run.");
        }

        Console.Error.WriteLine(
            $"WARNING: running as '{Name}' on a Kubernetes host because {OverrideVariable}=true. " +
            "Development keys and certificates are accepted. This host must hold no real data.");
    }

    /// <summary>
    /// Development, or Testing where <see cref="EnsureNotDeployed"/> allows it:
    /// the hosts on which development keys and certificates are tolerated.
    /// </summary>
    public static bool AllowsDevelopmentSecrets(IHostEnvironment environment, Func<string, string?>? variables = null)
    {
        if (environment.IsDevelopment())
            return true;
        if (!environment.IsEnvironment(Name))
            return false;
        EnsureNotDeployed(environment, variables);
        return true;
    }
}
