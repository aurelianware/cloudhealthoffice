using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudHealthOffice.Tools.PaTimelinessChallenge;

/// <summary>
/// Writes a corpus as the files a run consumes:
/// <list type="bullet">
/// <item><c>history/authorizations.csv</c>: the history cohort in CHO's authorization
/// import format (POST api/v1/authorizations/import/csv), one row per service line.</item>
/// <item><c>history/reporting-facts.jsonl</c>: sourced extension and appeal facts to
/// record after the import (POST api/authorizations/cms-0057/{id}/facts).</item>
/// <item><c>live/requests.jsonl</c>: the live cohort, one case per line, for the validator
/// to submit through PAS and decide as the reviewer.</item>
/// <item><c>answer-key.json</c>: every case's expectation and the expected metrics.</item>
/// </list>
/// </summary>
public static class PaCorpusWriter
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The documented import header for each column, in file order.</summary>
    public static readonly IReadOnlyList<string> CsvHeader =
    [
        "authorization_number", "member_id", "status", "received_date", "decision_date",
        "requesting_provider_npi", "requesting_provider_name", "servicing_provider_npi", "servicing_provider_name",
        "request_type", "service_type_code", "level_of_service", "line_of_business",
        "requested_from_date", "requested_to_date", "diagnosis_codes", "denial_reason_code", "denial_reason",
        "patient_first_name", "patient_last_name", "patient_birth_date",
        "line_number", "procedure_code", "code_system", "procedure_description",
        "requested_units", "approved_units", "line_status", "line_denial_reason_code", "line_denial_reason",
    ];

    public static void WriteAll(string directory, PaCorpusProfile profile, IReadOnlyList<PaCase> cases)
    {
        Directory.CreateDirectory(Path.Combine(directory, "history"));
        Directory.CreateDirectory(Path.Combine(directory, "live"));
        var history = cases.Where(c => c.Cohort == PaCohort.History).ToList();
        File.WriteAllText(Path.Combine(directory, "history", "authorizations.csv"), HistoryCsv(history), Utf8);
        File.WriteAllText(Path.Combine(directory, "history", "reporting-facts.jsonl"), Lines(ReportingFacts(history)), Utf8);
        File.WriteAllText(Path.Combine(directory, "live", "requests.jsonl"),
            Lines(cases.Where(c => c.Cohort == PaCohort.Live)), Utf8);
        File.WriteAllText(Path.Combine(directory, "answer-key.json"),
            JsonSerializer.Serialize(AnswerKey.From(profile, cases), new JsonSerializerOptions(Json) { WriteIndented = true }), Utf8);
    }

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static string HistoryCsv(IEnumerable<PaCase> cases)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(',', CsvHeader)).Append('\n');
        foreach (var c in cases)
        {
            if (c.Cohort != PaCohort.History)
                throw new ArgumentException($"{c.CaseId} is a live case; only history cases are imported.");
            var decidedAt = c.Decision is { } d ? Instant(c.ReceivedAt + d.After) : "";
            var from = DateOnly.FromDateTime(c.ReceivedAt.UtcDateTime.AddDays(7));
            foreach (var line in c.Lines)
            {
                string[] row =
                [
                    c.AuthorizationNumber, c.Member.MemberId, Status(c.Expected.InitialOutcome), Instant(c.ReceivedAt), decidedAt,
                    c.RequestingProvider.Npi, c.RequestingProvider.Name, c.ServicingProvider.Npi, c.ServicingProvider.Name,
                    "prior", c.ServiceTypeCode, c.LevelOfService, c.LineOfBusiness.ToString(),
                    Date(from), Date(from.AddDays(30)), string.Join(';', c.DiagnosisCodes),
                    c.Decision?.DenialReasonCode ?? "", c.Decision?.DenialReason ?? "",
                    c.Member.FirstName, c.Member.LastName, Date(c.Member.BirthDate),
                    line.LineNumber.ToString(CultureInfo.InvariantCulture), line.ProcedureCode, line.CodeSystem, line.Description,
                    line.RequestedUnits.ToString(CultureInfo.InvariantCulture),
                    line.ApprovedUnits?.ToString(CultureInfo.InvariantCulture) ?? "",
                    line.Decision == PaOutcome.Open ? "" : LineStatus(line.Decision),
                    line.DenialReasonCode ?? "", line.DenialReason ?? "",
                ];
                sb.Append(string.Join(',', row.Select(Field))).Append('\n');
            }
        }
        return sb.ToString();
    }

    /// <summary>A sourced reporting fact for CHO's PA reporting ledger, keyed by authorization number.</summary>
    public sealed record ReportingFact(
        string AuthorizationNumber,
        string EventId,
        string Kind,
        DateTimeOffset EffectiveAt,
        string? Outcome,
        DateTimeOffset? OriginalDueAt,
        DateTimeOffset? ExtendedDueAt,
        string SourceSystem,
        string EvidenceReference);

    public static IEnumerable<ReportingFact> ReportingFacts(IEnumerable<PaCase> history)
    {
        foreach (var c in history)
        {
            if (c.Extension is { } e && c.Expected.Extended)
            {
                var original = c.ReceivedAt.AddHours(PaTimeframes.BaseDeadlineHours(c.LevelOfService));
                yield return new ReportingFact(c.AuthorizationNumber, $"{c.CaseId}:extension", "ReviewExtension",
                    c.ReceivedAt + e.GrantedAfter, null, original, original + e.ExtendedBy,
                    PaCorpusGenerator.SourceSystem, $"{c.CaseId}/extension");
            }
            if (c.Appeal is { } a)
                yield return new ReportingFact(c.AuthorizationNumber, $"{c.CaseId}:appeal", "AppealDecision",
                    c.ReceivedAt + a.DecidedAfter, a.Outcome.ToString(), null, null,
                    PaCorpusGenerator.SourceSystem, $"{c.CaseId}/appeal");
        }
    }

    /// <summary>The answer key: what a correct implementation reports for this corpus.</summary>
    public sealed record AnswerKey
    {
        public required string GeneratorVersion { get; init; }
        public required int Seed { get; init; }
        public required int Count { get; init; }
        public required DateTimeOffset AsOf { get; init; }
        public required DateTimeOffset PeriodStart { get; init; }
        public required DateTimeOffset PeriodEnd { get; init; }
        public required string DataClass { get; init; }
        public required IReadOnlyDictionary<string, int> ScenarioCounts { get; init; }
        /// <summary>History cohort: all lines of business, then each one.</summary>
        public required IReadOnlyList<PaExpectedMetrics> ExpectedHistoryMetrics { get; init; }
        /// <summary>History cohort: the annual public-reporting draft per line of business and receipt year (UTC).</summary>
        public required IReadOnlyList<PaExpectedAnnual> ExpectedAnnualDrafts { get; init; }
        public required IReadOnlyList<PaCaseExpectation> Cases { get; init; }

        public static AnswerKey From(PaCorpusProfile profile, IReadOnlyList<PaCase> cases)
        {
            var history = cases.Where(c => c.Cohort == PaCohort.History).ToList();
            var metrics = new List<PaExpectedMetrics> { PaExpectedMetrics.Compute("all", history) };
            metrics.AddRange(Enum.GetValues<PaLineOfBusiness>()
                .Select(l => PaExpectedMetrics.Compute(l.ToString(), history.Where(c => c.LineOfBusiness == l))));
            return new AnswerKey
            {
                GeneratorVersion = PaCorpusGenerator.Version,
                Seed = profile.Seed,
                Count = cases.Count,
                AsOf = profile.AsOf.ToUniversalTime(),
                PeriodStart = profile.ResolvedPeriodStart,
                PeriodEnd = profile.ResolvedPeriodEnd,
                DataClass = "synthetic",
                ScenarioCounts = cases.GroupBy(c => $"{c.Cohort}/{c.Scenario}").OrderBy(g => g.Key, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Count()),
                ExpectedHistoryMetrics = metrics,
                ExpectedAnnualDrafts = history.Select(c => c.ReceivedAt.UtcDateTime.Year).Distinct().Order()
                    .SelectMany(year => Enum.GetValues<PaLineOfBusiness>().Select(l => PaExpectedAnnual.Compute(history, l, year)))
                    .ToList(),
                Cases = cases.Select(c => new PaCaseExpectation(c.CaseId, c.AuthorizationNumber, c.Cohort, c.Scenario,
                    c.LineOfBusiness, c.Expected)).ToList(),
            };
        }
    }

    public sealed record PaCaseExpectation(string CaseId, string AuthorizationNumber, PaCohort Cohort,
        PaScenario Scenario, PaLineOfBusiness LineOfBusiness, PaExpectation Expected);

    private static string Lines<T>(IEnumerable<T> items)
    {
        var sb = new StringBuilder();
        foreach (var item in items) sb.Append(JsonSerializer.Serialize(item, Json)).Append('\n');
        return sb.ToString();
    }

    private static string Status(PaOutcome outcome) => outcome switch
    {
        PaOutcome.Approved => "approved",
        PaOutcome.Modified => "modified",
        PaOutcome.Denied => "denied",
        _ => "pended",
    };

    private static string LineStatus(PaOutcome outcome) => outcome switch
    {
        PaOutcome.Approved => "A1",
        PaOutcome.Modified => "A2",
        PaOutcome.Denied => "A3",
        _ => "",
    };

    private static string Instant(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Field(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
}
