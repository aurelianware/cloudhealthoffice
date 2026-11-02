using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CloudHealthOffice.Tools.PaTimelinessChallenge;

public sealed record PaRunOptions
{
    public required Uri AuthorizationUrl { get; init; }
    public required Uri FhirUrl { get; init; }
    public required string TenantId { get; init; }
    public required string BearerToken { get; init; }
    public required string OutputDirectory { get; init; }
    public bool History { get; init; } = true;
    public bool Live { get; init; } = true;
    public int Parallelism { get; init; } = 8;
}

/// <summary>
/// Runs a corpus against a deployed CHO stack and scores it:
/// <list type="number">
/// <item>Baseline: the history period's metrics before anything is loaded, to spot a tenant that already holds other requests.</item>
/// <item>History: import the CSV, record the sourced extension and appeal facts, then score the operational
/// metrics (all and per line of business), the UM decision-timeliness rows (every request) and the annual draft
/// (per line of business). History is scored before the live cohort is sent.</item>
/// <item>Live: submit each case through PAS, take the scripted extension, decide it as the reviewer,
/// read it back and score it, then score the live window's metrics against what CHO recorded.</item>
/// </list>
/// The run never adjudicates on the side and never edits a record except through the endpoints a plan's staff use.
/// </summary>
public sealed class PaChallengeRunner
{
    private readonly ChoApi _api;
    private readonly PaRunOptions _options;
    private readonly List<PaCheck> _checks = [];
    private readonly List<string> _notes = [];
    private readonly Dictionary<string, double> _phaseSeconds = [];
    private readonly List<double> _submitMilliseconds = [];

    public PaChallengeRunner(ChoApi api, PaRunOptions options)
    {
        _api = api;
        _options = options;
    }

    public async Task<PaRunSummary> RunAsync(PaCorpusProfile profile, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var cases = PaCorpusGenerator.Generate(profile);
        PaCorpusWriter.WriteAll(Path.Combine(_options.OutputDirectory, "corpus"), profile, cases);
        var history = cases.Where(c => c.Cohort == PaCohort.History).ToList();
        var live = cases.Where(c => c.Cohort == PaCohort.Live).ToList();

        if (_options.History && history.Count > 0) await Phase("history", () => RunHistoryAsync(profile, history, ct));
        if (_options.Live && live.Count > 0) await Phase("live", () => RunLiveAsync(live, ct));

        var summary = new PaRunSummary
        {
            GeneratorVersion = PaCorpusGenerator.Version,
            Seed = profile.Seed,
            Count = cases.Count,
            TenantId = _options.TenantId,
            StartedAt = started,
            CompletedAt = DateTimeOffset.UtcNow,
            PeriodStart = profile.ResolvedPeriodStart,
            PeriodEnd = profile.ResolvedPeriodEnd,
            PhaseSeconds = _phaseSeconds,
            LiveSubmitP50Ms = Percentile(_submitMilliseconds, 0.50),
            LiveSubmitP95Ms = Percentile(_submitMilliseconds, 0.95),
            Checks = _checks,
            Notes = _notes,
        };
        Directory.CreateDirectory(_options.OutputDirectory);
        await File.WriteAllTextAsync(Path.Combine(_options.OutputDirectory, "run-summary.json"),
            JsonSerializer.Serialize(summary, new JsonSerializerOptions(PaCorpusWriter.Json) { WriteIndented = true }), ct);
        await File.WriteAllTextAsync(Path.Combine(_options.OutputDirectory, "run-summary.md"), summary.ToMarkdown(), ct);
        return summary;
    }

    private async Task RunHistoryAsync(PaCorpusProfile profile, List<PaCase> history, CancellationToken ct)
    {
        var from = profile.ResolvedPeriodStart;
        var to = profile.ResolvedPeriodEnd;
        var expected = PaExpectedMetrics.Compute("all", history);

        var baseline = await _api.MetricsAsync(from, to, null, ct);
        var before = baseline.Body?["totalRequests"]?.GetValue<int>();
        if (!baseline.Ok) Fail("history", "baseline metrics", "200", $"{(int)baseline.Status} {Trim(baseline.Text)}");
        else if (before is > 0 && before != expected.TotalRequests)
            _notes.Add($"The tenant already held {before} in-scope requests received in the history period before this run " +
                $"(the corpus has {expected.TotalRequests}). Metrics include them; use a dedicated tenant for a clean score.");

        // 1. Import.
        var import = await _api.ImportCsvAsync(PaCorpusWriter.HistoryCsv(history), "pa-timeliness-challenge.csv", ct);
        var items = import.Body?["authorizations"] as JsonArray ?? [];
        var ids = items.Where(i => i?["choAuthorizationId"] is not null)
            .ToDictionary(i => i!["authorizationNumber"]!.GetValue<string>(), i => i!["choAuthorizationId"]!.GetValue<string>(), StringComparer.Ordinal);
        var rejected = import.Body?["rejected"]?.GetValue<int>() ?? -1;
        _checks.Add(new PaCheck("history", "import", $"{history.Count} loaded, 0 rejected",
            import.Ok ? $"{ids.Count} loaded, {rejected} rejected (created {import.Body?["created"]}, updated {import.Body?["updated"]}, unchanged {import.Body?["unchanged"]})"
                : $"{(int)import.Status} {Trim(import.Text)}",
            import.Ok && ids.Count == history.Count && rejected == 0));
        foreach (var bad in items.Where(i => (i?["errors"] as JsonArray)?.Count > 0).Take(5))
            _notes.Add($"Import rejected {bad!["authorizationNumber"]}: {string.Join("; ", (bad["errors"] as JsonArray)!.Select(e => e?.GetValue<string>()))}");
        if (!import.Ok) return;

        // 2. Sourced facts. A rerun returns the same fact (idempotent by event id).
        var facts = PaCorpusWriter.ReportingFacts(history).ToList();
        var factFailures = 0;
        await ForEachAsync(facts, async f =>
        {
            if (!ids.TryGetValue(f.AuthorizationNumber, out var id)) { Interlocked.Increment(ref factFailures); return; }
            var r = await _api.RecordFactAsync(id, f, ct);
            if (!r.Ok)
            {
                if (Interlocked.Increment(ref factFailures) <= 5)
                    lock (_notes) _notes.Add($"Fact {f.EventId} refused: {(int)r.Status} {Trim(r.Text)}");
            }
        }, ct);
        _checks.Add(new PaCheck("history", "reporting facts", $"{facts.Count} recorded", $"{facts.Count - factFailures} recorded", factFailures == 0));

        // 3. Operational metrics, all and per line of business.
        _checks.AddRange(PaScorer.CompareMetrics("history/metrics/all", expected, (await _api.MetricsAsync(from, to, null, ct)).Body));
        foreach (var lob in Enum.GetValues<PaLineOfBusiness>())
        {
            var want = PaExpectedMetrics.Compute(lob.ToString(), history.Where(c => c.LineOfBusiness == lob));
            _checks.AddRange(PaScorer.CompareMetrics($"history/metrics/{lob}", want, (await _api.MetricsAsync(from, to, lob, ct)).Body));
        }

        // 4. Every request's timeliness, from the UM report, in windows small enough not to be cut at 1,000 rows.
        var rows = new List<JsonNode>();
        var truncated = false;
        for (var windowStart = from; windowStart < to; windowStart = windowStart.AddDays(7))
        {
            var windowEnd = windowStart.AddDays(7) < to ? windowStart.AddDays(7) : to;
            var report = await _api.DecisionTimelinessAsync(windowStart, windowEnd, ct);
            if (!report.Ok) { Fail("history/timeliness", $"report {windowStart:yyyy-MM-dd}", "200", $"{(int)report.Status} {Trim(report.Text)}"); continue; }
            if (report.Body?["items"] is not JsonArray windowRows)
            {
                Fail("history/timeliness", $"report {windowStart:yyyy-MM-dd}", "a report run with items", Trim(report.Text));
                continue;
            }
            truncated |= report.Body?["truncated"]?.GetValue<bool>() == true;
            rows.AddRange(windowRows.OfType<JsonNode>());
        }
        if (truncated) _notes.Add("A decision-timeliness window was cut at 1,000 rows; missing requests below may be that, not a defect.");
        var timeliness = PaScorer.ScoreTimelinessRows(history, rows, DateTimeOffset.UtcNow);
        _checks.Add(new PaCheck("history/timeliness", "per-request on-time and extension flags",
            $"{history.Count(c => c.Expected.InScope)} requests, 0 mismatched, 0 missing",
            $"{timeliness.Scored} scored, {timeliness.Mismatched} mismatched, {timeliness.Missing} missing, {timeliness.Unexpected} not in the corpus",
            timeliness.Mismatched == 0 && timeliness.Missing == 0));
        _notes.AddRange(timeliness.Examples);
        if (timeliness.Unexpected > 0)
            _notes.Add($"{timeliness.Unexpected} rows in the history period are not corpus requests (the tenant holds other data).");

        // 5. Annual draft per line of business, for each receipt year.
        foreach (var year in history.Select(c => c.ReceivedAt.UtcDateTime.Year).Distinct().Order())
        foreach (var lob in Enum.GetValues<PaLineOfBusiness>())
        {
            var draft = await _api.AnnualDraftAsync(year, lob, ct);
            _checks.AddRange(PaScorer.CompareAnnual($"history/annual/{year}/{lob}", PaExpectedAnnual.Compute(history, lob, year),
                draft.Ok ? draft.Body : null));
        }
    }

    private async Task RunLiveAsync(List<PaCase> live, CancellationToken ct)
    {
        var windowStart = DateTimeOffset.UtcNow.AddSeconds(-1);
        var observations = new PaScorer.LiveObservation[live.Count];
        await ForEachAsync(Enumerable.Range(0, live.Count), async i => observations[i] = await RunLiveCaseAsync(live[i], ct), ct);
        var windowEnd = DateTimeOffset.UtcNow.AddSeconds(1);

        for (var i = 0; i < live.Count; i++) _checks.AddRange(PaScorer.ScoreLive(live[i], observations[i]));
        var auto = observations.Count(o => o.AutoDecided);
        if (auto > 0)
            _notes.Add($"{auto} live requests were decided by a rule on submit, so the scripted reviewer decision did not run for them " +
                "(see the 'reviewable' checks). The live metrics below still use what CHO recorded.");
        if (live.Any(c => c.LineOfBusiness == PaLineOfBusiness.Exchange))
            _notes.Add("PAS has no Exchange (QHP) claim filing code, so live Exchange requests are sent and recorded as Commercial.");

        var observed = live.Select((c, i) => PaScorer.AsObserved(c, observations[i])).OfType<PaCase>().ToList();
        _checks.AddRange(PaScorer.CompareMetrics("live/metrics/all", PaExpectedMetrics.Compute("live", observed),
            (await _api.MetricsAsync(windowStart, windowEnd, null, ct)).Body));
    }

    private async Task<PaScorer.LiveObservation> RunLiveCaseAsync(PaCase c, CancellationToken ct)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var submit = await _api.PasSubmitAsync(PasRequestBuilder.Build(c, DateTimeOffset.UtcNow), ct);
            sw.Stop();
            lock (_submitMilliseconds) _submitMilliseconds.Add(sw.Elapsed.TotalMilliseconds);
            var claimResponse = (submit.Body?["entry"] as JsonArray ?? [])
                .Select(e => e?["resource"]).FirstOrDefault(r => r?["resourceType"]?.GetValue<string>() == "ClaimResponse")
                ?? (submit.Body?["resourceType"]?.GetValue<string>() == "ClaimResponse" ? submit.Body : null);
            var number = claimResponse?["preAuthRef"]?.GetValue<string>();
            if (!submit.Ok || number is null)
                return new PaScorer.LiveObservation { CaseId = c.CaseId, Error = $"PAS $submit {(int)submit.Status}: {Trim(submit.Text)}" };

            var read = await _api.AuthorizationByNumberAsync(number, ct);
            var id = read.Body?["id"]?.GetValue<string>();
            if (!read.Ok || id is null)
                return new PaScorer.LiveObservation { CaseId = c.CaseId, AuthorizationNumber = number, Error = $"read {(int)read.Status}: {Trim(read.Text)}" };
            var auto = PaScorer.Outcome(read.Body) != PaOutcome.Open;

            int? extensionStatus = null, decisionStatus = null;
            if (!auto && c.Extension is { } e)
                extensionStatus = (int)(await _api.ExtendAsync(id, e.Reason, ct)).Status;
            if (!auto && c.Decision is not null)
                decisionStatus = (int)(await _api.DecideAsync(id, c, ct)).Status;

            var final = await _api.AuthorizationAsync(id, ct);
            return new PaScorer.LiveObservation
            {
                CaseId = c.CaseId,
                AuthorizationNumber = number,
                AuthorizationId = id,
                SubmitOutcome = claimResponse?["outcome"]?.GetValue<string>(),
                AutoDecided = auto,
                ExtensionStatusCode = extensionStatus,
                DecisionStatusCode = decisionStatus,
                Authorization = final.Ok ? final.Body : read.Body,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or FormatException)
        {
            return new PaScorer.LiveObservation { CaseId = c.CaseId, Error = ex.Message };
        }
    }

    private async Task Phase(string name, Func<Task> run)
    {
        var sw = Stopwatch.StartNew();
        Console.WriteLine($"==> {name}");
        await run();
        _phaseSeconds[name] = Math.Round(sw.Elapsed.TotalSeconds, 1);
    }

    private Task ForEachAsync<T>(IEnumerable<T> items, Func<T, Task> body, CancellationToken ct) =>
        Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _options.Parallelism), CancellationToken = ct },
            async (item, _) => await body(item));

    private void Fail(string area, string name, string expected, string actual) =>
        _checks.Add(new PaCheck(area, name, expected, actual, false));

    private static string Trim(string text) => text.Length <= 300 ? text : text[..300] + "…";

    private static double? Percentile(List<double> values, double p)
    {
        if (values.Count == 0) return null;
        var sorted = values.Order().ToList();
        return Math.Round(sorted[(int)Math.Min(sorted.Count - 1, Math.Ceiling(p * sorted.Count) - 1)], 1);
    }
}

/// <summary>A run's result: every check, the notes a reader needs, and timings.</summary>
public sealed record PaRunSummary
{
    public required string GeneratorVersion { get; init; }
    public required int Seed { get; init; }
    public required int Count { get; init; }
    public required string TenantId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public required DateTimeOffset PeriodStart { get; init; }
    public required DateTimeOffset PeriodEnd { get; init; }
    public string DataClass => "synthetic";
    public required IReadOnlyDictionary<string, double> PhaseSeconds { get; init; }
    public double? LiveSubmitP50Ms { get; init; }
    public double? LiveSubmitP95Ms { get; init; }
    public required IReadOnlyList<PaCheck> Checks { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
    public int Passed => Checks.Count(c => c.Passed);
    public int Failed => Checks.Count(c => !c.Passed);

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# PA Timeliness Challenge run").AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Synthetic data. Seed {Seed}, {Count:N0} requests, tenant `{TenantId}`, history {PeriodStart:yyyy-MM-dd} to {PeriodEnd:yyyy-MM-dd}, run {StartedAt:yyyy-MM-dd HH:mm} UTC.").AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"**{Passed:N0} checks passed, {Failed:N0} failed.**").AppendLine();
        var phases = string.Join(", ", PhaseSeconds.Select(p => $"{p.Key} {p.Value}s"));
        if (phases.Length > 0) sb.AppendLine(CultureInfo.InvariantCulture, $"Phases: {phases}. PAS $submit p50 {LiveSubmitP50Ms} ms, p95 {LiveSubmitP95Ms} ms.").AppendLine();
        sb.AppendLine("| Area | Checks | Failed |").AppendLine("|---|---|---|");
        foreach (var g in Checks.GroupBy(c => c.Area.StartsWith("live/PTC", StringComparison.Ordinal) ? "live/requests" : c.Area))
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {g.Key} | {g.Count()} | {g.Count(c => !c.Passed)} |");
        var failed = Checks.Where(c => !c.Passed).ToList();
        if (failed.Count > 0)
        {
            sb.AppendLine().AppendLine("## Failed checks").AppendLine().AppendLine("| Area | Check | Expected | CHO |").AppendLine("|---|---|---|---|");
            foreach (var c in failed.Take(200))
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {c.Area} | {c.Name} | {c.Expected} | {c.Actual} |");
            if (failed.Count > 200) sb.AppendLine(CultureInfo.InvariantCulture, $"\n{failed.Count - 200} more in run-summary.json.");
        }
        if (Notes.Count > 0)
        {
            sb.AppendLine().AppendLine("## Notes").AppendLine();
            foreach (var n in Notes) sb.AppendLine(CultureInfo.InvariantCulture, $"- {n}");
        }
        return sb.ToString();
    }
}
