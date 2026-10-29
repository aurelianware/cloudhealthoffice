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
        using (var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) })
        {
            await WaitUntilLiveAsync(
                http, serviceUrls.Values, InteropSettings.Timeouts.Readiness, InteropSettings.Timeouts.ReadinessPoll,
                cancellationToken);
        }

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

    /// <summary>
    /// Waits until every service the seed calls answers <c>/health/live</c>.
    ///
    /// <para>
    /// <c>docker compose up --detach</c> returns once each container has
    /// started, not once it listens. fhir-service is the last CHO service to
    /// start (it depends on all the others), so a seed that begins the moment
    /// the kit answers can reach it before Kestrel is up. Docker's port proxy
    /// then resets the connection, which is how INFERNO-BULK-SERVER-001, the
    /// first scenario of a run and so the one starting from cold, failed in
    /// run 40937514337 with ECONNRESET before its suite ran.
    /// </para>
    /// </summary>
    /// <exception cref="InteropEnvironmentException">A service was not live within the timeout.</exception>
    public static async Task WaitUntilLiveAsync(
        HttpClient http,
        IEnumerable<string> serviceUrls,
        TimeSpan timeout,
        TimeSpan poll,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        foreach (var baseUrl in serviceUrls.Distinct(StringComparer.Ordinal))
        {
            var url = baseUrl.TrimEnd('/') + "/health/live";
            string last = "no answer yet";
            while (true)
            {
                try
                {
                    using var response = await http.GetAsync(url, cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        break;
                    }

                    last = $"HTTP {(int)response.StatusCode}";
                }
                catch (HttpRequestException ex)
                {
                    last = ex.Message;
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    last = "the request timed out";
                }

                if (stopwatch.Elapsed >= timeout)
                {
                    throw new InteropEnvironmentException(
                        $"{url} was not live within {timeout.TotalSeconds:0} seconds ({last}), so the " +
                        "interop-cho profile was not seeded and no conformance suite was run.");
                }

                await Task.Delay(poll, cancellationToken);
            }
        }
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
