using System.Diagnostics;
using System.Text;

namespace DaVinciInterop.Tests.Harness;

/// <summary>Result of one `docker compose` invocation.</summary>
public sealed record ProcessOutcome(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;

    public string Combined => string.IsNullOrWhiteSpace(StandardError)
        ? StandardOutput
        : $"{StandardOutput}{System.Environment.NewLine}{StandardError}";
}

/// <summary>
/// Thin wrapper over the `docker compose` CLI against interop/docker-compose.interop.yml.
///
/// The repository already orchestrates local stacks with Compose profiles
/// (docker-compose.yml), so the harness reuses that pattern rather than adding a
/// second orchestration framework. Every call is bounded by an explicit timeout:
/// a wedged external image must fail with a diagnostic, never hang CI.
/// </summary>
public sealed class DockerCompose
{
    /// <summary>
    /// An extra Compose file layered over the interop stack, from
    /// CHO_INTEROP_COMPOSE_OVERRIDE. Intended for hosts whose outbound HTTPS goes
    /// through a proxy: the external RIs download their Da Vinci IG packages at
    /// startup, and on such a host they need the proxy and its CA to boot at all.
    /// See interop/docker-compose.proxy.example.yml.
    ///
    /// An override may only add host-specific plumbing. It must never change an
    /// image reference — the pin lives in interop/versions.json and is enforced by
    /// InteropVersionsTests.
    /// </summary>
    public const string OverrideFileVariable = "CHO_INTEROP_COMPOSE_OVERRIDE";

    private readonly string _composeFile;
    private readonly string? _overrideFile;
    private readonly string _workingDirectory;
    private readonly string _projectName;

    public DockerCompose(
        string composeFile,
        string workingDirectory,
        string projectName = "cho-interop",
        string? overrideFile = null)
    {
        _composeFile = composeFile;
        _workingDirectory = workingDirectory;
        _projectName = projectName;
        _overrideFile = overrideFile;
    }

    public static DockerCompose ForRepository() =>
        new(InteropPaths.ComposeFile,
            InteropPaths.RepositoryRoot,
            overrideFile: System.Environment.GetEnvironmentVariable(OverrideFileVariable));

    /// <summary>True when a Docker daemon is reachable. Scenarios skip rather than fail when it is not.</summary>
    public static async Task<bool> IsDockerAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var outcome = await RunAsync("docker", ["info", "--format", "{{.ServerVersion}}"],
                Directory.GetCurrentDirectory(), TimeSpan.FromSeconds(20), cancellationToken);
            return outcome.Succeeded;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Starts the services selected by the given profiles and waits for the CLI to return.
    /// </summary>
    /// <param name="buildImages">
    /// False for profiles whose services all come from pinned images — the default,
    /// and the only safe setting for third-party targets. True only for profiles
    /// that build locally from this repository or from a checkout fetched at a pin
    /// (the CHO service, the Inferno kits). Regardless of this flag, `--pull never`
    /// means an image that was not pulled beforehand can never be fetched here, so
    /// an unpinned image cannot slip in mid-scenario.
    /// </param>
    public Task<ProcessOutcome> UpAsync(
        IEnumerable<string> profiles,
        TimeSpan timeout,
        bool buildImages = false,
        CancellationToken cancellationToken = default)
    {
        var args = BaseArgs(profiles);
        args.AddRange(["up", "--detach", "--pull", "never"]);
        args.Add(buildImages ? "--build" : "--no-build");
        return InvokeAsync(args, timeout, cancellationToken);
    }

    /// <summary>
    /// Pulls the pinned images. Separate from <see cref="UpAsync"/> so image
    /// download time is not charged against the startup timeout, and so `up` can
    /// run with `--pull never` — an unpinned image can never be silently fetched
    /// during a scenario.
    /// </summary>
    public Task<ProcessOutcome> PullAsync(IEnumerable<string> profiles, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var args = BaseArgs(profiles);
        // --ignore-buildable: a profile may mix pinned images with services built
        // locally (CHO itself, an Inferno kit checkout). Those have nothing to pull.
        args.AddRange(["pull", "--policy", "missing", "--ignore-buildable"]);
        return InvokeAsync(args, timeout, cancellationToken);
    }

    /// <summary>Tears the stack down, including its volumes and network.</summary>
    public Task<ProcessOutcome> DownAsync(IEnumerable<string> profiles, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var args = BaseArgs(profiles);
        args.AddRange(["down", "--volumes", "--remove-orphans", "--timeout", "30"]);
        return InvokeAsync(args, timeout, cancellationToken);
    }

    /// <summary>Captures a service's container logs for failure diagnostics.</summary>
    public async Task<string> LogsAsync(string service, int tailLines, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var args = BaseArgs([]);
        args.AddRange(["logs", "--no-color", "--tail", tailLines.ToString(), service]);
        var outcome = await InvokeAsync(args, timeout, cancellationToken);
        return outcome.Combined;
    }

    /// <summary>Reports whether a service's container is running.</summary>
    public async Task<bool> IsRunningAsync(string service, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var args = BaseArgs([]);
        args.AddRange(["ps", "--status", "running", "--format", "{{.Service}}"]);
        var outcome = await InvokeAsync(args, timeout, cancellationToken);
        return outcome.Succeeded
               && outcome.StandardOutput
                   .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                   .Any(line => line.Trim() == service);
    }

    private List<string> BaseArgs(IEnumerable<string> profiles)
    {
        var args = new List<string> { "compose", "--project-name", _projectName, "--file", _composeFile };
        if (!string.IsNullOrWhiteSpace(_overrideFile))
        {
            args.AddRange(["--file", _overrideFile]);
        }

        foreach (var profile in profiles)
        {
            args.AddRange(["--profile", profile]);
        }

        return args;
    }

    private Task<ProcessOutcome> InvokeAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken cancellationToken) =>
        RunAsync("docker", args, _workingDirectory, timeout, cancellationToken);

    private static async Task<ProcessOutcome> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException(
                $"`{fileName} {string.Join(' ', arguments)}` did not finish within {timeout.TotalSeconds:0}s. " +
                $"Output so far:{System.Environment.NewLine}{stdout}{stderr}");
        }

        return new ProcessOutcome(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or SystemException)
        {
            // The process died on its own between the check and the kill; nothing to clean up.
        }
    }
}
