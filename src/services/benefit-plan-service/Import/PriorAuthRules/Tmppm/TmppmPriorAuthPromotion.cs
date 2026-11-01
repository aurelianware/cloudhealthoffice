using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CsvHelper;

namespace BenefitPlanService.Import.PriorAuthRules.Tmppm;

/// <summary>
/// One rule extracted from a state Medicaid provider manual (the TMPPM for
/// Texas), as terminology-service <c>GET api/v1/tmppm/rules?state=</c> returns
/// it. Only the fields the promotion reads.
/// </summary>
public sealed record StateManualPaRule
{
    public string RuleId { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string TmppmRef { get; init; } = string.Empty;
    public string RuleType { get; init; } = string.Empty;
    public List<string> ProcedureCodes { get; init; } = [];
    public bool AuthRequired { get; init; }
    public List<string>? AllowedDiagnoses { get; init; }
    public JsonElement? AgeLimit { get; init; }
    public JsonElement? UnitLimit { get; init; }
    public DateOnly EffectiveDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public string SourceEdition { get; init; } = string.Empty;
}

/// <summary>Reads a state's extracted manual rules.</summary>
public interface IStateManualRuleSource
{
    Task<IReadOnlyList<StateManualPaRule>> GetRulesAsync(string tenantId, string state, CancellationToken ct = default);
}

public sealed class StateManualRuleSourceException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>terminology-service, with this service's own identity (the rules are shared state policy).</summary>
public sealed class HttpStateManualRuleSource(IHttpClientFactory factory, ILogger<HttpStateManualRuleSource> logger)
    : IStateManualRuleSource
{
    public const string ClientName = "terminology-service";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<StateManualPaRule>> GetRulesAsync(string tenantId, string state, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"api/v1/tmppm/rules?state={Uri.EscapeDataString(state)}")
                .UseChoServiceIdentity();
            request.Headers.Add("X-Tenant-ID", tenantId);
            using var response = await factory.CreateClient(ClientName).SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return [];
            if (!response.IsSuccessStatusCode)
                throw new StateManualRuleSourceException(
                    $"terminology-service answered {(int)response.StatusCode} for the {state} manual rules.");
            return await response.Content.ReadFromJsonAsync<List<StateManualPaRule>>(Json, ct) ?? [];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not StateManualRuleSourceException)
        {
            logger.LogWarning(ex, "Reading the {State} manual rules from terminology-service failed", state);
            throw new StateManualRuleSourceException($"Could not read the {state} manual rules ({ex.GetType().Name}).", ex);
        }
    }
}

public sealed record StateManualPromotionOptions
{
    /// <summary>Two-letter state; TX (the TMPPM) is the only manual ingested today.</summary>
    public string StateCode { get; init; } = "TX";
    public PaLineOfBusiness LineOfBusiness { get; init; } = PaLineOfBusiness.Medicaid;
    public string? Program { get; init; }
    public bool KeepExemptions { get; init; } = true;
    public bool DryRun { get; init; } = true;
    public bool IncludeCodes { get; init; }
    /// <summary>Date the rules must be in effect on; default today (UTC).</summary>
    public DateOnly? AsOf { get; init; }
}

public sealed record StateManualPromotionReport
{
    public required PriorAuthRuleImportReport Import { get; init; }
    /// <summary>Manual rules read from terminology-service.</summary>
    public int ManualRulesRead { get; init; }
    /// <summary>Manual rules turned into PA-required rules.</summary>
    public int ManualRulesPromoted { get; init; }
    /// <summary>Manual rules left out, counted by reason.</summary>
    public IReadOnlyDictionary<string, int> ManualRulesLeftOut { get; init; } = new Dictionary<string, int>();
    /// <summary>Editions the promoted rules were extracted from, newest first.</summary>
    public IReadOnlyList<string> Editions { get; init; } = [];
    /// <summary>
    /// Promoted rules whose manual section also states diagnosis, age or unit
    /// conditions. They are loaded as "always needs auth" for their codes; the
    /// conditions are not applied and are listed here for review.
    /// </summary>
    public IReadOnlyList<string> ConditionsNotApplied { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public interface IStateManualPromotionService
{
    Task<StateManualPromotionReport> PromoteAsync(
        StateManualPromotionOptions options, string tenantId, string? actor, CancellationToken ct = default);
}

/// <summary>
/// Promotes a state Medicaid provider manual's extracted prior-auth rules (the
/// TMPPM, ingested monthly by tools/CloudHealthOffice.TmppmIngestionService into
/// terminology-service) into the tenant's PA rule set for one state, line of
/// business and program: the rule set CRD, PAS <c>$submit</c>, the published
/// required-services list and claims adjudication read.
///
/// <para>
/// It goes through the prior-auth list importer under the <c>tmppm</c>
/// namespace, so the plan's own imported list in the same scope stays in place
/// beside it, a re-promotion disables manual rules that dropped out, and the
/// dry run (the default) is the review step: it shows what would be written and
/// what stops applying before anything changes.
/// </para>
/// Only rules the manual marks as needing authorization and that name codes are
/// promoted, as "always" rules; see <see cref="StateManualPromotionReport.ConditionsNotApplied"/>.
/// See docs/integration/STATE-MANUAL-PA-RULES.md.
/// </summary>
public sealed class StateManualPromotionService(
    IStateManualRuleSource source,
    IPriorAuthRuleImportService importer) : IStateManualPromotionService
{
    public const string Namespace = "tmppm";

    /// <summary>A manual section naming more codes than this is likely to have picked up stray numbers.</summary>
    public const int LargeRuleCodeCount = 200;

    public async Task<StateManualPromotionReport> PromoteAsync(
        StateManualPromotionOptions options, string tenantId, string? actor, CancellationToken ct = default)
    {
        var state = options.StateCode.Trim().ToUpperInvariant();
        var asOf = options.AsOf ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var rules = await source.GetRulesAsync(tenantId, state, ct);

        var leftOut = new SortedDictionary<string, int>(StringComparer.Ordinal);
        void LeaveOut(string reason) => leftOut[reason] = leftOut.GetValueOrDefault(reason) + 1;

        var promoted = new List<StateManualPaRule>();
        foreach (var rule in rules)
        {
            if (!string.Equals(rule.State, state, StringComparison.OrdinalIgnoreCase)) { LeaveOut("other state"); continue; }
            if (string.Equals(rule.RuleType, "Noncovered", StringComparison.OrdinalIgnoreCase)) { LeaveOut("not covered (not a PA rule)"); continue; }
            if (!rule.AuthRequired) { LeaveOut("manual does not say authorization is required"); continue; }
            if (rule.ProcedureCodes.Count == 0) { LeaveOut("no procedure codes"); continue; }
            if (rule.EndDate is { } end && end < asOf) { LeaveOut("ended"); continue; }
            if (rule.EffectiveDate > asOf) { LeaveOut("not yet in effect"); continue; }
            promoted.Add(rule);
        }

        var conditions = promoted
            .Where(r => r.AllowedDiagnoses is { Count: > 0 } || IsSet(r.AgeLimit) || IsSet(r.UnitLimit))
            .Select(r => $"{r.RuleId} ({Ref(r)})")
            .ToList();

        var warnings = new List<string>();
        if (rules.Count == 0)
            warnings.Add($"terminology-service has no manual rules for {state}. Run the TMPPM ingestion tool first.");
        var large = promoted.Where(r => r.ProcedureCodes.Count > LargeRuleCodeCount).ToList();
        if (large.Count > 0)
            warnings.Add($"{large.Count} manual section(s) name more than {LargeRuleCodeCount} codes " +
                         $"({string.Join(", ", large.Take(5).Select(r => r.RuleId))}). The extractor takes every code " +
                         "in a section's text; check them before writing.");
        var editions = promoted.Select(r => r.SourceEdition).Where(e => e.Length > 0)
            .Distinct(StringComparer.Ordinal).OrderByDescending(e => e, StringComparer.Ordinal).ToList();
        if (editions.Count > 1)
            warnings.Add($"The rules come from {editions.Count} editions ({string.Join(", ", editions)}): chapters that did " +
                         "not change keep their earlier extraction, and sections removed from the manual are not deleted.");

        await using var csv = ToCsv(promoted);
        var import = await importer.ImportAsync(csv, new PriorAuthRuleImportOptions
        {
            StateCode = state,
            LineOfBusiness = options.LineOfBusiness,
            Program = string.IsNullOrWhiteSpace(options.Program) ? null : options.Program.Trim(),
            SourceName = editions.Count > 0 ? $"{state} provider manual (edition {editions[0]})" : $"{state} provider manual",
            KeepExemptions = options.KeepExemptions,
            DryRun = options.DryRun,
            IncludeCodes = options.IncludeCodes,
            RuleNamespace = Namespace,
        }, tenantId, actor, ct);

        return new StateManualPromotionReport
        {
            Import = import,
            ManualRulesRead = rules.Count,
            ManualRulesPromoted = promoted.Count,
            ManualRulesLeftOut = leftOut,
            Editions = editions,
            ConditionsNotApplied = conditions,
            Warnings = warnings,
        };
    }

    private static string Ref(StateManualPaRule r) =>
        string.Join(' ', new[] { r.TmppmRef, r.Category }.Where(x => !string.IsNullOrWhiteSpace(x)));

    private static bool IsSet(JsonElement? e) =>
        e is { } v && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    /// <summary>The rules as a list the importer reads: one row per manual section.</summary>
    internal static MemoryStream ToCsv(IEnumerable<StateManualPaRule> rules)
    {
        var stream = new MemoryStream();
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
        using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
        {
            csv.WriteField("rule_id");
            csv.WriteField("name");
            csv.WriteField("codes");
            csv.WriteField("condition");
            csv.NextRecord();
            foreach (var r in rules)
            {
                csv.WriteField(r.RuleId);
                var name = Ref(r);
                csv.WriteField(name.Length > 0 ? name : r.RuleId);
                csv.WriteField(string.Join(' ', r.ProcedureCodes.Select(c => c.Trim().ToUpperInvariant())));
                csv.WriteField("Always");
                csv.NextRecord();
            }
        }
        stream.Position = 0;
        return stream;
    }
}
