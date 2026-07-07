using System.Text.Json;

namespace CloudHealthOffice.Tools.MccPlatformValidator;

/// <summary>
/// Diagnostic-only instrumentation for expected-pend MCC scenarios (episode
/// investigation into PR #841's finding that expected-pend claims never reach
/// <c>ClaimStatus.Pended</c> through the validator's direct HTTP path).
///
/// <para>
/// Read-only with respect to adjudication: this collector performs additional
/// <c>GET /api/claims/{id}</c> reads after the timed benchmark window closes
/// (same posture as <see cref="MccClaimStatusObserver"/> pend observation) and
/// writes a report artifact. It never calls any write endpoint and never
/// changes a claim's disposition.
/// </para>
///
/// <para>
/// Off by default; only runs when <see cref="ValidatorOptions.PendDiagnosticsPath"/>
/// is set. A diagnostics-on run is not a valid throughput benchmark — see
/// <see cref="PendDiagnosticsReport.Note"/>.
/// </para>
/// </summary>
internal static class PendDiagnostics
{
    /// <summary>
    /// Business denial code emitted by <c>AdjudicationController.Adjudicate</c>
    /// (src/services/benefit-plan-service/Controllers/AdjudicationController.cs)
    /// when the NCCI/MUE engine fails an edit. The Argo workflow's
    /// <c>update-claim-step</c> is the only code path that redirects this code to
    /// a pend instead of a denial — see PR description for the full trace.
    /// </summary>
    public const string NcciMueDenialCode = "NCCI_MUE_EDIT_FAILURE";

    private const string UnlabeledNcciSampleScenario = "(unlabeled NCCI/MUE sample)";

    /// <summary>
    /// Every expected-pend claim, plus a bounded sample of claims that failed
    /// NCCI/MUE edits (denied today) regardless of what the answer key expects —
    /// the Argo design treats NCCI/MUE as pendable, so their current denial
    /// codes are evidence for the edit-model severity design (Deliverable 3.3).
    /// </summary>
    public static IReadOnlyList<ClaimValidationResult> SelectCandidates(
        IReadOnlyList<ClaimValidationResult> results,
        int ncciSampleSize)
    {
        var expectedPend = results
            .Where(r => r.ExpectedOutcome == ClaimValidationOutcome.Pended.ToString())
            .ToList();

        var ncciSample = results
            .Where(r => r.ExpectedOutcome != ClaimValidationOutcome.Pended.ToString()
                && string.Equals(r.BusinessDenialCode, NcciMueDenialCode, StringComparison.Ordinal))
            .OrderBy(r => r.GeneratedClaimId, StringComparer.Ordinal)
            .Take(Math.Max(0, ncciSampleSize))
            .ToList();

        return expectedPend.Concat(ncciSample).ToList();
    }

    public static async Task<PendDiagnosticsReport> CollectAsync(
        HttpClient http,
        ValidatorOptions options,
        IReadOnlyList<ClaimValidationResult> results,
        CancellationToken ct = default)
    {
        var candidates = SelectCandidates(results, options.PendDiagnosticsNcciSampleSize);
        var rows = new List<PendDiagnosticRow>(candidates.Count);

        foreach (var candidate in candidates)
        {
            rows.Add(await BuildRowAsync(http, options, candidate, ct).ConfigureAwait(false));
        }

        var orderedRows = rows
            .OrderBy(r => r.GeneratedClaimId, StringComparer.Ordinal)
            .ToList();

        return new PendDiagnosticsReport(
            DateTimeOffset.UtcNow,
            options.TenantId,
            options.Claims,
            orderedRows.Count,
            "Diagnostics-on runs are NOT valid throughput benchmarks. This report performs one " +
            "additional claims-service read per diagnosed claim after the timed benchmark window " +
            "closes (same posture as --pend-observation); it does not affect P95/P99/throughput.",
            orderedRows,
            BuildScenarioSummaries(orderedRows));
    }

    private static async Task<PendDiagnosticRow> BuildRowAsync(
        HttpClient http,
        ValidatorOptions options,
        ClaimValidationResult result,
        CancellationToken ct)
    {
        ClaimStateSnapshot? state = null;
        string? fetchError = null;

        if (!string.IsNullOrWhiteSpace(result.SubmittedClaimId))
        {
            try
            {
                using var response = await http
                    .GetAsync($"{options.ClaimsUrl}/api/claims/{result.SubmittedClaimId}", ct)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var document = JsonDocument.Parse(body);
                    state = ClaimStateSnapshot.FromClaimJson(document.RootElement);
                }
                else
                {
                    fetchError = $"claims-service returned {(int)response.StatusCode}";
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                fetchError = $"claim state fetch failed: {ex.Message}";
            }
        }
        else
        {
            fetchError = "no submitted claim id (platform failure before submission)";
        }

        return new PendDiagnosticRow(
            result.GeneratedClaimId,
            result.SubmittedClaimId,
            result.ClaimType,
            string.IsNullOrWhiteSpace(result.ValidationScenario) ? UnlabeledNcciSampleScenario : result.ValidationScenario,
            result.ExpectedOutcome,
            result.ExpectedBusinessDenialCode,
            result.ValidationStatus,
            result.Outcome.ToString(),
            result.AdjudicationSuccess,
            result.BusinessDenialCode,
            result.Error,
            result.SyncAdjudicationSnapshot,
            state?.Status,
            state?.PendCode,
            state?.PendReason,
            state?.DenialReasonCode,
            state?.DenialReason,
            state?.Lines ?? Array.Empty<PendDiagnosticLineOutcome>(),
            fetchError);
    }

    public static IReadOnlyList<PendDiagnosticScenarioSummary> BuildScenarioSummaries(
        IReadOnlyList<PendDiagnosticRow> rows)
    {
        return rows
            .GroupBy(r => r.Scenario, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var deniedBreakdown = g
                    .Where(r => r.Outcome == ClaimValidationOutcome.BusinessDenial.ToString())
                    .GroupBy(r => r.SynchronousBusinessDenialCode ?? r.PersistedDenialReasonCode ?? "UNKNOWN", StringComparer.Ordinal)
                    .OrderByDescending(dg => dg.Count())
                    .ThenBy(dg => dg.Key, StringComparer.Ordinal)
                    .Select(dg => new PendDiagnosticDenialCount(dg.Key, dg.Count()))
                    .ToList();

                return new PendDiagnosticScenarioSummary(
                    g.Key,
                    g.Count(),
                    g.Count(r => r.ExpectedOutcome == ClaimValidationOutcome.Pended.ToString()),
                    g.Count(r => r.Outcome == ClaimValidationOutcome.Paid.ToString()),
                    deniedBreakdown,
                    g.Count(r => r.Outcome == ClaimValidationOutcome.Pended.ToString()),
                    g.Count(r => r.Outcome == ClaimValidationOutcome.ObservationTimeout.ToString()));
            })
            .ToList();
    }

    public static async Task WriteReportAsync(string path, PendDiagnosticsReport report, JsonSerializerOptions json)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, json));
        Console.WriteLine($"  Pend diagnostics:   {path} ({report.DiagnosedClaimCount:N0} claims)");
    }

    public static void PrintAggregateTable(PendDiagnosticsReport report)
    {
        Console.WriteLine();
        Console.WriteLine("Pend diagnostics (internal engineering finding — NOT a throughput benchmark)");
        Console.WriteLine($"  {report.Note}");
        Console.WriteLine();
        Console.WriteLine($"  {"Scenario",-34} {"Total",6} {"ExpPend",7} {"Paid",6} {"Pended",6} {"Timeout",7}  Denied (by code)");
        foreach (var scenario in report.ScenarioSummaries)
        {
            var deniedText = scenario.DeniedBreakdown.Count == 0
                ? "-"
                : string.Join(", ", scenario.DeniedBreakdown.Select(d => $"{d.Code}={d.Count}"));
            Console.WriteLine(
                $"  {scenario.Scenario,-34} {scenario.Total,6:N0} {scenario.ExpectedPendCount,7:N0} {scenario.ObservedPaid,6:N0} {scenario.ObservedPended,6:N0} {scenario.ObservedTimeouts,7:N0}  {deniedText}");
        }
        Console.WriteLine();
    }
}

/// <summary>Parsed subset of the claims-service <c>GET /api/claims/{id}</c> body relevant to pend diagnostics.</summary>
internal sealed record ClaimStateSnapshot(
    string Status,
    string? PendCode,
    string? PendReason,
    string? DenialReasonCode,
    string? DenialReason,
    IReadOnlyList<PendDiagnosticLineOutcome> Lines)
{
    public static ClaimStateSnapshot FromClaimJson(JsonElement root)
    {
        var status = ReadStatus(root);

        string? pendCode = null;
        string? pendReason = null;
        if (root.TryGetProperty("pendDetails", out var pendDetails) && pendDetails.ValueKind == JsonValueKind.Object)
        {
            pendCode = ReadString(pendDetails, "pendCode");
            pendReason = ReadString(pendDetails, "pendReason");
        }

        string? denialCode = null;
        string? denialReason = null;
        if (root.TryGetProperty("adjudicationResult", out var adjudicationResult) && adjudicationResult.ValueKind == JsonValueKind.Object)
        {
            denialCode = ReadString(adjudicationResult, "denialReasonCode");
            denialReason = ReadString(adjudicationResult, "denialReason");
        }

        var lines = new List<PendDiagnosticLineOutcome>();
        if (root.TryGetProperty("claimLines", out var claimLines) && claimLines.ValueKind == JsonValueKind.Array)
        {
            foreach (var line in claimLines.EnumerateArray())
            {
                var lineNumber = line.TryGetProperty("lineNumber", out var lineNumberElement)
                    && lineNumberElement.TryGetInt32(out var parsedLineNumber)
                        ? parsedLineNumber
                        : 0;
                var procedureCode = ReadString(line, "procedureCode");

                decimal? allowed = null;
                decimal? paid = null;
                string? lineReasonCodes = null;
                if (line.TryGetProperty("adjudicationResult", out var lineResult) && lineResult.ValueKind == JsonValueKind.Object)
                {
                    allowed = ReadDecimal(lineResult, "allowedAmount");
                    paid = ReadDecimal(lineResult, "paidAmount");
                    if (lineResult.TryGetProperty("adjustmentReasons", out var reasons) && reasons.ValueKind == JsonValueKind.Array)
                    {
                        var codes = reasons.EnumerateArray()
                            .Select(r => ReadString(r, "reasonCode"))
                            .Where(c => !string.IsNullOrWhiteSpace(c))
                            .ToList();
                        lineReasonCodes = codes.Count == 0 ? null : string.Join("|", codes);
                    }
                }

                lines.Add(new PendDiagnosticLineOutcome(lineNumber, procedureCode, allowed, paid, lineReasonCodes));
            }
        }

        return new ClaimStateSnapshot(status, pendCode, pendReason, denialCode, denialReason, lines);
    }

    private static string ReadStatus(JsonElement root)
    {
        if (!root.TryGetProperty("status", out var value))
        {
            return "Unknown";
        }

        var raw = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.TryGetInt32(out var number) ? number.ToString() : value.GetRawText(),
            _ => null
        };

        return raw?.Trim() switch
        {
            "1" or "Submitted" => "Submitted",
            "2" or "Received" => "Received",
            "3" or "InAdjudication" => "InAdjudication",
            "4" or "Pended" => "Pended",
            "5" or "Approved" => "Approved",
            "6" or "Denied" => "Denied",
            "7" or "Paid" => "Paid",
            "8" or "Voided" => "Voided",
            "9" or "PartiallyPaid" => "PartiallyPaid",
            null => "Unknown",
            var other => other
        };
    }

    private static string? ReadString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static decimal? ReadDecimal(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.TryGetDecimal(out var number)
            ? number
            : null;
}

internal sealed record PendDiagnosticLineOutcome(
    int LineNumber,
    string? ProcedureCode,
    decimal? AllowedAmount,
    decimal? PaidAmount,
    string? DenialReasonCodes);

/// <summary>One row per diagnosed claim (Deliverable 1 + 2.1).</summary>
internal sealed record PendDiagnosticRow(
    string GeneratedClaimId,
    string? SubmittedClaimId,
    string ClaimType,
    string Scenario,
    string? ExpectedOutcome,
    string? ExpectedBusinessDenialCode,
    string ValidationStatus,
    string Outcome,
    bool SynchronousAdjudicationSuccess,
    string? SynchronousBusinessDenialCode,
    string? Error,
    JsonElement? SynchronousAdjudicationResponse,
    string? PersistedClaimStatus,
    string? PendCode,
    string? PendReason,
    string? PersistedDenialReasonCode,
    string? PersistedDenialReason,
    IReadOnlyList<PendDiagnosticLineOutcome> LineOutcomes,
    string? ClaimStateFetchError);

internal sealed record PendDiagnosticDenialCount(string Code, int Count);

/// <summary>Per-scenario aggregate row (Deliverable 2.2) — pasteable into an ADR or episode packet.</summary>
internal sealed record PendDiagnosticScenarioSummary(
    string Scenario,
    int Total,
    int ExpectedPendCount,
    int ObservedPaid,
    IReadOnlyList<PendDiagnosticDenialCount> DeniedBreakdown,
    int ObservedPended,
    int ObservedTimeouts);

internal sealed record PendDiagnosticsReport(
    DateTimeOffset GeneratedAtUtc,
    string TenantId,
    int RequestedClaims,
    int DiagnosedClaimCount,
    string Note,
    IReadOnlyList<PendDiagnosticRow> Rows,
    IReadOnlyList<PendDiagnosticScenarioSummary> ScenarioSummaries);
