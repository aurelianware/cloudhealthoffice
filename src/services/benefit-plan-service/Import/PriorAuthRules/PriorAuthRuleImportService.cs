using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BenefitPlanService.Import.FeeSchedules;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using CloudHealthOffice.PriorAuthRuleEngine.Persistence;
using CloudHealthOffice.PriorAuthRuleEngine.Rules.Platform;
using CloudHealthOffice.PriorAuthRuleEngine.Services;
using CsvHelper;
using CsvHelper.Configuration;

namespace BenefitPlanService.Import.PriorAuthRules;

public interface IPriorAuthRuleImportService
{
    Task<PriorAuthRuleImportReport> ImportAsync(
        Stream csv, PriorAuthRuleImportOptions options, string tenantId, string? actor, CancellationToken ct = default);
}

/// <summary>
/// Loads a plan's prior-auth-required list (a CSV of codes, code ranges and
/// the conditions under which they need authorization) into the prior-auth
/// rule store as the tenant's rule set for one state, line of business and
/// program. That store is what CRD, PAS <c>$submit</c> and the CMS-0057-F
/// published list (<c>GET api/v1/prior-auth-rules/required-services</c>)
/// read, so all three answer from the plan's own list once it is loaded.
///
/// <para>
/// The file is the whole list for its scope: rules of an earlier import of the
/// same scope that the file no longer has are disabled. The rule set resolver
/// uses the most specific non-empty set, so the imported set replaces the
/// platform set for that scope; the report names what stops applying, and the
/// replaced set's exemptions (gold card etc.) are carried over by default.
/// </para>
/// See docs/integration/PRIOR-AUTH-RULES-IMPORTER.md.
/// </summary>
public sealed class PriorAuthRuleImportService(
    IPaRuleRepository repository,
    ILogger<PriorAuthRuleImportService> logger) : IPriorAuthRuleImportService
{
    /// <summary>Rule ids this importer owns start with it; nothing else in the store does.</summary>
    public const string ImportedRuleIdPrefix = "import:";

    /// <summary>Copied exemptions start with it (after <see cref="ImportedRuleIdPrefix"/>).</summary>
    public const string KeptExemptionMarker = "keep:";
    /// <summary>Lists loaded beside the plan's own list in one scope (<see cref="PriorAuthRuleImportOptions.RuleNamespace"/>).</summary>
    public static readonly IReadOnlySet<string> RuleNamespaces = new HashSet<string>(StringComparer.Ordinal) { "tmppm", "medi-cal" };

    public const int MaxHeaderSearchRows = 30;

    /// <summary>A range wider than this is almost certainly a typo, and would bloat the rule.</summary>
    public const int MaxCodesPerRange = 1000;

    private const int MaxExamples = 25;
    private const int MaxCodesInReport = 25;

    private static readonly HashSet<string> ExemptionTypes = new(StringComparer.OrdinalIgnoreCase)
        { "TxGoldCardExemption", "ProviderTypeExemption", "MemberAgeLimit" };

    // ── Column aliases (compared after FeeScheduleCsvReader.Normalize) ─────────
    internal static readonly string[] RuleIdAliases = ["ruleid"];
    internal static readonly string[] NameAliases =
        ["name", "category", "servicecategory", "servicetype", "service", "benefitcategory", "group", "servicegroup"];
    internal static readonly string[] CodeAliases =
        ["codes", "code", "procedurecode", "procedurecodes", "proccode", "cpt", "cptcode", "cptcodes", "hcpcs", "hcpcscode",
         "cpthcpcs", "cpthcpcscode", "hcpcscpt", "servicecode", "cdtcode", "cdt"];
    internal static readonly string[] RangeAliases = ["coderanges", "coderange", "range", "ranges", "procedurecoderange"];
    internal static readonly string[] AllServicesAliases = ["allservices"];
    internal static readonly string[] ConditionAliases = ["condition", "when"];
    internal static readonly string[] PlaceOfServiceAliases =
        ["placesofservice", "placeofservice", "pos", "poscode", "poscodes"];
    internal static readonly string[] DiagnosisAliases =
        ["diagnoses", "diagnosis", "diagnosiscodes", "diagnosiscode", "icd10", "icd10codes", "icd10cm", "dx", "dxcodes"];
    internal static readonly string[] UnitLimitAliases = ["unitlimit", "units", "maxunits"];
    internal static readonly string[] VisitLimitAliases = ["visitlimit", "visits", "maxvisits"];
    internal static readonly string[] DeniedAliases = ["deniedwithoutreview", "notcovered"];
    internal static readonly string[] RequiredAliases =
        ["parequired", "priorauthrequired", "priorauthorizationrequired", "authrequired", "authorizationrequired",
         "requirespriorauth", "requirespriorauthorization", "requiresauth", "pa"];

    private static readonly Regex RangeJoiner = new(@"\s*(?:-|–|—|\bthru\b|\bthrough\b|\bto\b)\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex CodeShape = new(@"^[A-Z0-9]{5}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex RangeEnd = new(@"^([A-Z]*)(\d+)([A-Z]*)$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex DiagnosisShape = new(@"^[A-Z][0-9][0-9A-Z]{1,5}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly char[] ListSeparators = [' ', ',', ';', '|', '\n', '\r', '\t'];

    private sealed record Columns(
        int? RuleId, int? Name, int? Codes, int? Ranges, int? AllServices, int? Condition, int? PlacesOfService,
        int? Diagnoses, int? UnitLimit, int? VisitLimit, int? Denied, int? Required, string[] Header);

    /// <summary>The attributes that make rows one rule; the codes are merged.</summary>
    private sealed record RuleShape(
        string BaseId, string Name, PaRequirementCondition Condition, string PlacesOfService, string Diagnoses,
        int? UnitLimit, int? VisitLimit, bool Denied, bool AllServices);

    private sealed class RuleBuilder(RuleShape shape)
    {
        public RuleShape Shape { get; } = shape;
        public SortedSet<string> Codes { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> Prefixes { get; } = new(StringComparer.Ordinal);
    }

    public async Task<PriorAuthRuleImportReport> ImportAsync(
        Stream csv, PriorAuthRuleImportOptions options, string tenantId, string? actor, CancellationToken ct = default)
    {
        var ns = options.RuleNamespace;
        if (ns is not null && !RuleNamespaces.Contains(ns))
            throw new ArgumentException($"Unknown rule namespace '{ns}'.", nameof(options));
        var idPrefix = ImportedRuleIdPrefix + (ns is null ? "" : ns + ":");
        var state = options.StateCode.Trim().ToUpperInvariant();
        var lob = options.LineOfBusiness;
        var program = string.IsNullOrWhiteSpace(options.Program) ? null : options.Program.Trim();
        var targetKey = new RuleSetKey { StateCode = state, Lob = lob, Program = program, TenantId = tenantId };

        var skipped = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var examples = new List<PriorAuthRuleSkippedRow>();
        void Skip(int row, string reason, string? value)
        {
            skipped[reason] = skipped.GetValueOrDefault(reason) + 1;
            if (examples.Count < MaxExamples) examples.Add(new PriorAuthRuleSkippedRow(row, reason, value));
        }

        var builders = new Dictionary<RuleShape, RuleBuilder>();
        var drugCodes = new SortedSet<string>(StringComparer.Ordinal);
        Columns? columns = null;
        var headerRow = 0;
        var rowsRead = 0;
        var rowsLoaded = 0;

        foreach (var (rowNumber, fields) in ReadRows(csv, (c, row) => { columns = c; headerRow = row; }))
        {
            ct.ThrowIfCancellationRequested();
            rowsRead++;
            var c = columns!;
            string Cell(int? index) => index is { } i && i < fields.Length ? fields[i].Trim() : string.Empty;

            if (c.Required is not null && IsNo(Cell(c.Required)))
            {
                Skip(rowNumber, "the PA-required column says no", Cell(c.Required));
                continue;
            }

            // Codes and ranges: both columns may hold codes, ranges ("70551-70553") and prefixes ("K*").
            var codes = new SortedSet<string>(StringComparer.Ordinal);
            var prefixes = new SortedSet<string>(StringComparer.Ordinal);
            string? badCode = null;
            foreach (var token in Tokens(Cell(c.Codes)).Concat(Tokens(Cell(c.Ranges))))
            {
                if (!TryAddCodes(token, codes, prefixes, out var reason))
                {
                    badCode = reason;
                    Skip(rowNumber, reason, token);
                    break;
                }
            }
            if (badCode is not null) continue;

            var allServices = IsYes(Cell(c.AllServices));
            if (codes.Count == 0 && prefixes.Count == 0 && !allServices)
            {
                Skip(rowNumber, "no procedure code", null);
                continue;
            }
            if (allServices && (codes.Count > 0 || prefixes.Count > 0))
            {
                Skip(rowNumber, "all_services is true but the row also names codes", Cell(c.Codes));
                continue;
            }

            if (!TryReadPlacesOfService(Cell(c.PlacesOfService), out var pos))
            {
                Skip(rowNumber, "place of service is not a 2-digit code", Cell(c.PlacesOfService));
                continue;
            }
            if (!TryReadDiagnoses(Cell(c.Diagnoses), out var diagnoses))
            {
                Skip(rowNumber, "diagnosis is not an ICD-10-CM code", Cell(c.Diagnoses));
                continue;
            }
            if (!TryReadLimit(Cell(c.UnitLimit), out var unitLimit) || !TryReadLimit(Cell(c.VisitLimit), out var visitLimit))
            {
                Skip(rowNumber, "unit or visit limit is not a positive whole number",
                    $"{Cell(c.UnitLimit)} {Cell(c.VisitLimit)}".Trim());
                continue;
            }

            var conditionCell = Cell(c.Condition);
            PaRequirementCondition condition;
            if (conditionCell.Length == 0)
            {
                condition = unitLimit is not null || visitLimit is not null ? PaRequirementCondition.AboveQuantityLimit
                    : diagnoses.Count > 0 ? PaRequirementCondition.WithDiagnosis
                    : PaRequirementCondition.Always;
            }
            else if (!TryReadCondition(conditionCell, out condition))
            {
                Skip(rowNumber, "condition is not Always, WithDiagnosis or AboveQuantityLimit", conditionCell);
                continue;
            }

            if (condition == PaRequirementCondition.WithDiagnosis && diagnoses.Count == 0)
            {
                Skip(rowNumber, "condition WithDiagnosis needs diagnoses", null);
                continue;
            }
            if (condition == PaRequirementCondition.AboveQuantityLimit && unitLimit is null && visitLimit is null)
            {
                Skip(rowNumber, "condition AboveQuantityLimit needs a unit or visit limit", null);
                continue;
            }

            var denied = IsYes(Cell(c.Denied));
            if (denied && condition != PaRequirementCondition.Always)
            {
                Skip(rowNumber, "denied_without_review applies to condition Always only", conditionCell);
                continue;
            }

            // Fields that do not apply to the condition are dropped, as the published list drops them.
            if (condition != PaRequirementCondition.Always) pos = [];
            if (condition != PaRequirementCondition.WithDiagnosis) diagnoses = [];
            if (condition != PaRequirementCondition.AboveQuantityLimit) { unitLimit = null; visitLimit = null; }

            var ruleIdCell = Cell(c.RuleId);
            var nameCell = Cell(c.Name);
            var baseId = Slug(ruleIdCell.Length > 0 ? ruleIdCell : nameCell.Length > 0 ? nameCell : "listed-services");
            var name = nameCell.Length > 0 ? nameCell : ruleIdCell.Length > 0 ? ruleIdCell : "Listed services";
            var shape = new RuleShape(baseId, name, condition, string.Join(' ', pos), string.Join(' ', diagnoses),
                unitLimit, visitLimit, denied, allServices);

            if (!builders.TryGetValue(shape, out var builder))
                builders[shape] = builder = new RuleBuilder(shape);
            builder.Codes.UnionWith(codes);
            builder.Prefixes.UnionWith(prefixes);
            foreach (var code in codes.Concat(prefixes).Where(x => x.StartsWith('J'))) drugCodes.Add(code);
            rowsLoaded++;
        }

        if (columns is null)
            throw new PriorAuthRuleFormatException(
                $"No header row with a procedure code column was found in the first {MaxHeaderSearchRows} rows. " +
                "Name the column codes, code_ranges, Procedure Code, CPT/HCPCS or Code.");

        var now = DateTime.UtcNow;
        var provenance = $"Imported from {(string.IsNullOrWhiteSpace(options.SourceName) ? "a CSV file" : options.SourceName)} " +
                         $"on {now:yyyy-MM-dd} by {(string.IsNullOrWhiteSpace(actor) ? "unknown" : actor)}.";

        var imported = BuildRules(builders.Values, idPrefix, state, lob, program, tenantId, provenance);

        // What requests for this scope resolve to without this tenant's imported rules:
        // the set the import replaces, and where the exemptions to keep come from.
        var withoutImports = new ImportedRulesHidden(repository);
        var (replacedKey, replacedRules) = await PaRuleSetResolver.ResolveAsync(
            withoutImports, state, lob, program, tenantId, programId: null, ct);

        var kept = new List<PaRuleDocument>();
        if (options.KeepExemptions && imported.Count > 0)
        {
            foreach (var rule in replacedRules.Where(r => ExemptionTypes.Contains(r.RuleType)
                         || r.Category == RuleCategory.RegulatoryExemption))
                kept.Add(CopyExemption(rule, state, lob, program, tenantId, provenance));
        }

        var keptSources = kept.Select(k => k.RuleId[(ImportedRuleIdPrefix.Length + KeptExemptionMarker.Length)..])
            .ToHashSet(StringComparer.Ordinal);
        var noLongerApplied = imported.Count == 0
            ? []
            : replacedRules.Where(r => !keptSources.Contains(r.RuleId)).Select(r => r.RuleId).ToList();

        var toWrite = imported.Concat(kept).ToList();
        var newIds = toWrite.Select(r => r.RuleId).ToHashSet(StringComparer.Ordinal);

        var existing = (await repository.ListAsync(tenantId, state, ct))
            .Where(r => r.TenantId == tenantId && r.Lob == lob && r.Program == program
                        && InNamespace(r.RuleId, ns))
            .ToList();
        var stale = existing.Where(r => r.IsEnabled && !newIds.Contains(r.RuleId)).ToList();
        var createdAt = existing.ToDictionary(r => r.RuleId, r => r.CreatedAt, StringComparer.Ordinal);
        foreach (var rule in toWrite)
            if (createdAt.TryGetValue(rule.RuleId, out var created)) rule.CreatedAt = created;

        var warnings = new List<string>();
        if (program is null)
        {
            var programSets = (await repository.ListAsync(null, state, ct))
                .Where(r => r.TenantId is null && r.IsEnabled && r.Lob == lob && r.Program is not null
                            && !r.RuleId.StartsWith(ImportedRuleIdPrefix, StringComparison.Ordinal))
                .Select(r => r.Program!)
                .Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList();
            if (programSets.Count > 0)
                warnings.Add($"The platform has program rule sets for {state}/{lob} ({string.Join(", ", programSets)}). " +
                             "Requests that name one of those programs still use the platform set, because a program's set " +
                             "outranks a program-less tenant set. Import the list once per program to cover them.");
        }
        if (drugCodes.Count > 0)
            warnings.Add($"{drugCodes.Count} drug code(s) (HCPCS J) are in the file. CRD and PAS apply them; the " +
                         "CMS-0057-F public list leaves drugs out, so they will not appear on it.");
        if (imported.Count > 0 && replacedKey is not null && noLongerApplied.Count > 0)
            warnings.Add($"The import replaces rule set {replacedKey} for this scope: {noLongerApplied.Count} of its " +
                         "rules stop applying (see rulesNoLongerApplied).");

        var outcome = imported.Count == 0
            ? PriorAuthRuleImportOutcome.NothingToImport
            : options.DryRun ? PriorAuthRuleImportOutcome.WouldWrite : PriorAuthRuleImportOutcome.Written;

        if (outcome == PriorAuthRuleImportOutcome.Written)
        {
            foreach (var rule in stale)
            {
                rule.IsEnabled = false;
                rule.UpdatedAt = now;
            }
            await repository.BulkUpsertAsync(toWrite.Concat(stale).ToList(), ct);
            logger.LogInformation(
                "Imported {Rules} prior-auth rules ({Kept} exemptions kept, {Disabled} disabled) into {RuleSet}",
                imported.Count, kept.Count, stale.Count, targetKey.ToString());
        }

        return new PriorAuthRuleImportReport
        {
            Outcome = outcome,
            RuleSet = targetKey.ToString(),
            HeaderRow = headerRow,
            Columns = Describe(columns),
            RowsRead = rowsRead,
            RowsLoaded = rowsLoaded,
            Skipped = skipped,
            SkippedExamples = examples,
            Rules = imported.Select(r => Summarize(r, options.IncludeCodes)).ToList(),
            DistinctCodes = imported.SelectMany(r => r.ProcedureCodes).Distinct(StringComparer.Ordinal).Count(),
            DrugCodes = drugCodes.ToList(),
            ReplacesRuleSet = replacedKey?.ToString(),
            RulesNoLongerApplied = noLongerApplied,
            ExemptionsKept = kept.Select(k => k.RuleId).ToList(),
            RulesDisabled = outcome == PriorAuthRuleImportOutcome.NothingToImport ? [] : stale.Select(r => r.RuleId).ToList(),
            Warnings = warnings,
        };
    }

    // ── Rules ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether an imported rule belongs to the list being loaded: a namespaced
    /// list owns its own ids; the plan's own list owns every other imported id,
    /// kept exemptions included.
    /// </summary>
    internal static bool InNamespace(string ruleId, string? ns)
    {
        if (!ruleId.StartsWith(ImportedRuleIdPrefix, StringComparison.Ordinal)) return false;
        if (ns is not null) return ruleId.StartsWith(ImportedRuleIdPrefix + ns + ":", StringComparison.Ordinal);
        return !RuleNamespaces.Any(n => ruleId.StartsWith(ImportedRuleIdPrefix + n + ":", StringComparison.Ordinal));
    }

    private static List<PaRuleDocument> BuildRules(
        IEnumerable<RuleBuilder> builders, string idPrefix, string state, PaLineOfBusiness lob, string? program,
        string tenantId, string provenance)
    {
        var rules = new List<PaRuleDocument>();
        // Rows sharing a rule id or name but differing in condition, place of service,
        // diagnoses or limits are separate rules; each such group gets a stable suffix.
        foreach (var group in builders.GroupBy(b => b.Shape.BaseId, StringComparer.Ordinal))
        {
            var shapes = group.ToList();
            foreach (var builder in shapes)
            {
                var s = builder.Shape;
                var ruleId = idPrefix + s.BaseId + (shapes.Count == 1 ? "" : "-" + ShapeHash(s));
                var (ruleType, category, priority) = s.Condition switch
                {
                    PaRequirementCondition.WithDiagnosis => Engine(new DiagnosisRequiredRule()),
                    PaRequirementCondition.AboveQuantityLimit => Engine(new QuantityLimitRule()),
                    _ => Engine(new ProcedureRequiresAuthRule()),
                };
                rules.Add(new PaRuleDocument
                {
                    RuleId = ruleId,
                    RuleName = s.Name,
                    Description = provenance,
                    StateCode = state,
                    Lob = lob,
                    Program = program,
                    TenantId = tenantId,
                    Category = category,
                    Scope = RuleScope.Tenant,
                    Priority = priority,
                    IsEnabled = true,
                    RuleType = ruleType,
                    ProcedureCodes = builder.Codes.ToList(),
                    ProcedureCodePrefixes = builder.Prefixes.ToList(),
                    PlaceOfServiceCodes = Split(s.PlacesOfService),
                    RequiredDiagnosisCodes = Split(s.Diagnoses),
                    UnitLimit = s.UnitLimit,
                    VisitLimit = s.VisitLimit,
                    // ProcedureRequiresAuth denies outright when a denial code is set (PaRuleBase's default code).
                    DenialCode = s.Denied ? "AUTH-DENY" : null,
                });
            }
        }
        return rules.OrderBy(r => r.RuleId, StringComparer.Ordinal).ToList();

        static (string, RuleCategory, int) Engine(CloudHealthOffice.PriorAuthRuleEngine.Rules.PaRuleBase rule)
            => (rule.RuleType, rule.Category, rule.Priority);
        static List<string> Split(string joined) =>
            joined.Length == 0 ? [] : joined.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static PaRuleDocument CopyExemption(
        PaRuleDocument source, string state, PaLineOfBusiness lob, string? program, string tenantId, string provenance)
    {
        var sourceId = source.RuleId.StartsWith(ImportedRuleIdPrefix + KeptExemptionMarker, StringComparison.Ordinal)
            ? source.RuleId[(ImportedRuleIdPrefix.Length + KeptExemptionMarker.Length)..]
            : source.RuleId;
        return new PaRuleDocument
        {
            RuleId = ImportedRuleIdPrefix + KeptExemptionMarker + sourceId,
            RuleName = source.RuleName,
            Description = $"Copy of {(source.TenantId is null ? "platform" : "tenant")} rule {sourceId} " +
                          $"({source.StateCode}/{source.Lob}/{source.Program ?? "any"}), kept when the prior-auth list was imported. " +
                          provenance,
            StateCode = state,
            Lob = lob,
            Program = program,
            TenantId = tenantId,
            Category = source.Category,
            Scope = RuleScope.Tenant,
            Priority = source.Priority,
            IsEnabled = true,
            RuleType = source.RuleType,
            ProcedureCodes = source.ProcedureCodes,
            ProcedureCodePrefixes = source.ProcedureCodePrefixes,
            RequiredDiagnosisCodes = source.RequiredDiagnosisCodes,
            PlaceOfServiceCodes = source.PlaceOfServiceCodes,
            ExemptTaxonomyPrefixes = source.ExemptTaxonomyPrefixes,
            CostThreshold = source.CostThreshold,
            UnitLimit = source.UnitLimit,
            VisitLimit = source.VisitLimit,
            GoldCardApprovalRateThreshold = source.GoldCardApprovalRateThreshold,
            GoldCardMinimumDecisions = source.GoldCardMinimumDecisions,
            MaxMemberAgeYears = source.MaxMemberAgeYears,
            MinMemberAgeYears = source.MinMemberAgeYears,
            DenialCode = source.DenialCode,
            DenialReasonTemplate = source.DenialReasonTemplate,
        };
    }

    private static ImportedPriorAuthRule Summarize(PaRuleDocument rule, bool includeCodes) => new()
    {
        RuleId = rule.RuleId,
        Name = rule.RuleName,
        RuleType = rule.RuleType,
        Condition = rule.RuleType switch
        {
            "DiagnosisRequired" => PaRequirementCondition.WithDiagnosis,
            "QuantityLimit" => PaRequirementCondition.AboveQuantityLimit,
            _ => PaRequirementCondition.Always,
        },
        CodeCount = rule.ProcedureCodes.Count,
        Codes = includeCodes ? rule.ProcedureCodes : rule.ProcedureCodes.Take(MaxCodesInReport).ToList(),
        CodePrefixes = rule.ProcedureCodePrefixes,
        PlacesOfService = rule.PlaceOfServiceCodes,
        Diagnoses = rule.RequiredDiagnosisCodes,
        UnitLimit = rule.UnitLimit,
        VisitLimit = rule.VisitLimit,
        DeniedWithoutReview = !string.IsNullOrEmpty(rule.DenialCode),
        AllServices = rule.ProcedureCodes.Count == 0 && rule.ProcedureCodePrefixes.Count == 0,
    };

    /// <summary>The repository as it would be without this importer's rules, for finding the set it replaces.</summary>
    private sealed class ImportedRulesHidden(IPaRuleRepository inner) : IPaRuleRepository
    {
        public async Task<IReadOnlyList<PaRuleDocument>> GetRulesAsync(RuleSetKey key, CancellationToken ct = default)
            => (await inner.GetRulesAsync(key, ct))
                .Where(r => !r.RuleId.StartsWith(ImportedRuleIdPrefix, StringComparison.Ordinal)).ToList();

        public Task UpsertAsync(PaRuleDocument rule, CancellationToken ct = default) => throw new NotSupportedException();
        public Task BulkUpsertAsync(IEnumerable<PaRuleDocument> rules, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string ruleId, string stateCode, string? tenantId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PaRuleDocument>> ListAsync(string? tenantId = null, string? stateCode = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    // ── Reading ───────────────────────────────────────────────────────────────

    private static IEnumerable<(int Row, string[] Fields)> ReadRows(Stream csv, Action<Columns, int> onHeader)
    {
        using var reader = new StreamReader(csv, detectEncodingFromByteOrderMarks: true);
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = false,
            BadDataFound = null,
            MissingFieldFound = null,
            TrimOptions = TrimOptions.None,
            DetectColumnCountChanges = false,
        };
        using var parser = new CsvParser(reader, config);

        Columns? found = null;
        var searched = 0;
        while (found is null && searched < MaxHeaderSearchRows && parser.Read())
        {
            searched++;
            found = TryResolve(parser.Record ?? []);
            if (found is not null) onHeader(found, parser.Row);
        }
        if (found is null) yield break;

        while (parser.Read())
        {
            var record = parser.Record;
            if (record is null || record.All(string.IsNullOrWhiteSpace)) continue;
            yield return (parser.Row, record);
        }
    }

    private static Columns? TryResolve(string[] record)
    {
        var normalized = record.Select(FeeScheduleCsvReader.Normalize).ToArray();
        int? Find(string[] aliases)
        {
            foreach (var alias in aliases)
            {
                var index = Array.IndexOf(normalized, alias);
                if (index >= 0) return index;
            }
            return null;
        }

        var codes = Find(CodeAliases);
        var ranges = Find(RangeAliases);
        var allServices = Find(AllServicesAliases);
        if (codes is null && ranges is null) return null;
        return new Columns(Find(RuleIdAliases), Find(NameAliases), codes, ranges, allServices, Find(ConditionAliases),
            Find(PlaceOfServiceAliases), Find(DiagnosisAliases), Find(UnitLimitAliases), Find(VisitLimitAliases),
            Find(DeniedAliases), Find(RequiredAliases), record);
    }

    private static IReadOnlyDictionary<string, string> Describe(Columns c)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        void Add(string role, int? index) { if (index is { } i) map[role] = c.Header[i].Trim(); }
        Add("ruleId", c.RuleId); Add("name", c.Name); Add("codes", c.Codes); Add("codeRanges", c.Ranges);
        Add("allServices", c.AllServices); Add("condition", c.Condition); Add("placesOfService", c.PlacesOfService);
        Add("diagnoses", c.Diagnoses); Add("unitLimit", c.UnitLimit); Add("visitLimit", c.VisitLimit);
        Add("deniedWithoutReview", c.Denied); Add("paRequired", c.Required);
        return map;
    }

    // ── Cells ─────────────────────────────────────────────────────────────────

    /// <summary>Splits a cell into code tokens, first joining "70551 - 70553" / "70551 thru 70553" into one range token.</summary>
    internal static IEnumerable<string> Tokens(string cell)
    {
        if (string.IsNullOrWhiteSpace(cell)) return [];
        var joined = RangeJoiner.Replace(cell.Trim(), "-");
        return joined.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().Trim('"', '\'').ToUpperInvariant())
            .Where(t => t.Length > 0);
    }

    /// <summary>A code, a "K*" prefix, or a range of codes that differ only in their numeric part.</summary>
    internal static bool TryAddCodes(string token, ISet<string> codes, ISet<string> prefixes, out string reason)
    {
        reason = string.Empty;
        if (token.EndsWith('*'))
        {
            var prefix = token.TrimEnd('*');
            if (prefix.Length is >= 1 and <= 4 && prefix.All(char.IsAsciiLetterOrDigit))
            {
                prefixes.Add(prefix);
                return true;
            }
            reason = "code prefix is not 1 to 4 letters or digits followed by *";
            return false;
        }

        var dash = token.IndexOf('-');
        if (dash > 0)
        {
            var from = token[..dash];
            var to = token[(dash + 1)..];
            var a = RangeEnd.Match(from);
            var b = RangeEnd.Match(to);
            if (!CodeShape.IsMatch(from) || !CodeShape.IsMatch(to) || !a.Success || !b.Success
                || a.Groups[1].Value != b.Groups[1].Value || a.Groups[3].Value != b.Groups[3].Value
                || a.Groups[2].Length != b.Groups[2].Length)
            {
                reason = "code range ends are not two codes of the same shape";
                return false;
            }
            var width = a.Groups[2].Length;
            var start = int.Parse(a.Groups[2].Value, CultureInfo.InvariantCulture);
            var end = int.Parse(b.Groups[2].Value, CultureInfo.InvariantCulture);
            if (end < start)
            {
                reason = "code range ends before it starts";
                return false;
            }
            if (end - start + 1 > MaxCodesPerRange)
            {
                reason = $"code range holds more than {MaxCodesPerRange} codes";
                return false;
            }
            for (var n = start; n <= end; n++)
                codes.Add(a.Groups[1].Value + n.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0') + a.Groups[3].Value);
            return true;
        }

        if (CodeShape.IsMatch(token))
        {
            codes.Add(token);
            return true;
        }
        reason = "not a 5-character CPT, HCPCS or CDT code";
        return false;
    }

    private static bool TryReadPlacesOfService(string cell, out List<string> pos)
    {
        pos = [];
        foreach (var token in cell.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = token.Trim();
            if (t.Length is < 1 or > 2 || !t.All(char.IsAsciiDigit)) return false;
            pos.Add(t.PadLeft(2, '0'));
        }
        pos = pos.Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();
        return true;
    }

    private static bool TryReadDiagnoses(string cell, out List<string> diagnoses)
    {
        diagnoses = [];
        foreach (var token in cell.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = token.Trim().Replace(".", "", StringComparison.Ordinal).ToUpperInvariant();
            if (!DiagnosisShape.IsMatch(t)) return false;
            diagnoses.Add(t);
        }
        diagnoses = diagnoses.Distinct().OrderBy(d => d, StringComparer.Ordinal).ToList();
        return true;
    }

    private static bool TryReadLimit(string cell, out int? limit)
    {
        limit = null;
        if (cell.Length == 0) return true;
        if (int.TryParse(cell, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0)
        {
            limit = value;
            return true;
        }
        return false;
    }

    private static bool TryReadCondition(string cell, out PaRequirementCondition condition)
    {
        switch (FeeScheduleCsvReader.Normalize(cell))
        {
            case "always": case "required": case "yes": condition = PaRequirementCondition.Always; return true;
            case "withdiagnosis": case "diagnosis": condition = PaRequirementCondition.WithDiagnosis; return true;
            case "abovequantitylimit": case "quantitylimit": case "abovelimit":
                condition = PaRequirementCondition.AboveQuantityLimit; return true;
            default: condition = default; return false;
        }
    }

    private static bool IsYes(string cell) =>
        FeeScheduleCsvReader.Normalize(cell) is "true" or "yes" or "y" or "1" or "x" or "required";

    private static bool IsNo(string cell) =>
        FeeScheduleCsvReader.Normalize(cell) is "false" or "no" or "n" or "0" or "notrequired" or "none";

    /// <summary>Lower-case letters, digits and dashes, at most 48 characters.</summary>
    internal static string Slug(string value)
    {
        var sb = new StringBuilder();
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        if (slug.Length > 48) slug = slug[..48].TrimEnd('-');
        return slug.Length == 0 ? "listed-services" : slug;
    }

    private static string ShapeHash(RuleShape s)
    {
        var text = $"{s.Condition}|{s.PlacesOfService}|{s.Diagnoses}|{s.UnitLimit}|{s.VisitLimit}|{s.Denied}|{s.AllServices}|{s.Name}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..8].ToLowerInvariant();
    }
}
