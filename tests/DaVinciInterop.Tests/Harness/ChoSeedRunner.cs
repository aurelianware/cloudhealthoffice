using System.Diagnostics;

namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// Runs <c>scripts/interop/seed-cho.mjs</c> against a started <c>interop-cho</c>
/// profile.
///
/// <para>
/// The harness shells out to the script rather than reimplementing the seed in
/// C#. A second implementation would be a second thing to keep true: the
/// script is what a developer runs by hand, and a conformance run that seeded
/// differently from the documented command would be judging a system nobody
/// else can reproduce. The script is idempotent, so a re-run against a stack
/// that is already seeded is a verify pass rather than a duplicate.
/// </para>
/// </summary>
public static class ChoSeedRunner
{
    /// <summary>
    /// Seeds and verifies, and returns the script's combined output so a failure
    /// carries the service's own message into the scenario's evidence.
    /// </summary>
    /// <exception cref="InteropEnvironmentException">The seed or its verify pass failed.</exception>
    public static async Task<string> SeedAsync(
        IReadOnlyDictionary<string, string> serviceUrls,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var script = Path.Combine(InteropPaths.RepositoryRoot, "scripts", "interop", "seed-cho.mjs");
        if (!File.Exists(script))
        {
            throw new InteropEnvironmentException($"The seed script is missing at '{script}'.");
        }

        var startInfo = new ProcessStartInfo("node")
        {
            WorkingDirectory = InteropPaths.RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(script);

        foreach (var (name, value) in serviceUrls)
        {
            startInfo.Environment[name] = value;
        }

        using var process = Process.Start(startInfo)
                            ?? throw new InteropEnvironmentException(
                                "Could not start node. The interop-cho seed is a Node script; CI installs Node 22.");

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new InteropEnvironmentException(
                $"The interop-cho seed did not finish within {timeout.TotalMinutes:0} minutes.");
        }

        var combined = $"{await stdout}{Environment.NewLine}{await stderr}".Trim();

        if (process.ExitCode != 0)
        {
            throw new InteropEnvironmentException(
                "Seeding the interop-cho profile failed, so no conformance suite was run: a suite reading " +
                "an empty or half-written system reports failures that say nothing about CHO's " +
                $"conformance.{Environment.NewLine}{Redaction.Body(combined)}");
        }

        return Redaction.Body(combined);
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or SystemException)
        {
            // Already gone. The timeout is what the caller is told about.
        }
    }
}
