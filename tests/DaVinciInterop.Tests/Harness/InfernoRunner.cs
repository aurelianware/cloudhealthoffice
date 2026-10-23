using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// An Inferno suite input, supplied when a test session is created.
///
/// <para>
/// <see cref="Type"/> matters for anything that is not plain text. Inferno's
/// session-data repository switches on it
/// (<c>SessionData#value_to_persist</c>) and raises
/// <c>UnknownSessionDataType</c> when a non-text input arrives without one, so
/// an <c>auth_info</c> or <c>checkbox</c> input sent as bare text is rejected
/// rather than ignored. It is omitted from the JSON when null, so a text input
/// is sent exactly as before.
/// </para>
/// </summary>
public sealed record InfernoInput
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("value")] public string Value { get; init; } = "";

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }

    /// <summary>
    /// A bearer token handed straight to the suite's FHIR client, as the
    /// <c>auth_info</c> input a US Core or CARIN BB server suite declares.
    ///
    /// <para>
    /// <c>auth_type: public</c> with an <c>access_token</c> and no token
    /// endpoint tells Inferno the authorization already happened and this is
    /// the result — which is true: the token is minted against CHO's own
    /// development issuer rather than fetched through a launch the suite would
    /// have to drive. The value is a JSON <em>string</em>, not a nested object,
    /// because that is what the repository parses.
    /// </para>
    /// </summary>
    public static InfernoInput AccessToken(string name, string accessToken) => new()
    {
        Name = name,
        Type = "auth_info",
        Value = JsonSerializer.Serialize(new { auth_type = "public", access_token = accessToken }),
    };
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

/// <summary>
/// A group as the suite description reports it, with its children.
///
/// <para>
/// The <see cref="Id"/> here is the <em>runnable</em> id, which is not the id
/// the kit's source declares. Inferno prefixes a nested group with its
/// ancestors — <c>us_core_v311_patient</c>, declared in the kit, is addressable
/// only as <c>us_core_v311-us_core_v311_fhir_api-us_core_v311_patient</c>. The
/// kit's own code walks its children with <c>id.ends_with?</c> for the same
/// reason.
/// </para>
/// </summary>
public sealed record InfernoTestGroup
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("test_groups")] public List<InfernoTestGroup> TestGroups { get; init; } = new();
}

/// <summary>A suite as Inferno's API describes it.</summary>
public sealed record InfernoTestSuite
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("test_groups")] public List<InfernoTestGroup> TestGroups { get; init; } = new();

    /// <summary>Every group in the suite, depth first, including nested ones.</summary>
    public IEnumerable<InfernoTestGroup> AllGroups()
    {
        IEnumerable<InfernoTestGroup> Walk(IEnumerable<InfernoTestGroup> groups) =>
            groups.SelectMany(group => new[] { group }.Concat(Walk(group.TestGroups)));

        return Walk(TestGroups);
    }
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
/// Upstream publishes no image for these kits, so a checkout at the pinned tag has
/// to exist before this runner has anything to talk to (see
/// scripts/interop/fetch-inferno.sh). <see cref="InfernoSuiteScenario"/> is what
/// drives it: INFERNO-USCORE-SERVER-001 and INFERNO-C4BB-SERVER-001 run through
/// here.
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
    public Task<InfernoTestRun> StartSuiteRunAsync(
        string sessionId,
        string suiteId,
        IReadOnlyList<InfernoInput> inputs,
        CancellationToken cancellationToken = default)
        => StartRunAsync(
            new { test_session_id = sessionId, test_suite_id = suiteId, inputs }, cancellationToken);

    /// <summary>
    /// Starts a run of one group within an existing session.
    ///
    /// <para>
    /// This is how CHO is judged honestly rather than generously. The US Core
    /// server suite covers roughly thirty clinical resource types — allergies,
    /// care plans, immunizations, vital signs — and CHO is a payer that
    /// publishes almost none of them. A whole-suite run would report a wall of
    /// failures for resources CHO has never claimed, burying whatever the
    /// Patient and Encounter groups actually found. Naming the groups says
    /// which claim is being tested; it does not soften the verdict on those
    /// groups, and <see cref="RollUp"/> still fails on any failure within them.
    /// </para>
    /// </summary>
    public Task<InfernoTestRun> StartGroupRunAsync(
        string sessionId,
        string groupId,
        IReadOnlyList<InfernoInput> inputs,
        CancellationToken cancellationToken = default)
        => StartRunAsync(
            new { test_session_id = sessionId, test_group_id = groupId, inputs }, cancellationToken);

    /// <summary>Reads a suite's structure, including every nested group.</summary>
    public async Task<InfernoTestSuite> DescribeSuiteAsync(
        string suiteId, CancellationToken cancellationToken = default)
        => await _http.GetFromJsonAsync<InfernoTestSuite>(
               $"{_apiBaseUrl}/test_suites/{Uri.EscapeDataString(suiteId)}", JsonOptions, cancellationToken)
           ?? throw new InvalidOperationException($"Inferno returned an unreadable description of '{suiteId}'.");

    /// <summary>
    /// Resolves the id the kit's source declares for a group into the runnable
    /// id Inferno addresses it by.
    ///
    /// <para>
    /// Asked of the running kit rather than composed from a naming rule. A rule
    /// would be a guess about how a particular suite nests its groups, and a
    /// wrong guess here does not fail loudly — it names a group that does not
    /// exist, and a conformance run that silently tested nothing is the one
    /// outcome this harness must never produce. Resolution is exact-or-suffix,
    /// and an ambiguous short id is an error rather than a first match.
    /// </para>
    /// </summary>
    public static string ResolveGroupId(InfernoTestSuite suite, string declaredGroupId)
    {
        var groups = suite.AllGroups().ToList();

        var matches = groups
            .Where(group => group.Id == declaredGroupId
                            || group.Id.EndsWith($"-{declaredGroupId}", StringComparison.Ordinal))
            .ToList();

        if (matches.Count == 1)
        {
            return matches[0].Id;
        }

        if (matches.Count == 0)
        {
            throw new InvalidOperationException(
                $"Suite '{suite.Id}' has no group '{declaredGroupId}'. Groups it does have: " +
                string.Join(", ", groups.Select(group => group.Id)));
        }

        throw new InvalidOperationException(
            $"'{declaredGroupId}' is ambiguous in suite '{suite.Id}': it matches " +
            string.Join(", ", matches.Select(group => group.Id)) +
            ". Name the group by its full runnable id.");
    }

    private async Task<InfernoTestRun> StartRunAsync(object body, CancellationToken cancellationToken)
    {
        var response = await _http.PostAsJsonAsync($"{_apiBaseUrl}/test_runs", body, cancellationToken);
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
