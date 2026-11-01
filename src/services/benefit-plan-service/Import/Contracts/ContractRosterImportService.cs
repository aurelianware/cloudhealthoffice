using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BenefitPlanService.Import.FeeSchedules;
using BenefitPlanService.Repositories;
using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Persistence;
using CloudHealthOffice.Infrastructure.Logging;
using CsvHelper;
using CsvHelper.Configuration;

namespace BenefitPlanService.Import.Contracts;

public sealed class ContractRosterImportOptions
{
    /// <summary>Contracts one request may write.</summary>
    public const int MaxContracts = 20_000;

    /// <summary>Short source label stored as the contract's origin, e.g. "qnxt" → origin "import:qnxt".</summary>
    public string Source { get; init; } = "csv";

    /// <summary>Replace a contract this importer did not write (origin not "import:..."). Off by default.</summary>
    public bool ReplaceNonImported { get; init; }

    public bool DryRun { get; init; } = true;

    /// <summary>Where the contracts live: the plan's core (a pricing copy) or CHO's contracts service.</summary>
    public ContractSystemOfRecord SystemOfRecord { get; init; } = ContractSystemOfRecord.Core;

    /// <summary>Return every contract in the report, not only a sample.</summary>
    public bool IncludeContracts { get; init; }
}

/// <summary>Which system holds the plan's provider contracts.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContractSystemOfRecord
{
    /// <summary>The plan's core admin system (QNXT, Facets, ...); CHO holds a pricing copy tagged import:{source}.</summary>
    Core,
    /// <summary>CHO: the import creates the legal contracts in provider-contracts-service and prices from their projection.</summary>
    Cho,
}

/// <summary>What happened to one CHO legal contract (CHO mode).</summary>
public sealed record LegalContractResult(
    [property: JsonPropertyName("contractNumber")] string ContractNumber,
    [property: JsonPropertyName("npi")] string Npi,
    [property: JsonPropertyName("legalContractId")] string? LegalContractId,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("detail")] string? Detail = null);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContractImportOutcome { WouldCreate, WouldReplace, Created, Replaced, Unchanged, Protected }

public sealed record ContractRowError(
    [property: JsonPropertyName("row")] int Row,
    [property: JsonPropertyName("npi")] string Npi,
    [property: JsonPropertyName("error")] string Error);

public sealed class ContractRosterImportReport
{
    public bool DryRun { get; init; }
    public string Origin { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> Columns { get; set; } = new Dictionary<string, string>();
    public int RowsRead { get; set; }
    public int RowsRejected { get; set; }
    public List<ContractRowError> RowErrors { get; } = new();
    public int Contracts { get; set; }
    public int Terms { get; set; }
    public Dictionary<ContractImportOutcome, int> Outcomes { get; } = new();
    /// <summary>Fee schedules the file names that this tenant does not hold (their lines would pend).</summary>
    public List<string> MissingFeeSchedules { get; } = new();
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<ProviderContract> ContractsSample { get; set; } = new();
    public ContractSystemOfRecord SystemOfRecord { get; init; }
    /// <summary>CHO mode: the legal contracts created or updated in provider-contracts-service.</summary>
    public List<LegalContractResult> LegalContracts { get; } = new();
}

public interface IContractRosterImportService
{
    Task<ContractRosterImportReport> ImportAsync(
        Stream csv, ContractRosterImportOptions options, string tenantId, string? actor, CancellationToken ct = default);
}

/// <summary>
/// Loads a provider contract roster CSV (one row per provider, plan and
/// reimbursement term) into the contracts claims are priced from, so a
/// provider's lines are paid from the fee schedule, percent of Medicare or
/// percent of billed its contract names rather than the plan default.
///
/// <para>
/// The contracts are a <b>pricing copy</b> of the plan's own contracts (the
/// core admin system stays their system of record): each is tagged
/// <c>origin = import:{source}</c> and a later import replaces it. Contracts
/// written any other way are left alone unless <c>replaceNonImported</c> is set.
/// Rows for the same NPI and plan become one contract whose terms keep file
/// order (the engine uses the first term whose scope and dates match).
/// </para>
/// See docs/integration/CONTRACT-ROSTER-IMPORTER.md.
/// </summary>
public sealed class ContractRosterImportService : IContractRosterImportService
{
    public const string OriginPrefix = "import:";

    /// <summary>Origin the contracts service's pricing projection stamps on engine contracts.</summary>
    public const string LegalOrigin = "legal";
    private const int SampleSize = 20;

    internal static readonly Dictionary<string, string[]> Aliases = new()
    {
        ["npi"] = ["npi", "providernpi", "renderingnpi", "billingnpi", "nationalprovideridentifier"],
        ["plan"] = ["plan", "planid", "benefitplanid", "chplanid", "choplanid", "plancode"],
        ["network"] = ["networkstatus", "network", "participation", "parstatus", "innetwork"],
        ["effective"] = ["effectivedate", "contracteffectivedate", "effective", "startdate", "fromdate"],
        ["term"] = ["termdate", "terminationdate", "enddate", "thrudate", "todate"],
        ["termEffective"] = ["termeffectivedate", "rateeffectivedate"],
        ["termEnd"] = ["termenddate", "rateenddate"],
        ["contractNumber"] = ["contractnumber", "contractid", "contract", "agreementnumber"],
        ["lob"] = ["lineofbusiness", "lob", "product"],
        ["method"] = ["method", "reimbursementmethod", "paymentmethod", "pricingmethod"],
        ["feeSchedule"] = ["feescheduleid", "feeschedule", "scheduleid"],
        ["baseSchedule"] = ["basescheduleid", "medicarescheduleid", "basefeescheduleid", "baseschedule"],
        ["percent"] = ["percent", "percentage", "pct", "percentofmedicare", "percentofbilled"],
        ["multiplier"] = ["multiplier", "ratemultiplier", "factor"],
        ["codeFrom"] = ["codefrom", "procedurecodefrom", "fromcode", "codestart"],
        ["codeTo"] = ["codeto", "procedurecodeto", "tocode", "codeend"],
        ["claimType"] = ["claimtype", "settingtype", "formtype"],
        ["lesserOf"] = ["lesserofbilled", "lesserof", "lesserofbilledcharges", "lessorof"],
        ["description"] = ["description", "termdescription", "notes"],
        ["providerName"] = ["providername", "provider", "name", "practicename", "organizationname"],
        ["providerType"] = ["providertype", "entitytype", "npitype"],
    };

    private static readonly string[] DateFormats =
        ["yyyy-MM-dd", "M/d/yyyy", "MM/dd/yyyy", "yyyyMMdd", "M/d/yy", "MM/dd/yy", "yyyy/MM/dd"];

    private readonly IProviderContractRepository _contracts;
    private readonly IFeeScheduleRepository _schedules;
    private readonly IBenefitPlanRepository _plans;
    private readonly ILogger<ContractRosterImportService> _logger;
    private readonly IProviderContractsClient? _legal;

    public ContractRosterImportService(
        IProviderContractRepository contracts, IFeeScheduleRepository schedules,
        IBenefitPlanRepository plans, ILogger<ContractRosterImportService> logger,
        IProviderContractsClient? legal = null)
    {
        _legal = legal;
        _contracts = contracts;
        _schedules = schedules;
        _plans = plans;
        _logger = logger;
    }

    public async Task<ContractRosterImportReport> ImportAsync(
        Stream csv, ContractRosterImportOptions options, string tenantId, string? actor, CancellationToken ct = default)
    {
        var source = Slug(options.Source);
        var cho = options.SystemOfRecord == ContractSystemOfRecord.Cho;
        var report = new ContractRosterImportReport
        {
            DryRun = options.DryRun,
            Origin = cho ? LegalOrigin : OriginPrefix + source,
            SystemOfRecord = options.SystemOfRecord,
        };
        var providers = new Dictionary<string, (string Name, string Type)>();

        var planCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var scheduleCache = new Dictionary<string, bool>(StringComparer.Ordinal);
        var contracts = new Dictionary<(string Npi, string Plan), ProviderContract>();
        var order = new List<(string, string)>();

        await foreach (var (row, r) in ReadAsync(csv, report))
        {
            report.RowsRead++;
            var npi = r("npi").Replace(" ", string.Empty);
            void Reject(string error)
            {
                report.RowsRejected++;
                if (report.RowErrors.Count < 50) report.RowErrors.Add(new ContractRowError(row, npi, error));
            }

            if (!IsValidNpi(npi)) { Reject("npi is not a valid 10-digit NPI"); continue; }
            if (cho && r("contractNumber").Length == 0)
            {
                Reject("contract_number is required when CHO holds the contracts (rows with one number are one contract)");
                continue;
            }
            if (!providers.ContainsKey(npi) || providers[npi].Name.Length == 0)
                providers[npi] = (r("providerName"), r("providerType"));

            var planText = r("plan");
            if (planText.Length == 0) { Reject("plan is required (the CHO benefit plan id or plan code)"); continue; }
            if (!planCache.TryGetValue(planText, out var planId))
            {
                planId = await ResolvePlanAsync(planText, tenantId);
                planCache[planText] = planId;
            }
            if (planId is null) { Reject($"plan '{planText}' is not a benefit plan id, or the code of a published plan, in this tenant"); continue; }

            if (!TryDate(r("effective"), out var effective)) { Reject("effective_date is required (yyyy-MM-dd or MM/dd/yyyy)"); continue; }
            DateTime? term = null;
            if (r("term").Length > 0)
            {
                if (!TryDate(r("term"), out var t)) { Reject("term_date is unreadable"); continue; }
                term = t;
            }

            if (!TryNetwork(r("network"), out var network)) { Reject($"network_status '{r("network")}' is not in, out, par or non-par"); continue; }

            var (termModel, termError) = BuildTerm(r);
            if (termError is not null) { Reject(termError); continue; }

            var errors = termModel!.Validate();
            if (errors.Count > 0) { Reject(string.Join("; ", errors)); continue; }

            foreach (var id in new[] { termModel.FeeScheduleId, termModel.BaseScheduleId })
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (!scheduleCache.TryGetValue(id, out var exists))
                {
                    exists = await _schedules.GetByIdAsync(tenantId, id, ct) is not null;
                    scheduleCache[id] = exists;
                    if (!exists) report.MissingFeeSchedules.Add(id);
                }
            }

            var key = (npi, planId);
            if (!contracts.TryGetValue(key, out var contract))
            {
                contract = new ProviderContract
                {
                    Id = ProviderContract.MakeId(tenantId, npi, planId),
                    TenantId = tenantId,
                    ProviderNpi = npi,
                    PlanId = planId,
                    PlanIds = [planId],
                    EffectiveDate = effective,
                    TermDate = term,
                    NetworkStatus = network,
                    ContractNumber = NullIfEmpty(r("contractNumber")),
                    LineOfBusiness = NullIfEmpty(r("lob")),
                    Origin = report.Origin,
                };
                contracts[key] = contract;
                order.Add(key);
            }
            else
            {
                // The contract spans its earliest start and latest end; each term keeps its own dates.
                if (effective < contract.EffectiveDate) contract.EffectiveDate = effective;
                if (contract.TermDate is not null && (term is null || term > contract.TermDate)) contract.TermDate = term;
                if (contract.NetworkStatus != network)
                    report.Warnings.Add($"NPI {npi} plan {planId}: rows disagree on network status; kept {contract.NetworkStatus}.");
            }

            // Row dates bound the term when the term has no dates of its own.
            termModel.EffectiveDate ??= effective;
            termModel.TermDate ??= term;
            termModel.Id = TermId(contract.Id, contract.ReimbursementTerms.Count, termModel);
            contract.ReimbursementTerms.Add(termModel);
        }

        report.Contracts = contracts.Count;
        report.Terms = contracts.Values.Sum(c => c.ReimbursementTerms.Count);
        if (contracts.Count > ContractRosterImportOptions.MaxContracts)
            report.Errors.Add($"{contracts.Count} contracts exceed the {ContractRosterImportOptions.MaxContracts} per request; split the file.");
        if (contracts.Count == 0)
            report.Errors.Add("No contract rows could be read; see rowErrors.");
        if (report.MissingFeeSchedules.Count > 0)
            report.Warnings.Add(
                $"{report.MissingFeeSchedules.Count} fee schedule id(s) are not in this tenant ({string.Join(", ", report.MissingFeeSchedules.Take(5))}). " +
                "Lines priced by those terms will pend until the schedules are imported.");
        if (contracts.Values.Any(c => c.ReimbursementTerms.Count > 1
                && c.ReimbursementTerms.Take(c.ReimbursementTerms.Count - 1).Any(t => t.Scope.IsUnconditional)))
            report.Warnings.Add("Some contracts have an unconditional term before other terms; the engine stops at the first match, so the later terms are never used.");

        var sample = new List<ProviderContract>();
        if (report.Errors.Count == 0 && cho)
        {
            await WriteChoContractsAsync(contracts, order, providers, options, report, tenantId, sample, ct);
        }
        else if (report.Errors.Count == 0)
        {
            foreach (var key in order)
            {
                ct.ThrowIfCancellationRequested();
                var contract = contracts[key];
                // By id, not by service date: a stored contract with other dates is still this one.
                var existing = (await _contracts.ListByProviderAsync(tenantId, contract.ProviderNpi, ct))
                    .FirstOrDefault(c => c.Id == contract.Id);

                var outcome = await WriteAsync(contract, existing, options, ct);
                report.Outcomes[outcome] = report.Outcomes.GetValueOrDefault(outcome) + 1;
                if (options.IncludeContracts || sample.Count < SampleSize) sample.Add(contract);
            }
        }
        report.ContractsSample = sample;
        if (report.Outcomes.GetValueOrDefault(ContractImportOutcome.Protected) > 0)
            report.Warnings.Add("Some contracts were written outside this importer and were left alone; pass replaceNonImported=true to replace them.");

        _logger.LogInformation(
            "AUDIT contract roster import tenant={Tenant} origin={Origin} contracts={Contracts} rejected={Rejected} dryRun={DryRun} actor={Actor}",
            LogSanitizer.Sanitize(tenantId, 256), LogSanitizer.Sanitize(report.Origin, 64), report.Contracts,
            report.RowsRejected, options.DryRun, LogSanitizer.Sanitize(actor, 256));

        return report;
    }

    /// <summary>
    /// CHO mode: one legal contract per NPI and contract number in
    /// provider-contracts-service (created, or its rate sheet replaced), activated,
    /// then its pricing projection written to the engine store so claims price from it.
    /// </summary>
    private async Task WriteChoContractsAsync(
        Dictionary<(string Npi, string Plan), ProviderContract> contracts, List<(string, string)> order,
        Dictionary<string, (string Name, string Type)> providers, ContractRosterImportOptions options,
        ContractRosterImportReport report, string tenantId, List<ProviderContract> sample, CancellationToken ct)
    {
        if (_legal is null)
        {
            report.Errors.Add("provider-contracts-service is not configured (Services:ProviderContractsServiceUrl).");
            return;
        }

        var groups = order.Select(k => contracts[k])
            .GroupBy(c => (c.ProviderNpi, Number: c.ContractNumber!))
            .ToList();

        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            var (npi, number) = group.Key;
            var first = group.First();
            if (group.Any(c => TermsFingerprint(c.ReimbursementTerms) != TermsFingerprint(first.ReimbursementTerms)))
            {
                report.LegalContracts.Add(new(number, npi, null, "Rejected",
                    "the plans under this contract number have different terms; a CHO contract has one rate sheet for all its plans"));
                continue;
            }

            var terms = first.ReimbursementTerms
                .Select((t, i) => { t.Id = TermId($"{tenantId}:{number}", i, t); return t; })
                .ToList();
            var planIds = group.Select(c => c.PlanId).Distinct().ToList();

            try
            {
                var existing = await _legal.GetByNumberAsync(tenantId, number, ct);
                if (existing is not null && existing.ProviderNpi != npi)
                {
                    report.LegalContracts.Add(new(number, npi, existing.Id, "Rejected",
                        $"contract number {number} is already CHO's contract for NPI {existing.ProviderNpi}"));
                    continue;
                }
                if (existing?.Status is LegalContractStatus.Terminated or LegalContractStatus.Expired)
                {
                    report.LegalContracts.Add(new(number, npi, existing.Id, "Rejected",
                        $"the CHO contract is {existing.Status}; reinstate or renew it in the contracts portal"));
                    continue;
                }

                var termsChanged = existing is null || TermsFingerprint(existing.ReimbursementTerms) != TermsFingerprint(terms);
                var missingPlans = existing is null ? [] : planIds.Except(existing.PlanIds).ToList();
                var detail = missingPlans.Count > 0
                    ? $"plans {string.Join(", ", missingPlans)} are not on the CHO contract; add them in the contracts portal (the import changes rates only)"
                    : null;

                if (options.DryRun)
                {
                    report.LegalContracts.Add(new(number, npi, existing?.Id,
                        existing is null ? "WouldCreate" : termsChanged ? "WouldReplaceRates" : "Unchanged", detail));
                    continue;
                }

                var id = existing?.Id;
                if (existing is null)
                {
                    var (name, type) = providers.GetValueOrDefault(npi);
                    var created = await _legal.CreateAsync(tenantId, new LegalContractDto
                    {
                        ContractNumber = number,
                        ProviderNpi = npi,
                        ProviderName = string.IsNullOrWhiteSpace(name) ? npi : name,
                        ProviderType = FeeScheduleCsvReader.Normalize(type) is "organization" or "org" or "2" or "facility" or "group"
                            ? LegalProviderType.Organization : LegalProviderType.Individual,
                        LineOfBusiness = ParseLob(first.LineOfBusiness),
                        PlanIds = planIds,
                        PaymentMethodology = LegalPaymentMethodology.FeeForService,
                        NetworkStatus = first.NetworkStatus is NetworkStatus.OutOfNetwork or NetworkStatus.NonParticipating
                            ? LegalNetworkStatus.NonParticipating : LegalNetworkStatus.Participating,
                        EffectiveDate = first.EffectiveDate,
                        TerminationDate = first.TermDate,
                    }, ct);
                    id = created.Id!;
                }

                if (termsChanged) await _legal.ReplaceTermsAsync(tenantId, id!, terms, ct);
                if (existing is null || existing.Status is LegalContractStatus.Draft or LegalContractStatus.Suspended)
                    await _legal.ActivateAsync(tenantId, id!, ct);

                report.LegalContracts.Add(new(number, npi, id,
                    existing is null ? "Created" : termsChanged ? "RatesReplaced" : "Unchanged", detail));

                // Price from what the legal contract projects to: the contracts service's own derivation.
                var projection = await _legal.GetPricingProjectionAsync(tenantId, id!, ct);
                foreach (var engine in projection.Contracts.Where(c => !string.IsNullOrWhiteSpace(c.PlanId)))
                {
                    var stored = (await _contracts.ListByProviderAsync(tenantId, engine.ProviderNpi, ct))
                        .FirstOrDefault(c => c.Id == engine.Id);
                    var ours = stored is null
                        || stored.Origin?.StartsWith(OriginPrefix, StringComparison.Ordinal) == true
                        || (stored.Origin == LegalOrigin && stored.LegalContractId == id);
                    ContractImportOutcome outcome;
                    if (!ours && !options.ReplaceNonImported) outcome = ContractImportOutcome.Protected;
                    else if (stored is not null && Fingerprint(stored) == Fingerprint(engine)) outcome = ContractImportOutcome.Unchanged;
                    else
                    {
                        engine.CreatedDate = stored?.CreatedDate ?? DateTime.UtcNow;
                        engine.Revision = stored?.Revision ?? 0;
                        await _contracts.UpsertAsync(engine, ct);
                        outcome = stored is null ? ContractImportOutcome.Created : ContractImportOutcome.Replaced;
                    }
                    report.Outcomes[outcome] = report.Outcomes.GetValueOrDefault(outcome) + 1;
                    if (options.IncludeContracts || sample.Count < SampleSize) sample.Add(engine);
                }
                foreach (var w in projection.Warnings) report.Warnings.Add($"{number}: {w}");
            }
            catch (ProviderContractsCallException ex)
            {
                report.LegalContracts.Add(new(number, npi, null, "Failed", ex.Message));
            }
        }

        if (report.LegalContracts.Any(l => l.Outcome is "Rejected" or "Failed"))
            report.Warnings.Add("Some CHO contracts were not written; see legalContracts.");
    }

    private static LegalLineOfBusiness ParseLob(string? text) => FeeScheduleCsvReader.Normalize(text ?? string.Empty) switch
    {
        "medicaid" or "managedmedicaid" or "chip" => LegalLineOfBusiness.Medicaid,
        "medicare" or "medicareadvantage" or "ma" or "mapd" => LegalLineOfBusiness.Medicare,
        "exchange" or "marketplace" or "qhp" or "aca" => LegalLineOfBusiness.Exchange,
        "tricare" => LegalLineOfBusiness.TRICARE,
        "va" => LegalLineOfBusiness.VA,
        _ => LegalLineOfBusiness.Commercial,
    };

    private static string TermsFingerprint(IEnumerable<ReimbursementTerm> terms) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(terms.Select(t => new
        {
            t.Method, t.FeeScheduleId, t.BaseScheduleId, t.Percent, t.Multiplier, t.LesserOfBilled, t.EffectiveDate, t.TermDate,
            t.Scope.ProcedureCodeFrom, t.Scope.ProcedureCodeTo, t.Scope.ClaimType,
        }))));

    private async Task<ContractImportOutcome> WriteAsync(
        ProviderContract contract, ProviderContract? existing, ContractRosterImportOptions options, CancellationToken ct)
    {
        if (existing is not null)
        {
            var imported = existing.Origin?.StartsWith(OriginPrefix, StringComparison.Ordinal) == true;
            if (!imported && !options.ReplaceNonImported) return ContractImportOutcome.Protected;
            if (Fingerprint(existing) == Fingerprint(contract)) return ContractImportOutcome.Unchanged;
        }

        if (options.DryRun)
            return existing is null ? ContractImportOutcome.WouldCreate : ContractImportOutcome.WouldReplace;

        contract.CreatedDate = existing?.CreatedDate ?? DateTime.UtcNow;
        contract.Revision = existing?.Revision ?? 0;
        await _contracts.UpsertAsync(contract, ct);
        return existing is null ? ContractImportOutcome.Created : ContractImportOutcome.Replaced;
    }

    /// <summary>
    /// The plan id claims are priced with: a plan id as given, or for a plan code the id of its
    /// published version in effect today (what claims resolve a plan code to).
    /// </summary>
    private async Task<string?> ResolvePlanAsync(string planText, string tenantId)
    {
        if (Guid.TryParse(planText, out var guid))
            return await _plans.GetByIdAsync(guid.ToString("D"), tenantId) is { } p ? p.Id : null;
        return await _plans.GetByPlanIdAsync(planText, tenantId) is { } byCode ? byCode.Id : null;
    }

    internal static (ReimbursementTerm? Term, string? Error) BuildTerm(Func<string, string> r)
    {
        var methodText = FeeScheduleCsvReader.Normalize(r("method"));
        var feeSchedule = NullIfEmpty(r("feeSchedule"));
        var baseSchedule = NullIfEmpty(r("baseSchedule"));

        ReimbursementMethod method;
        switch (methodText)
        {
            case "" when feeSchedule is not null:
            case "feeschedule": case "feescheduleref": case "fs": case "fee": case "fixed": case "flat":
                method = ReimbursementMethod.FeeScheduleRef; break;
            case "percentofmedicare": case "pctmedicare": case "pom": case "medicare": case "percentmedicare":
                method = ReimbursementMethod.PercentOfMedicare; break;
            case "percentofbilled": case "pctbilled": case "pob": case "billed": case "percentofcharges": case "discountoffcharges":
                method = ReimbursementMethod.PercentOfBilled; break;
            case "caserate": method = ReimbursementMethod.CaseRate; break;
            case "perdiem": method = ReimbursementMethod.PerDiem; break;
            case "capitation": case "cap":
                return (null, "capitation terms need a capitation rate config; set them in the contracts portal, not this import");
            case "":
                return (null, "method is required (fee_schedule, percent_of_medicare, percent_of_billed, case_rate, per_diem)");
            default:
                return (null, $"method '{r("method")}' is not fee_schedule, percent_of_medicare, percent_of_billed, case_rate or per_diem");
        }

        var term = new ReimbursementTerm { Method = method, Description = NullIfEmpty(r("description")) };

        if (method is ReimbursementMethod.PercentOfMedicare or ReimbursementMethod.PercentOfBilled)
        {
            var pctText = r("percent").TrimEnd('%').Trim();
            if (!decimal.TryParse(pctText, NumberStyles.Number, CultureInfo.InvariantCulture, out var pct))
                return (null, "percent is required for a percent-of term (110 = 110%)");
            if (pct < 10m || pct > 1000m)
                return (null, $"percent {pctText} is read as a percentage (110 = 110%); values under 10 or over 1000 are refused");
            term.Percent = pct / 100m;
            if (method == ReimbursementMethod.PercentOfMedicare)
                term.BaseScheduleId = baseSchedule ?? feeSchedule;
        }
        else
        {
            term.FeeScheduleId = feeSchedule;
            var m = r("multiplier").TrimEnd('%').Trim();
            if (m.Length > 0)
            {
                if (!decimal.TryParse(m, NumberStyles.Number, CultureInfo.InvariantCulture, out var mult) || mult <= 0m)
                    return (null, "multiplier must be a positive number (1.0 = the schedule's rate)");
                term.Multiplier = r("multiplier").Contains('%') ? mult / 100m : mult;
            }
        }

        var from = NullIfEmpty(r("codeFrom")?.ToUpperInvariant());
        var to = NullIfEmpty(r("codeTo")?.ToUpperInvariant());
        term.Scope.ProcedureCodeFrom = from;
        term.Scope.ProcedureCodeTo = to ?? from;

        switch (FeeScheduleCsvReader.Normalize(r("claimType")))
        {
            case "": case "any": case "all": break;
            case "professional": case "prof": case "837p": case "cms1500": case "dental": case "837d":
                term.Scope.ClaimType = ContractClaimType.Professional; break;
            case "institutional": case "inst": case "837i": case "ub04": case "facility":
                term.Scope.ClaimType = ContractClaimType.Institutional; break;
            case "inpatient": case "institutionalinpatient": case "ip":
                term.Scope.ClaimType = ContractClaimType.InstitutionalInpatient; break;
            case "outpatient": case "institutionaloutpatient": case "op":
                term.Scope.ClaimType = ContractClaimType.InstitutionalOutpatient; break;
            default:
                return (null, $"claim_type '{r("claimType")}' is not professional, institutional, inpatient or outpatient");
        }

        var lesser = FeeScheduleCsvReader.Normalize(r("lesserOf"));
        if (lesser.Length > 0) term.LesserOfBilled = lesser is "y" or "yes" or "true" or "1";

        if (r("termEffective").Length > 0)
        {
            if (!TryDate(r("termEffective"), out var te)) return (null, "term_effective_date is unreadable");
            term.EffectiveDate = te;
        }
        if (r("termEnd").Length > 0)
        {
            if (!TryDate(r("termEnd"), out var tt)) return (null, "term_end_date is unreadable");
            term.TermDate = tt;
        }

        return (term, null);
    }

    private static async IAsyncEnumerable<(int Row, Func<string, string> Get)> ReadAsync(Stream csv, ContractRosterImportReport report)
    {
        using var reader = new StreamReader(csv, detectEncodingFromByteOrderMarks: true);
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = false,
            BadDataFound = null,
            MissingFieldFound = null,
            DetectColumnCountChanges = false,
        };
        using var parser = new CsvParser(reader, config);

        Dictionary<string, int>? index = null;
        while (await parser.ReadAsync())
        {
            var f = parser.Record;
            if (f is null || f.All(string.IsNullOrWhiteSpace)) continue;

            if (index is null)
            {
                var normalized = f.Select(FeeScheduleCsvReader.Normalize).ToArray();
                var found = new Dictionary<string, int>();
                foreach (var (role, aliases) in Aliases)
                {
                    foreach (var a in aliases)
                    {
                        var i = Array.IndexOf(normalized, a);
                        if (i >= 0 && !found.ContainsValue(i)) { found[role] = i; break; }
                    }
                }
                if (!found.ContainsKey("npi") || parser.Row > 30)
                {
                    if (parser.Row > 30)
                        throw new FeeScheduleFormatException("No header with an NPI column was found in the first 30 rows.");
                    continue;
                }
                foreach (var required in new[] { "plan", "effective", "network" })
                    if (!found.ContainsKey(required))
                        throw new FeeScheduleFormatException(
                            $"The header has no {required} column. Required: npi, plan, network_status, effective_date; see the importer guide.");
                index = found;
                report.Columns = found.ToDictionary(kv => kv.Key, kv => f[kv.Value].Trim());
                continue;
            }

            var fields = f;
            var map = index;
            yield return (parser.Row, role => map.TryGetValue(role, out var i) && i < fields.Length ? fields[i].Trim() : string.Empty);
        }

        if (index is null)
            throw new FeeScheduleFormatException("The file is empty or has no header with an NPI column.");
    }

    /// <summary>Content fingerprint for idempotent re-import (ignores ids, revision and timestamps).</summary>
    internal static string Fingerprint(ProviderContract c)
    {
        var shape = new
        {
            c.ProviderNpi, c.PlanId, c.EffectiveDate, c.TermDate, c.NetworkStatus, c.ContractNumber, c.LineOfBusiness, c.Origin,
            Terms = c.ReimbursementTerms.Select(t => new
            {
                t.Method, t.FeeScheduleId, t.BaseScheduleId, t.Percent, t.Multiplier, t.LesserOfBilled, t.EffectiveDate, t.TermDate,
                t.Scope.ProcedureCodeFrom, t.Scope.ProcedureCodeTo, t.Scope.ClaimType, t.Description,
            }),
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(shape)));
    }

    private static string TermId(string contractId, int position, ReimbursementTerm t)
    {
        var text = $"{contractId}|{position}|{t.Method}|{t.FeeScheduleId}|{t.BaseScheduleId}|{t.Percent}|{t.Scope.ProcedureCodeFrom}|{t.Scope.ProcedureCodeTo}";
        return "imp-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
    }

    /// <summary>10 digits with a valid Luhn check digit over the 80840 prefix (the NPI standard).</summary>
    internal static bool IsValidNpi(string npi)
    {
        if (npi.Length != 10 || !npi.All(char.IsDigit)) return false;
        var digits = "80840" + npi[..9];
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i] - '0';
            if (i % 2 == 0) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
        }
        return (10 - sum % 10) % 10 == npi[9] - '0';
    }

    private static bool TryNetwork(string text, out NetworkStatus status)
    {
        switch (FeeScheduleCsvReader.Normalize(text))
        {
            case "innetwork": case "in": case "inn": case "y": case "yes": case "contracted": case "network":
                status = NetworkStatus.InNetwork; return true;
            case "par": case "participating":
                status = NetworkStatus.Participating; return true;
            case "outofnetwork": case "out": case "oon": case "n": case "no":
                status = NetworkStatus.OutOfNetwork; return true;
            case "nonpar": case "nonparticipating":
                status = NetworkStatus.NonParticipating; return true;
            default:
                status = NetworkStatus.Unknown; return false;
        }
    }

    private static bool TryDate(string text, out DateTime date)
    {
        var t = text.Trim();
        var space = t.IndexOf(' ');
        if (space > 0) t = t[..space];
        if (DateTime.TryParseExact(t, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            date = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
            return true;
        }
        return false;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Slug(string s)
    {
        var slug = new string(s.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        return slug.Length == 0 ? "csv" : slug[..Math.Min(40, slug.Length)];
    }
}
