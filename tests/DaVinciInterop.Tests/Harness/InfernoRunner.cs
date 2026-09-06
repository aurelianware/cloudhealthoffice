using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DaVinciInterop.Tests.Harness;

/// <summary>An Inferno suite input, supplied when a test session is created.</summary>
public sealed record InfernoInput
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("value")] public string Value { get; init; } = "";
}

/// <summary>One result row Inferno reports for a test, group or suite.</summary>
public sealed record InfernoResult
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("test_id")] public string? TestId { get; init; }
    [JsonPropertyName("test_group_id")] public string? TestGroupId { get; init; }
    [JsonPropertyName("test_suite_id")] public string? TestSuiteId { get; init; }

    /// <summary>Inferno's own vocabulary: pass, fail, skip, omit, error, wait, running, cancel.</summary>
    [JsonPropertyName("result")] public string Result { get; init; } = "";

    [JsonPropertyName("result_message")] public string? ResultMessage { get; init; }
}

/// <summary>An Inferno test run as reported by its API.</summary>
public sealed record InfernoTestRun
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("test_session_id")] public string? TestSessionId { get; init; }
    [JsonPropertyName("results")] public List<InfernoResult> Results { get; init; } = new();

    public bool IsFinished => Status is "done" or "cancelling" or "cancelled";
}

/// <summary>
/// The automation seam for Inferno conformance kits.
///
/// Inferno Core exposes a JSON API alongside its browser UI: create a test
/// session for a suite, start a test run, poll it, then read structured results.
/// The harness drives that API — it never scrapes the UI, and it never parses
/// rendered HTML, because a conformance result that depends on page markup is not
/// a result worth publishing.
///
/// The kits are not started by any scenario in this PR. Upstream publishes no
/// image for them, so a checkout at the pinned tag has to exist before this runner
/// has anything to talk to (see scripts/interop/fetch-inferno.sh and
/// docs/interop/davinci.md). Everything the next PR needs is here: session
/// creation with CHO's endpoint as suite input, run start, bounded polling, and
/// the mapping from Inferno's vocabulary into <see cref="InteropStatus"/>.
/// </summary>
public sealed class InfernoRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly string _apiBaseUrl;

    public InfernoRunner(string apiBaseUrl, HttpClient? http = null)
    {
        _apiBaseUrl = apiBaseUrl.TrimEnd('/');
        _http = http ?? new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
    }

    /// <summary>
    /// Creates a test session for a suite, handing it the CHO endpoint and any
    /// other configuration the suite declares as input.
    /// </summary>
    public async Task<string> CreateSessionAsync(
        string suiteId,
        IReadOnlyList<InfernoInput> inputs,
        CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync(
            $"{_apiBaseUrl}/test_sessions?test_suite_id={Uri.EscapeDataString(suiteId)}",
            new { suite_options = Array.Empty<object>() },
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(body);
        var sessionId = document.RootElement.GetProperty("id").GetString()
                        ?? throw new InvalidOperationException("Inferno returned a session without an id.");

        if (inputs.Count > 0)
        {
            var setInputs = await _http.PutAsJsonAsync(
                $"{_apiBaseUrl}/test_sessions/{sessionId}/session_data",
                inputs,
                cancellationToken);
            setInputs.EnsureSuccessStatusCode();
        }

        return sessionId;
    }

    /// <summary>Starts a run of the whole suite within an existing session.</summary>
    public async Task<InfernoTestRun> StartSuiteRunAsync(
        string sessionId,
        string suiteId,
        IReadOnlyList<InfernoInput> inputs,
        CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync(
            $"{_apiBaseUrl}/test_runs",
            new { test_session_id = sessionId, test_suite_id = suiteId, inputs },
            cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<InfernoTestRun>(JsonOptions, cancellationToken)
               ?? throw new InvalidOperationException("Inferno returned an unreadable test run.");
    }

    /// <summary>
    /// Polls a run until it finishes or the deadline passes. Bounded: a wedged
    /// conformance kit fails with a diagnostic rather than hanging the job.
    /// </summary>
    public async Task<InfernoTestRun> WaitForCompletionAsync(
        string testRunId,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        InfernoTestRun? last = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await _http.GetFromJsonAsync<InfernoTestRun>(
                       $"{_apiBaseUrl}/test_runs/{testRunId}?include_results=true", JsonOptions, cancellationToken)
                   ?? throw new InvalidOperationException("Inferno returned an unreadable test run.");

            if (last.IsFinished)
            {
                return last;
            }

            await Task.Delay(pollInterval, cancellationToken);
        }

        throw new TimeoutException(
            $"Inferno test run '{testRunId}' did not finish within {timeout.TotalMinutes:0} minutes " +
            $"(last status: {last?.Status ?? "unknown"}).");
    }

    /// <summary>
    /// Maps Inferno's result vocabulary into the harness's.
    ///
    /// Inferno's `omit` and `wait` are not failures — they mean a test did not
    /// apply or needed an interaction that never came — so they map to Skipped
    /// rather than being counted against the run. `error` is a Failed: the suite
    /// could not evaluate the system under test, which is a real interop result.
    /// </summary>
    public static InteropStatus MapStatus(string infernoResult) => infernoResult switch
    {
        "pass" => InteropStatus.Passed,
        "fail" => InteropStatus.Failed,
        "error" => InteropStatus.Failed,
        "cancel" => InteropStatus.Skipped,
        "skip" => InteropStatus.Skipped,
        "omit" => InteropStatus.Skipped,
        "wait" => InteropStatus.Skipped,
        "running" => InteropStatus.NotRun,
        _ => InteropStatus.NotRun,
    };

    /// <summary>
    /// Rolls a finished Inferno run up into a single scenario status: any failure
    /// fails the scenario; otherwise at least one pass is required, because a suite
    /// that skipped everything has proven nothing.
    /// </summary>
    public static InteropStatus RollUp(InfernoTestRun run)
    {
        var statuses = run.Results.Select(r => MapStatus(r.Result)).ToList();
        if (statuses.Count == 0)
        {
            return InteropStatus.NotRun;
        }

        if (statuses.Any(s => s == InteropStatus.Failed))
        {
            return InteropStatus.Failed;
        }

        return statuses.Any(s => s == InteropStatus.Passed) ? InteropStatus.Passed : InteropStatus.Skipped;
    }
}
