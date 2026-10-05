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
/// </summary>
public static class ChoTestingEnvironment
{
    public const string Name = "Testing";

    /// <summary>Must be <c>1</c> (or <c>true</c>) for a Testing host to start. Set by the test assemblies.</summary>
    public const string MarkerVariable = "CHO_TESTING";

    /// <summary>Set to <c>true</c> to run a Testing host on a deployed platform on purpose.</summary>
    public const string OverrideVariable = "CHO_ALLOW_TESTING_ENVIRONMENT";

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
    /// <c>CHO_TESTING=1</c>, or on a deployed host without the explicit
    /// override. Any other environment passes.
    /// </summary>
    public static void EnsureNotDeployed(IHostEnvironment environment, Func<string, string?>? variables = null)
    {
        if (!environment.IsEnvironment(Name))
            return;

        variables ??= Environment.GetEnvironmentVariable;
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

    private static bool IsSet(string? value, params string[] accepted)
        => value is not null && accepted.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);
}
