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

        sb.AppendLine("## Scenario matrix");
        sb.AppendLine();
        sb.AppendLine("| Scenario | Name | CHO Replace (product) | QNXT Augment (integration) |");
        sb.AppendLine("| --- | --- | --- | --- |");
        foreach (var s in r.Scenarios)
        {
            var replace = s.Backends.FirstOrDefault(b => b.Backend == BackendIds.Replace)?.DeclaredStatus ?? "N/A";
            var qnxt = s.Backends.FirstOrDefault(b => b.Backend == BackendIds.Augment("qnxt"))?.DeclaredStatus ?? "N/A";
            sb.AppendLine($"| {s.Id} | {s.Name} | {replace} | {qnxt} |");
        }
        sb.AppendLine();

        AppendGapSection(sb, r, "## Remaining product gaps", BackendIds.Replace);
        AppendIntegrationGapSection(sb, r);

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

    private static void AppendGapSection(StringBuilder sb, EvidenceReport r, string heading, string backendId)
    {
        sb.AppendLine(heading);
        sb.AppendLine();
        sb.AppendLine("## Product capability — Cloud Health Office Replace");
        sb.AppendLine();
        var rows = r.KnownGaps.Where(g => g.Backend == backendId).ToList();
        if (rows.Count == 0)
        {
            sb.AppendLine("_No declared product gaps._");
        }
        else
        {
            sb.AppendLine("| Scenario | Status | Rationale |");
            sb.AppendLine("| --- | --- | --- |");
            foreach (var g in rows) sb.AppendLine($"| {g.ScenarioId} | {g.Status} | {g.Rationale} |");
        }
        sb.AppendLine();
    }

    private static void AppendIntegrationGapSection(StringBuilder sb, EvidenceReport r)
    {
        sb.AppendLine("## Integration capability — external cores (Augment)");
        sb.AppendLine();
        var rows = r.KnownGaps.Where(g => g.Backend.StartsWith("augment.", StringComparison.Ordinal)).ToList();
        if (rows.Count == 0)
        {
            sb.AppendLine("_No declared integration gaps._");
        }
        else
        {
            sb.AppendLine("| Scenario | Backend | Status | Rationale |");
            sb.AppendLine("| --- | --- | --- | --- |");
            foreach (var g in rows) sb.AppendLine($"| {g.ScenarioId} | {g.Backend} | {g.Status} | {g.Rationale} |");
        }
        sb.AppendLine();
    }

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
                      + ".PASSABLE{color:#087f5b;font-weight:600}.PARTIAL{color:#b7791f;font-weight:600}.GAP{color:#c92a2a;font-weight:600}"
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
        sb.AppendLine("<h2>Scenario matrix</h2><table><tr><th>Scenario</th><th>Name</th><th>CHO Replace (product)</th><th>QNXT Augment (integration)</th></tr>");
        foreach (var s in r.Scenarios)
        {
            var replace = s.Backends.FirstOrDefault(b => b.Backend == BackendIds.Replace)?.DeclaredStatus ?? "N/A";
            var qnxt = s.Backends.FirstOrDefault(b => b.Backend == BackendIds.Augment("qnxt"))?.DeclaredStatus ?? "N/A";
            sb.AppendLine($"<tr><td>{E(s.Id)}</td><td>{E(s.Name)}</td><td class=\"{E(replace)}\">{E(replace)}</td><td class=\"{E(qnxt)}\">{E(qnxt)}</td></tr>");
        }
        sb.AppendLine("</table>");
        sb.AppendLine("<h2>Limitations</h2><ul>"
            + "<li>Evidence reflects the acceptance suite on the tested commit, on synthetic data only.</li>"
            + "<li>Declared capability status is separate from test execution status.</li>"
            + "<li>No QNXT/Facets/HealthEdge production integration is claimed.</li></ul>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }
}
