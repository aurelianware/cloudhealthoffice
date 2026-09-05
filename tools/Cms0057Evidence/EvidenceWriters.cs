using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Cms0057Evidence;

/// <summary>Serializes an <see cref="EvidenceReport"/> to JSON, Markdown, and HTML.</summary>
public static class EvidenceWriters
{
    private const string PassableDisclaimer =
        "PASSABLE means the repository's defined acceptance scenario is supported by the tested " +
        "implementation. It is not a certification by CMS and does not by itself establish production " +
        "readiness for a specific payer deployment.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string ToJson(EvidenceReport report) => JsonSerializer.Serialize(report, JsonOptions);

    /// <summary>Serializes the sanitized public projection (see <see cref="PublicEvidenceProjector"/>).</summary>
    public static string ToPublicJson(PublicEvidence evidence) => JsonSerializer.Serialize(evidence, JsonOptions);

    // ── Markdown ───────────────────────────────────────────────────────────────

    public static string ToMarkdown(EvidenceReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Cloud Health Office CMS-0057-F Acceptance Evidence");
        sb.AppendLine();
        sb.AppendLine("## Evidence identity");
        sb.AppendLine();
        sb.AppendLine($"- Evidence schema: {r.SchemaVersion}");
        sb.AppendLine($"- Generated (UTC): {r.Identity.GeneratedAtUtc}");
        if (!string.IsNullOrEmpty(r.Identity.Repository)) sb.AppendLine($"- Repository: {r.Identity.Repository}");
        if (!string.IsNullOrEmpty(r.Identity.CommitSha)) sb.AppendLine($"- Commit: `{r.Identity.CommitSha}`");
        if (!string.IsNullOrEmpty(r.Identity.Ref)) sb.AppendLine($"- Ref: {r.Identity.Ref}");
        if (!string.IsNullOrEmpty(r.Identity.WorkflowRunId)) sb.AppendLine($"- Workflow run: {r.Identity.WorkflowRunId}");
        sb.AppendLine($"- Environment: {r.Identity.Environment}");
        sb.AppendLine($"- Test data: {r.Identity.TestDataClassification}");
        sb.AppendLine($"- Framework: {r.Identity.Framework}");
        sb.AppendLine($"- FHIR version: {r.Identity.FhirVersion}");
        sb.AppendLine();

        sb.AppendLine("## Test execution summary");
        sb.AppendLine();
        sb.AppendLine($"- Passed: {r.TestSummary.Passed}");
        sb.AppendLine($"- Failed: {r.TestSummary.Failed}");
        sb.AppendLine($"- Skipped: {r.TestSummary.Skipped}");
        sb.AppendLine($"- Total: {r.TestSummary.Total}");
        sb.AppendLine();
        sb.AppendLine($"> {PassableDisclaimer}");
        sb.AppendLine();

        // Matrix columns are generated from the augment backends actually present
        // in the report, so new backends (e.g. facets/healthedge) appear
        // automatically without editing the writer.
        var augmentKeys = AugmentKeys(r);
        sb.AppendLine("## Scenario matrix");
        sb.AppendLine();
        sb.Append("| Scenario | Name | CHO Replace (product) |");
        foreach (var key in augmentKeys) sb.Append($" {key.ToUpperInvariant()} Augment (integration) |");
        sb.AppendLine();
        sb.Append("| --- | --- | --- |");
        foreach (var _ in augmentKeys) sb.Append(" --- |");
        sb.AppendLine();
        foreach (var s in r.Scenarios)
        {
            sb.Append($"| {s.Id} | {s.Name} | {Declared(s, BackendIds.Replace)} |");
            foreach (var key in augmentKeys) sb.Append($" {Declared(s, BackendIds.Augment(key))} |");
            sb.AppendLine();
        }
        sb.AppendLine();

        AppendCapabilitySection(sb, r, "## Product capability — Cloud Health Office Replace",
            b => b == BackendIds.Replace, includeBackendColumn: false);
        AppendCapabilitySection(sb, r, "## Integration capability — external cores (Augment)",
            b => b.StartsWith("augment.", StringComparison.Ordinal), includeBackendColumn: true);

        sb.AppendLine("## Test details");
        sb.AppendLine();
        foreach (var s in r.Scenarios)
        {
            sb.AppendLine($"### {s.Id} — {s.Name}");
            sb.AppendLine();
            foreach (var b in s.Backends)
            {
                sb.AppendLine($"- **{b.Backend}**: declared `{b.DeclaredStatus}`, tests `{b.TestExecutionStatus}`" +
                              (b.Rationale is null ? "" : $" — {b.Rationale}"));
                foreach (var t in b.SupportingTests)
                    sb.AppendLine($"  - `{t}`");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Limitations");
        sb.AppendLine();
        sb.AppendLine("- Evidence reflects the acceptance suite on the tested commit, on synthetic data only.");
        sb.AppendLine("- Declared capability status is separate from test execution status: a passing GAP-assertion test confirms a gap and does not make the scenario PASSABLE.");
        sb.AppendLine("- Integration (Augment) status reflects only backends represented in the manifest; no QNXT/Facets/HealthEdge production integration is claimed.");
        sb.AppendLine();
        return sb.ToString();
    }

    private static void AppendCapabilitySection(
        StringBuilder sb, EvidenceReport r, string heading, Func<string, bool> backendMatch, bool includeBackendColumn)
    {
        sb.AppendLine(heading);
        sb.AppendLine();
        var rows = r.KnownGaps.Where(g => backendMatch(g.Backend)).ToList();
        if (rows.Count == 0)
        {
            sb.AppendLine("_No declared partials or gaps._");
        }
        else if (includeBackendColumn)
        {
            sb.AppendLine("| Scenario | Backend | Status | Rationale |");
            sb.AppendLine("| --- | --- | --- | --- |");
            foreach (var g in rows) sb.AppendLine($"| {g.ScenarioId} | {g.Backend} | {g.Status} | {g.Rationale} |");
        }
        else
        {
            sb.AppendLine("| Scenario | Status | Rationale |");
            sb.AppendLine("| --- | --- | --- |");
            foreach (var g in rows) sb.AppendLine($"| {g.ScenarioId} | {g.Status} | {g.Rationale} |");
        }
        sb.AppendLine();
    }

    private static string Declared(ScenarioEvidence s, string backendId) =>
        s.Backends.FirstOrDefault(b => b.Backend == backendId)?.DeclaredStatus ?? "N/A";

    /// <summary>Ordered set of augment backend keys present anywhere in the report.</summary>
    private static IReadOnlyList<string> AugmentKeys(EvidenceReport r) =>
        r.Scenarios
            .SelectMany(s => s.Backends)
            .Select(b => b.Backend)
            .Where(b => b.StartsWith("augment.", StringComparison.Ordinal))
            .Select(b => b["augment.".Length..])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

    // ── HTML ───────────────────────────────────────────────────────────────────

    public static string ToHtml(EvidenceReport r)
    {
        static string E(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.AppendLine("<title>CMS-0057-F Acceptance Evidence</title>");
        sb.AppendLine("<style>body{font:14px/1.6 system-ui,sans-serif;max-width:960px;margin:2rem auto;padding:0 1rem;color:#111}"
                      + "table{border-collapse:collapse;width:100%;margin:1rem 0}th,td{border:1px solid #ccc;padding:.4rem .6rem;text-align:left}"
                      + "th{background:#f3f4f6}code{background:#f3f4f6;padding:.1rem .3rem;border-radius:3px}"
                      + ".PASSABLE{color:#087f5b;font-weight:600}.PARTIAL{color:#b7791f;font-weight:600}.GAP{color:#c92a2a;font-weight:600}.NA{color:#555}"
                      + "blockquote{border-left:3px solid #ccc;margin:1rem 0;padding:.2rem 1rem;color:#444}</style></head><body>");
        sb.AppendLine("<h1>Cloud Health Office CMS-0057-F Acceptance Evidence</h1>");
        sb.AppendLine("<h2>Evidence identity</h2><ul>");
        sb.AppendLine($"<li>Evidence schema: {r.SchemaVersion}</li>");
        sb.AppendLine($"<li>Generated (UTC): {E(r.Identity.GeneratedAtUtc)}</li>");
        if (!string.IsNullOrEmpty(r.Identity.Repository)) sb.AppendLine($"<li>Repository: {E(r.Identity.Repository)}</li>");
        if (!string.IsNullOrEmpty(r.Identity.CommitSha)) sb.AppendLine($"<li>Commit: <code>{E(r.Identity.CommitSha)}</code></li>");
        if (!string.IsNullOrEmpty(r.Identity.Ref)) sb.AppendLine($"<li>Ref: {E(r.Identity.Ref)}</li>");
        if (!string.IsNullOrEmpty(r.Identity.WorkflowRunId)) sb.AppendLine($"<li>Workflow run: {E(r.Identity.WorkflowRunId)}</li>");
        sb.AppendLine($"<li>Environment: {E(r.Identity.Environment)}</li>");
        sb.AppendLine($"<li>Test data: {E(r.Identity.TestDataClassification)}</li>");
        sb.AppendLine($"<li>FHIR version: {E(r.Identity.FhirVersion)}</li></ul>");
        sb.AppendLine($"<h2>Test execution summary</h2><p>Passed {r.TestSummary.Passed} · Failed {r.TestSummary.Failed} · Skipped {r.TestSummary.Skipped} · Total {r.TestSummary.Total}</p>");
        sb.AppendLine($"<blockquote>{E(PassableDisclaimer)}</blockquote>");
        var augmentKeys = AugmentKeys(r);
        sb.Append("<h2>Scenario matrix</h2><table><tr><th>Scenario</th><th>Name</th><th>CHO Replace (product)</th>");
        foreach (var key in augmentKeys) sb.Append($"<th>{E(key.ToUpperInvariant())} Augment (integration)</th>");
        sb.AppendLine("</tr>");
        foreach (var s in r.Scenarios)
        {
            sb.Append($"<tr><td>{E(s.Id)}</td><td>{E(s.Name)}</td>{Cell(Declared(s, BackendIds.Replace))}");
            foreach (var key in augmentKeys) sb.Append(Cell(Declared(s, BackendIds.Augment(key))));
            sb.AppendLine("</tr>");
        }
        sb.AppendLine("</table>");

        // Local helper: a status cell with a CSS-safe class (N/A -> NA).
        static string Cell(string status)
        {
            var enc = System.Net.WebUtility.HtmlEncode(status);
            var cls = new string(status.Where(char.IsLetterOrDigit).ToArray());
            return $"<td class=\"{cls}\">{enc}</td>";
        }
        sb.AppendLine("<h2>Limitations</h2><ul>"
            + "<li>Evidence reflects the acceptance suite on the tested commit, on synthetic data only.</li>"
            + "<li>Declared capability status is separate from test execution status.</li>"
            + "<li>No QNXT/Facets/HealthEdge production integration is claimed.</li></ul>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }
}
