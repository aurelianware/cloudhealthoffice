using System.Globalization;
using System.Text.Json.Nodes;

namespace CloudHealthOffice.Tools.PaTimelinessChallenge;

/// <summary>One scored value: what the answer key says, what CHO returned.</summary>
public sealed record PaCheck(string Area, string Name, string? Expected, string? Actual, bool Passed);

/// <summary>
/// Compares CHO's responses with the answer key. Pure functions over JSON, so
/// they are tested without a running stack. Rounded values (percentages and
/// hours) may differ by one rounding step at an exact midpoint; counts must match.
/// </summary>
public static class PaScorer
{
    /// <summary>
    /// An enum value as authorization-service writes it: by name (its MVC JSON
    /// options use a string enum converter). A number is read too, by CHO's
    /// declared values, in case a surface writes integers.
    /// </summary>
    public static string? EnumName(JsonNode? node, IReadOnlyList<string>? byNumber = null)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<string>(out var s)) return s;
        return byNumber is not null && v.TryGetValue<int>(out var i) && i >= 0 && i < byNumber.Count ? byNumber[i] : null;
    }

    /// <summary>CHO's <c>AuthorizationStatus</c> names, indexed by value.</summary>
    public static readonly IReadOnlyList<string> StatusNames =
        ["", "Submitted", "InReview", "Pended", "Approved", "Modified", "Denied", "Expired", "Cancelled"];

    /// <summary>CHO's <c>LineOfBusiness</c> names, indexed by value.</summary>
    public static readonly IReadOnlyList<string> LineOfBusinessNames =
        ["Unknown", "Commercial", "Medicare", "Medicaid", "Exchange", "TRICARE", "VA"];

    public static PaOutcome Outcome(string? status) => status switch
    {
        "Approved" => PaOutcome.Approved,
        "Modified" => PaOutcome.Modified,
        "Denied" => PaOutcome.Denied,
        _ => PaOutcome.Open,
    };

    public static PaOutcome Outcome(JsonNode? authorization) => Outcome(EnumName(authorization?["status"], StatusNames));

    /// <summary>The operational metrics (<c>GET api/authorizations/cms-0057/metrics</c>).</summary>
    public static IEnumerable<PaCheck> CompareMetrics(string area, PaExpectedMetrics expected, JsonNode? actual)
    {
        if (actual is null)
        {
            yield return new PaCheck(area, "response", "metrics", null, false);
            yield break;
        }
        yield return Count(area, "totalRequests", expected.TotalRequests, actual["totalRequests"]);
        yield return Count(area, "openRequests", expected.OpenRequests, actual["openRequests"]);
        yield return Count(area, "decidedRequests", expected.DecidedRequests, actual["decidedRequests"]);
        yield return Rounded(area, "extendedThenApprovedPercent", expected.ExtendedThenApprovedPercent, actual["extendedThenApprovedPercent"], 0.1);
        foreach (var (name, want) in new[] { ("standard", expected.Standard), ("expedited", expected.Expedited) })
        {
            var got = actual[name];
            yield return Count(area, $"{name}.requests", want.Requests, got?["requests"]);
            yield return Count(area, $"{name}.decided", want.Decided, got?["decided"]);
            yield return Count(area, $"{name}.approved", want.Approved, got?["approved"]);
            yield return Count(area, $"{name}.denied", want.Denied, got?["denied"]);
            yield return Rounded(area, $"{name}.approvedPercent", want.ApprovedPercent, got?["approvedPercent"], 0.1);
            yield return Rounded(area, $"{name}.deniedPercent", want.DeniedPercent, got?["deniedPercent"], 0.1);
            yield return Rounded(area, $"{name}.averageDecisionHours", want.AverageDecisionHours, got?["averageDecisionHours"], 0.1);
            yield return Rounded(area, $"{name}.medianDecisionHours", want.MedianDecisionHours, got?["medianDecisionHours"], 0.1);
            yield return Count(area, $"{name}.decidedWithinTimeframe", want.DecidedWithinTimeframe, got?["decidedWithinTimeframe"]);
            yield return Rounded(area, $"{name}.decidedWithinTimeframePercent", want.DecidedWithinTimeframePercent, got?["decidedWithinTimeframePercent"], 0.1);
            yield return Count(area, $"{name}.extended", want.Extended, got?["extended"]);
            yield return Count(area, $"{name}.extendedThenApproved", want.ExtendedThenApproved, got?["extendedThenApproved"]);
            yield return Count(area, $"{name}.approvedAfterAppeal", want.ApprovedAfterAppeal, got?["approvedAfterAppeal"]);
        }
    }

    /// <summary>The annual draft (<c>GET api/authorizations/cms-0057/annual-draft</c>).</summary>
    public static IEnumerable<PaCheck> CompareAnnual(string area, PaExpectedAnnual expected, JsonNode? actual)
    {
        if (actual is null)
        {
            yield return new PaCheck(area, "response", "annual draft", null, false);
            yield break;
        }
        yield return Count(area, "legacyRecordsWithoutEvidence", 0, actual["legacyRecordsWithoutEvidence"]);
        yield return Count(area, "invalidReceiptRecords", 0, actual["invalidReceiptRecords"]);
        yield return Rounded(area, "extendedThenApprovedPercent", expected.ExtendedThenApprovedPercent, actual["extendedThenApprovedPercent"], 0.0001);
        foreach (var (name, want) in new[] { ("standard", expected.Standard), ("expedited", expected.Expedited) })
        {
            var got = actual[name];
            yield return Count(area, $"{name}.requests", want.Requests, got?["requests"]);
            yield return Count(area, $"{name}.approved", want.Approved, got?["approved"]);
            yield return Count(area, $"{name}.denied", want.Denied, got?["denied"]);
            yield return Count(area, $"{name}.undecided", want.Undecided, got?["undecided"]);
            yield return Count(area, $"{name}.approvedAfterAppeal", want.ApprovedAfterAppeal, got?["approvedAfterAppeal"]);
            yield return Count(area, $"{name}.extendedThenApproved", want.ExtendedThenApproved, got?["extendedThenApproved"]);
            yield return Count(area, $"{name}.initialDecisionTimingUnavailable", 0, got?["initialDecisionTimingUnavailable"]);
            yield return Count(area, $"{name}.conflictingFinalOutcomes", 0, got?["conflictingFinalOutcomes"]);
            yield return Rounded(area, $"{name}.approvedPercent", want.ApprovedPercent, got?["approvedPercent"], 0.0001);
            yield return Rounded(area, $"{name}.deniedPercent", want.DeniedPercent, got?["deniedPercent"], 0.0001);
            yield return Rounded(area, $"{name}.averageInitialDecisionHours", want.AverageInitialDecisionHours, got?["averageInitialDecisionHours"], 0.0001);
            yield return Rounded(area, $"{name}.medianInitialDecisionHours", want.MedianInitialDecisionHours, got?["medianInitialDecisionHours"], 0.0001);
        }
    }

    /// <summary>
    /// Per-request timeliness from the UM decision-timeliness report rows
    /// (<c>GET api/authorizations/reports/um-decision-timeliness</c>): every in-scope
    /// history case must appear once, with the expected on-time flag and decision.
    /// An undecided request is late once its deadline has passed at <paramref name="asOf"/>
    /// and blank before that. Returns the failures (at most <paramref name="limit"/>) and the counts.
    /// </summary>
    public static (int Scored, int Mismatched, int Missing, int Unexpected, List<string> Examples) ScoreTimelinessRows(
        IReadOnlyList<PaCase> history, IEnumerable<JsonNode> rows, DateTimeOffset asOf, int limit = 25)
    {
        var expected = history.Where(c => c.Expected.InScope).ToDictionary(c => c.AuthorizationNumber, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var examples = new List<string>();
        int scored = 0, mismatched = 0, unexpected = 0;
        foreach (var row in rows)
        {
            var number = row["authorizationNumber"]?.GetValue<string>() ?? "";
            if (!expected.TryGetValue(number, out var c))
            {
                unexpected++;
                continue;
            }
            if (!seen.Add(number)) { unexpected++; continue; }
            scored++;
            var within = row["withinTimeframe"] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : (bool?)null;
            var problems = new List<string>();
            var want = c.Expected.InitialOutcome == PaOutcome.Open
                ? (asOf > c.ReceivedAt.AddHours(c.Expected.DeadlineHours) ? false : null)
                : c.Expected.DecidedWithinTimeframe;
            if (within != want)
                problems.Add($"withinTimeframe {Text(want)} expected, {Text(within)} reported");
            var extended = row["extended"]?.GetValue<bool>() ?? false;
            if (extended != c.Expected.Extended)
                problems.Add($"extended {c.Expected.Extended} expected, {extended} reported");
            if (problems.Count > 0)
            {
                mismatched++;
                if (examples.Count < limit) examples.Add($"{c.CaseId} {number} ({c.Scenario}): {string.Join("; ", problems)}");
            }
        }
        var missing = expected.Keys.Count(k => !seen.Contains(k));
        if (missing > 0 && examples.Count < limit)
            examples.Add($"{missing} in-scope history requests were not in the report, e.g. {string.Join(", ", expected.Keys.Where(k => !seen.Contains(k)).Take(3))}");
        return (scored, mismatched, missing, unexpected, examples);
    }

    /// <summary>What a live case looked like after the validator acted on it.</summary>
    public sealed record LiveObservation
    {
        public required string CaseId { get; init; }
        public string? AuthorizationNumber { get; init; }
        public string? AuthorizationId { get; init; }
        /// <summary>The outcome PAS returned on submit, before the reviewer acted.</summary>
        public string? SubmitOutcome { get; init; }
        /// <summary>CHO decided the request itself on submit (a rule), so the scripted reviewer decision did not run.</summary>
        public bool AutoDecided { get; init; }
        public int? ExtensionStatusCode { get; init; }
        public int? DecisionStatusCode { get; init; }
        public JsonNode? Authorization { get; init; }
        public string? Error { get; init; }
    }

    public static IEnumerable<PaCheck> ScoreLive(PaCase c, LiveObservation o)
    {
        var area = $"live/{c.CaseId}";
        if (o.Error is not null || o.Authorization is null)
        {
            yield return new PaCheck(area, "submitted", "stored and readable", o.Error ?? "no authorization", false);
            yield break;
        }
        var a = o.Authorization;
        var expectedLob = (c.LineOfBusiness == PaLineOfBusiness.Exchange ? PaLineOfBusiness.Commercial : c.LineOfBusiness).ToString();
        var lob = EnumName(a["lineOfBusiness"], LineOfBusinessNames);
        yield return new PaCheck(area, "lineOfBusiness", expectedLob, lob, lob == expectedLob);
        yield return new PaCheck(area, "levelOfService", c.LevelOfService, a["levelOfService"]?.GetValue<string>(),
            string.Equals(c.LevelOfService, a["levelOfService"]?.GetValue<string>(), StringComparison.Ordinal));

        if (c.Extension is not null)
        {
            var want = c.Expected.ExtensionMustBeRefused ? 409 : 200;
            yield return new PaCheck(area, "extension", want.ToString(CultureInfo.InvariantCulture),
                o.ExtensionStatusCode?.ToString(CultureInfo.InvariantCulture), o.ExtensionStatusCode == want);
            var extended = a["decisionExtension"] is JsonObject;
            yield return new PaCheck(area, "extended", c.Expected.Extended.ToString(), extended.ToString(), extended == c.Expected.Extended);
        }

        if (o.AutoDecided)
        {
            // The scripted decision could not run: reported, not scored as the reviewer's outcome.
            yield return new PaCheck(area, "reviewable", "pended for review", $"auto-decided on submit ({o.SubmitOutcome})", false);
            yield break;
        }
        if (c.Decision is not null)
            yield return new PaCheck(area, "decisionRecorded", "200", o.DecisionStatusCode?.ToString(CultureInfo.InvariantCulture),
                o.DecisionStatusCode == 200);
        var outcome = Outcome(a);
        yield return new PaCheck(area, "outcome", c.Expected.InitialOutcome.ToString(), outcome.ToString(), outcome == c.Expected.InitialOutcome);

        if (c.Expected.AdverseReasons.Count > 0)
        {
            var lines = (a["requestedServices"] as JsonArray ?? [])
                .Select((s, i) => (Line: s?["lineNumber"]?.GetValue<int?>() ?? i + 1, Status: s?["serviceStatus"]?.GetValue<string>(),
                    Code: s?["denialReasonCode"]?.GetValue<string>()))
                .Where(l => l.Status == "A3")
                .Select(l => $"{l.Line}:{l.Code}")
                .ToList();
            var want = c.Expected.AdverseReasons.Select(r => $"{r.LineNumber}:{r.Code}").ToList();
            yield return new PaCheck(area, "lineDenialReasons", string.Join(",", want), string.Join(",", lines), want.SequenceEqual(lines));
        }
    }

    /// <summary>
    /// A live case rewritten with what CHO actually recorded, so the live window's
    /// metrics can be checked against CHO's own records: the arithmetic and the
    /// scope (drugs out), whatever a rule decided on submit.
    /// </summary>
    public static PaCase? AsObserved(PaCase c, LiveObservation o)
    {
        if (o.Authorization is not JsonNode a) return null;
        var outcome = Outcome(a);
        var receipt = Instant(a["decisionReceivedAt"]) ?? Instant(a["submittedDate"]);
        var reviewed = Instant(a["reviewedDate"]);
        double? hours = outcome != PaOutcome.Open && receipt is { } r && reviewed is { } d && d >= r ? (d - r).TotalHours : null;
        var extended = a["decisionExtension"] is JsonObject;
        var lob = Enum.TryParse<PaLineOfBusiness>(EnumName(a["lineOfBusiness"], LineOfBusinessNames), out var parsed)
            ? parsed : c.LineOfBusiness;
        var deadline = PaTimeframes.BaseDeadlineHours(c.LevelOfService) + (extended ? PaTimeframes.MaxExtension.TotalHours : 0);
        return c with
        {
            LineOfBusiness = lob,
            ReceivedAt = receipt is { } rr ? new DateTimeOffset(rr, TimeSpan.Zero) : c.ReceivedAt,
            Expected = c.Expected with
            {
                InitialOutcome = outcome,
                FinalOutcome = outcome,
                DecisionHours = hours,
                DecidedWithinTimeframe = hours is { } h ? h <= deadline : null,
                Extended = extended,
                DeadlineHours = deadline,
            },
        };
    }

    private static DateTime? Instant(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s)
            && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? d : null;

    private static PaCheck Count(string area, string name, int expected, JsonNode? actual)
    {
        int? got = actual is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
        return new PaCheck(area, name, expected.ToString(CultureInfo.InvariantCulture), got?.ToString(CultureInfo.InvariantCulture), got == expected);
    }

    private static PaCheck Rounded(string area, string name, double? expected, JsonNode? actual, double step)
    {
        double? got = actual is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
        var passed = expected is null ? got is null : got is { } g && Math.Abs(g - expected.Value) <= step + 1e-9;
        return new PaCheck(area, name, expected?.ToString(CultureInfo.InvariantCulture), got?.ToString(CultureInfo.InvariantCulture), passed);
    }

    private static string Text(bool? value) => value?.ToString() ?? "blank";
}
