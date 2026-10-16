using Microsoft.Extensions.Hosting;

namespace CloudHealthOffice.Infrastructure.Security;

/// <summary>
/// The "Testing" host environment exists for automated tests. Like
/// Development it accepts the public development keys and certificates, so a
/// deployed host must never run as Testing by accident. Two rules:
/// <list type="number">
/// <item>Opt-in: a Testing host refuses to start unless
/// <c>CHO_TESTING=1</c> is set. Every test assembly sets it for its own
/// process (a module initializer added to every <c>*Tests</c> project by the
/// repository's <c>Directory.Build.targets</c>); nothing deployed sets it.</item>
/// <item>Deployed hosts: even with the marker, a process that runs as Testing
/// where a hosting platform's marker is present (Kubernetes
/// <c>KUBERNETES_SERVICE_HOST</c>, Azure App Service <c>WEBSITE_SITE_NAME</c>,
/// Azure Container Apps <c>CONTAINER_APP_NAME</c>) refuses to start unless
/// <c>CHO_ALLOW_TESTING_ENVIRONMENT=true</c> says it is deliberate (an
/// in-cluster test run), and then warns loudly on stderr.</item>
/// </list>
/// Development accepts the same keys, so it gets the second rule too: a
/// Development process on a deployed host refuses to start unless
/// <c>CHO_ALLOW_DEV_ON_DEPLOYED_HOST=true</c> says so (the port-forward-only
/// GKE demo, <c>deploy/gcp/render_manifests.py</c>), and then warns loudly.
/// Development off any platform (a laptop, docker compose) is unaffected.
/// </summary>
public static class ChoTestingEnvironment
{
    public const string Name = "Testing";

    /// <summary>Must be <c>1</c> (or <c>true</c>) for a Testing host to start. Set by the test assemblies.</summary>
    public const string MarkerVariable = "CHO_TESTING";

    /// <summary>Set to <c>true</c> to run a Testing host on a deployed platform on purpose.</summary>
    public const string OverrideVariable = "CHO_ALLOW_TESTING_ENVIRONMENT";

    /// <summary>Set to <c>true</c> to run a Development host on a deployed platform on purpose.</summary>
    public const string DevelopmentOverrideVariable = "CHO_ALLOW_DEV_ON_DEPLOYED_HOST";

    /// <summary>Present in every Kubernetes pod.</summary>
    public const string KubernetesMarkerVariable = "KUBERNETES_SERVICE_HOST";

    /// <summary>Present on Azure App Service.</summary>
    public const string AppServiceMarkerVariable = "WEBSITE_SITE_NAME";

    /// <summary>Present on Azure Container Apps.</summary>
    public const string ContainerAppsMarkerVariable = "CONTAINER_APP_NAME";

    /// <summary>The markers of a deployed host.</summary>
    public static readonly IReadOnlyList<string> DeployedMarkerVariables =
        [KubernetesMarkerVariable, AppServiceMarkerVariable, ContainerAppsMarkerVariable];

    /// <summary>
    /// Throws when <paramref name="environment"/> is Testing without
    /// <c>CHO_TESTING=1</c>, or is Testing or Development on a deployed host
    /// without that environment's explicit override. Production, Staging and
    /// any other environment pass.
    /// </summary>
    public static void EnsureNotDeployed(IHostEnvironment environment, Func<string, string?>? variables = null)
    {
        variables ??= Environment.GetEnvironmentVariable;
        if (environment.IsDevelopment())
        {
            EnsureDevelopmentNotDeployed(environment.EnvironmentName, variables);
            return;
        }

        if (!environment.IsEnvironment(Name))
            return;

        if (!IsSet(variables(MarkerVariable), "1", "true"))
        {
            throw new InvalidOperationException(
                $"ASPNETCORE_ENVIRONMENT is '{Name}' but {MarkerVariable}=1 is not set. " +
                $"'{Name}' accepts the public development keys and certificates and is for automated tests only " +
                $"(test assemblies set {MarkerVariable}=1 themselves). Set the environment to Production/Staging/Development.");
        }

        var deployed = DeployedMarkerVariables.FirstOrDefault(v => !string.IsNullOrEmpty(variables(v)));
        if (deployed is null)
            return;

        if (!IsSet(variables(OverrideVariable), "true"))
        {
            throw new InvalidOperationException(
                $"ASPNETCORE_ENVIRONMENT is '{Name}' on a deployed host ({deployed} is set). " +
                $"'{Name}' accepts the public development keys and certificates and is for automated tests only. " +
                $"Set the environment to Production/Staging, or set {OverrideVariable}=true for a deliberate in-cluster test run.");
        }

        Console.Error.WriteLine(
            $"WARNING: running as '{Name}' on a deployed host ({deployed} is set) because {OverrideVariable}=true. " +
            "Development keys and certificates are accepted. This host must hold no real data.");
    }

    private static void EnsureDevelopmentNotDeployed(string environmentName, Func<string, string?> variables)
    {
        var deployed = DeployedMarkerVariables.FirstOrDefault(v => !string.IsNullOrEmpty(variables(v)));
        if (deployed is null)
            return;

        if (!IsSet(variables(DevelopmentOverrideVariable), "true"))
        {
            throw new InvalidOperationException(
                $"ASPNETCORE_ENVIRONMENT is '{environmentName}' on a deployed host ({deployed} is set). " +
                "Development accepts the public development keys and certificates, shows exception details and " +
                "relaxes other production checks. Set the environment to Production/Staging, or set " +
                $"{DevelopmentOverrideVariable}=true for a deliberate demo that holds no real data.");
        }

        Console.Error.WriteLine(
            $"WARNING: running as '{environmentName}' on a deployed host ({deployed} is set) because " +
            $"{DevelopmentOverrideVariable}=true. Development keys and certificates are accepted and exception " +
            "details are returned to callers. This host must hold no real data and must not be publicly reachable.");
    }

    /// <summary>
    /// Development or Testing where <see cref="EnsureNotDeployed"/> allows it:
    /// the hosts on which development keys and certificates are tolerated.
    /// </summary>
    public static bool AllowsDevelopmentSecrets(IHostEnvironment environment, Func<string, string?>? variables = null)
    {
        if (!environment.IsDevelopment() && !environment.IsEnvironment(Name))
            return false;
        EnsureNotDeployed(environment, variables);
        return true;
    }

    private static bool IsSet(string? value, params string[] accepted)
        => value is not null && accepted.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);
}
