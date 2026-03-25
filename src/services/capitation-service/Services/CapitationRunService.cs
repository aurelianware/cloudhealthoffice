using System.Text.Json;
using CapitationService.Models;
using CapitationService.Repositories;

namespace CapitationService.Services;

public interface ICapitationRunService
{
    Task<CapitationRun> CreateRunAsync(CreateCapitationRunRequest request, string? createdBy);
    Task<CapitationRun> ExecuteRunAsync(string runId);
    Task<CapitationRun> GetRunAsync(string runId);
    Task<IEnumerable<CapitationRun>> GetRunsAsync(DateTime? from, DateTime? to, LineOfBusiness? lineOfBusiness = null);
    Task CancelRunAsync(string runId);
    Task<CapitationStatement> ApproveStatementAsync(string statementId);
    Task<CapitationStatement> VoidStatementAsync(string statementId, string reason);
    Task<CapitationStatement> HoldStatementAsync(string statementId, string reason);
    Task<CapitationPeriodSummary> GetCapitationSummaryAsync(DateTime period);
}

public class CapitationRunService : ICapitationRunService
{
    private readonly ICapitationRunRepository _runRepository;
    private readonly ICapitationContractRepository _contractRepository;
    private readonly ICapitationStatementRepository _statementRepository;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CapitationRunService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public CapitationRunService(
        ICapitationRunRepository runRepository,
        ICapitationContractRepository contractRepository,
        ICapitationStatementRepository statementRepository,
        IHttpClientFactory httpClientFactory,
        ILogger<CapitationRunService> logger)
    {
        _runRepository = runRepository;
        _contractRepository = contractRepository;
        _statementRepository = statementRepository;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Maps LineOfBusiness enum values to 3-character abbreviations for RunNumber formatting.
    /// </summary>
    private static readonly Dictionary<LineOfBusiness, string> LobAbbreviations = new()
    {
        [LineOfBusiness.Commercial] = "COM",
        [LineOfBusiness.Medicare] = "MCR",
        [LineOfBusiness.Medicaid] = "MCD",
        [LineOfBusiness.Exchange] = "EXC",
        [LineOfBusiness.TRICARE] = "TRI",
        [LineOfBusiness.VA] = "VA"
    };

    public async Task<CapitationRun> CreateRunAsync(CreateCapitationRunRequest request, string? createdBy)
    {
        // Validate run type + criteria consistency
        ValidateRunCriteria(request.RunType, request.Criteria);

        // Normalize capitation period to first of month
        var period = new DateTime(request.CapitationPeriod.Year, request.CapitationPeriod.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var lobAbbrev = LobAbbreviations.GetValueOrDefault(request.Criteria.LineOfBusiness, "UNK");

        var run = new CapitationRun
        {
            RunNumber = $"CAPRUN-{lobAbbrev}-{period:yyyy-MM}-{Guid.NewGuid().ToString()[..4].ToUpperInvariant()}",
            RunType = request.RunType,
            LineOfBusiness = request.Criteria.LineOfBusiness,
            CapitationPeriod = period,
            Description = request.Description ?? GenerateDefaultDescription(request.RunType, request.Criteria, period),
            Criteria = request.Criteria,
            CreatedBy = createdBy,
            Status = CapitationRunStatus.Pending
        };

        return await _runRepository.CreateAsync(run);
    }

    private static string GenerateDefaultDescription(CapitationRunType runType, CapitationRunCriteria criteria, DateTime period)
    {
        var lob = criteria.LineOfBusiness.ToString();
        return runType switch
        {
            CapitationRunType.Monthly => $"Monthly {lob} capitation for {period:MMMM yyyy}",
            CapitationRunType.AdHocProvider => $"Ad-hoc {lob} capitation for provider {criteria.ProviderNPI}, {period:MMMM yyyy}",
            CapitationRunType.RetroAdjustment => $"Retro adjustment — {lob} {period:MMMM yyyy}",
            CapitationRunType.WithholdRelease => $"Withhold release — {lob} {period:MMMM yyyy}",
            _ => $"Capitation run for {period:MMMM yyyy}"
        };
    }

    private static void ValidateRunCriteria(CapitationRunType runType, CapitationRunCriteria criteria)
    {
        // LineOfBusiness is always required — capitation runs are scoped to a single LOB
        if (!Enum.IsDefined(typeof(LineOfBusiness), criteria.LineOfBusiness))
            throw new ArgumentException("A valid LineOfBusiness is required for all capitation runs");

        switch (runType)
        {
            case CapitationRunType.Monthly:
                if (!string.IsNullOrEmpty(criteria.ProviderNPI))
                    throw new ArgumentException("Monthly runs process all providers in the LOB; ProviderNPI must not be set");
                if (criteria.OriginalPeriod.HasValue)
                    throw new ArgumentException("OriginalPeriod is only valid for RetroAdjustment runs");
                break;

            case CapitationRunType.AdHocProvider:
                if (string.IsNullOrEmpty(criteria.ProviderNPI))
                    throw new ArgumentException("AdHocProvider runs require a ProviderNPI");
                if (criteria.OriginalPeriod.HasValue)
                    throw new ArgumentException("OriginalPeriod is only valid for RetroAdjustment runs");
                break;

            case CapitationRunType.RetroAdjustment:
                if (criteria.OriginalPeriod == null)
                    throw new ArgumentException("RetroAdjustment runs require an OriginalPeriod");
                break;

            case CapitationRunType.WithholdRelease:
                if (criteria.OriginalPeriod.HasValue)
                    throw new ArgumentException("OriginalPeriod is only valid for RetroAdjustment runs");
                break;

            default:
                throw new ArgumentException($"Unknown run type: {runType}");
        }
    }

    public async Task<CapitationRun> ExecuteRunAsync(string runId)
    {
        var run = await _runRepository.GetByIdAsync(runId)
            ?? throw new InvalidOperationException($"Capitation run {runId} not found");

        if (run.Status != CapitationRunStatus.Pending)
            throw new InvalidOperationException($"Capitation run is in {run.Status} state, expected Pending");

        // 1. Mark as running
        run.Status = CapitationRunStatus.Running;
        run.ExecutionStartedAt = DateTime.UtcNow;
        await _runRepository.UpdateAsync(run);

        var periodStart = run.CapitationPeriod;
        var periodEnd = periodStart.AddMonths(1).AddDays(-1);
        var daysInMonth = DateTime.DaysInMonth(periodStart.Year, periodStart.Month);

        try
        {
            // 2. Fetch active capitation contracts filtered by run criteria
            var contracts = (await _contractRepository.GetActiveContractsAsync(
                run.Criteria.LineOfBusiness, run.Criteria.ContractType)).ToList();

            // Apply single-provider filter for AdHocProvider, or optional provider scoping
            if (!string.IsNullOrEmpty(run.Criteria.ProviderNPI))
                contracts = contracts.Where(c => c.ProviderNPI == run.Criteria.ProviderNPI).ToList();

            // Filter by ProviderNPIs list when set (multi-provider scoping)
            if (run.Criteria.ProviderNPIs?.Count > 0)
                contracts = contracts.Where(c => run.Criteria.ProviderNPIs.Contains(c.ProviderNPI)).ToList();

            // Filter by PlanIds when set — include only contracts that cover at least one matching plan
            if (run.Criteria.PlanIds?.Count > 0)
            {
                var planIdsSet = new HashSet<string>(run.Criteria.PlanIds);
                contracts = contracts.Where(c => c.PlanIds.Any(p => planIdsSet.Contains(p))).ToList();
            }

            if (contracts.Count == 0)
            {
                run.Warnings.Add($"No active capitation contracts found for {run.Criteria.LineOfBusiness}" +
                    (!string.IsNullOrEmpty(run.Criteria.ProviderNPI) ? $", provider {run.Criteria.ProviderNPI}" : "") +
                    (run.Criteria.ContractType.HasValue ? $", contract type {run.Criteria.ContractType}" : ""));
            }

            _logger.LogInformation("Found {Count} active capitation contracts for run {RunNumber}",
                contracts.Count, run.RunNumber);

            decimal totalGross = 0;
            decimal totalWithholds = 0;
            decimal totalAdjustments = 0;
            int totalMemberMonths = 0;
            int totalProviders = 0;

            // 3. For each contract, generate a statement
            foreach (var contract in contracts)
            {
                try
                {
                    var statement = await GenerateStatementForContractAsync(
                        contract, periodStart, periodEnd, daysInMonth, run.Id);

                    run.StatementIds.Add(statement.Id);
                    totalGross += statement.GrossCapitation;
                    totalWithholds += statement.WithholdAmount;
                    totalAdjustments += statement.TotalAdjustments;
                    totalMemberMonths += statement.MemberMonths;
                    totalProviders++;
                }
                catch (Exception ex)
                {
                    var msg = $"Failed to generate statement for provider {contract.ProviderNPI} ({contract.ContractNumber}): {ex.Message}";
                    run.Warnings.Add(msg);
                    _logger.LogWarning(ex, "Failed to generate statement for provider {NPI}, contract {ContractNumber}",
                        contract.ProviderNPI, contract.ContractNumber);
                }
            }

            // 4. Finalize run
            run.TotalStatements = run.StatementIds.Count;
            run.TotalMemberMonths = totalMemberMonths;
            run.TotalGrossCapitation = totalGross;
            run.TotalWithholds = totalWithholds;
            run.TotalAdjustments = totalAdjustments;
            run.TotalNetPayable = totalGross - totalWithholds + totalAdjustments;
            run.TotalProviders = totalProviders;
            run.Status = CapitationRunStatus.Completed;
            run.ExecutionCompletedAt = DateTime.UtcNow;
            run.ExecutionDurationSeconds = (run.ExecutionCompletedAt.Value - run.ExecutionStartedAt!.Value).TotalSeconds;

            _logger.LogInformation(
                "Capitation run {RunNumber} completed: {StatementCount} statements, {MemberMonths} member-months, " +
                "${GrossCapitation:N2} gross, ${NetPayable:N2} net, {ProviderCount} providers",
                run.RunNumber, run.TotalStatements, run.TotalMemberMonths,
                run.TotalGrossCapitation, run.TotalNetPayable, run.TotalProviders);
        }
        catch (Exception ex)
        {
            run.Status = CapitationRunStatus.Failed;
            run.Errors.Add(ex.Message);
            run.ExecutionCompletedAt = DateTime.UtcNow;
            _logger.LogError(ex, "Capitation run {RunNumber} failed", run.RunNumber);
        }

        return await _runRepository.UpdateAsync(run);
    }

    public async Task<CapitationRun> GetRunAsync(string runId)
    {
        return await _runRepository.GetByIdAsync(runId)
            ?? throw new InvalidOperationException($"Capitation run {runId} not found");
    }

    public async Task<IEnumerable<CapitationRun>> GetRunsAsync(DateTime? from, DateTime? to, LineOfBusiness? lineOfBusiness = null)
    {
        return await _runRepository.SearchAsync(from, to, lineOfBusiness: lineOfBusiness);
    }

    public async Task CancelRunAsync(string runId)
    {
        var run = await _runRepository.GetByIdAsync(runId)
            ?? throw new InvalidOperationException($"Capitation run {runId} not found");

        if (run.Status != CapitationRunStatus.Pending)
            throw new InvalidOperationException($"Can only cancel runs in Pending state, current: {run.Status}");

        run.Status = CapitationRunStatus.Cancelled;
        await _runRepository.UpdateAsync(run);
    }

    public async Task<CapitationStatement> ApproveStatementAsync(string statementId)
    {
        var statement = await _statementRepository.GetByIdAsync(statementId)
            ?? throw new InvalidOperationException($"Statement {statementId} not found");

        if (statement.Status != CapitationStatementStatus.Generated && statement.Status != CapitationStatementStatus.OnHold)
            throw new InvalidOperationException($"Can only approve statements in Generated or OnHold state, current: {statement.Status}");

        statement.Status = CapitationStatementStatus.Approved;
        _logger.LogInformation("Approved capitation statement {StatementNumber}", statement.StatementNumber);

        return await _statementRepository.UpdateAsync(statement);
    }

    public async Task<CapitationStatement> VoidStatementAsync(string statementId, string reason)
    {
        var statement = await _statementRepository.GetByIdAsync(statementId)
            ?? throw new InvalidOperationException($"Statement {statementId} not found");

        if (statement.Status == CapitationStatementStatus.Paid)
            throw new InvalidOperationException("Cannot void a paid statement");

        statement.Status = CapitationStatementStatus.Voided;
        statement.Adjustments.Add(new CapitationAdjustment
        {
            Type = CapitationAdjustmentType.Other,
            Description = $"Statement voided: {reason}",
            Amount = 0,
            AdjustmentDate = DateTime.UtcNow
        });

        _logger.LogInformation("Voided capitation statement {StatementNumber}: {Reason}",
            statement.StatementNumber, SanitizeForLog(reason));

        return await _statementRepository.UpdateAsync(statement);
    }

    public async Task<CapitationStatement> HoldStatementAsync(string statementId, string reason)
    {
        var statement = await _statementRepository.GetByIdAsync(statementId)
            ?? throw new InvalidOperationException($"Statement {statementId} not found");

        if (statement.Status != CapitationStatementStatus.Generated && statement.Status != CapitationStatementStatus.Approved)
            throw new InvalidOperationException($"Can only hold statements in Generated or Approved state, current: {statement.Status}");

        statement.Status = CapitationStatementStatus.OnHold;
        statement.Adjustments.Add(new CapitationAdjustment
        {
            Type = CapitationAdjustmentType.Other,
            Description = $"Statement held: {reason}",
            Amount = 0,
            AdjustmentDate = DateTime.UtcNow
        });

        _logger.LogInformation("Held capitation statement {StatementNumber}: {Reason}",
            statement.StatementNumber, SanitizeForLog(reason));

        return await _statementRepository.UpdateAsync(statement);
    }

    public async Task<CapitationPeriodSummary> GetCapitationSummaryAsync(DateTime period)
    {
        var normalizedPeriod = new DateTime(period.Year, period.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd = normalizedPeriod.AddMonths(1).AddDays(-1);

        var statements = (await _statementRepository.GetByProviderNpiAsync(
            npi: null!, periodFrom: normalizedPeriod, periodTo: periodEnd)).ToList();

        // If GetByProviderNpiAsync requires a non-null NPI, fall back to fetching runs for the period
        // and aggregating from run totals. For now, build summary from contract-level data.
        var contracts = (await _contractRepository.GetActiveContractsAsync()).ToList();

        var summary = new CapitationPeriodSummary
        {
            Period = normalizedPeriod,
            TotalProviders = statements.Select(s => s.ProviderNPI).Distinct().Count(),
            TotalMemberMonths = statements.Sum(s => s.MemberMonths),
            TotalGrossCapitation = statements.Sum(s => s.GrossCapitation),
            TotalWithholds = statements.Sum(s => s.WithholdAmount),
            TotalNetPayable = statements.Sum(s => s.NetPayable)
        };

        // Build LOB breakdown from contracts that have statements
        var contractsById = contracts.ToDictionary(c => c.Id);
        foreach (var statement in statements)
        {
            if (contractsById.TryGetValue(statement.ContractId, out var contract))
            {
                var lobKey = contract.LineOfBusiness.ToString();
                if (!summary.ByLineOfBusiness.ContainsKey(lobKey))
                    summary.ByLineOfBusiness[lobKey] = 0m;
                summary.ByLineOfBusiness[lobKey] += statement.NetPayable;

                var typeKey = contract.ContractType.ToString();
                if (!summary.ByContractType.ContainsKey(typeKey))
                    summary.ByContractType[typeKey] = 0m;
                summary.ByContractType[typeKey] += statement.NetPayable;
            }
        }

        return summary;
    }

    // --- Private helper methods ---

    private async Task<CapitationStatement> GenerateStatementForContractAsync(
        CapitationContract contract, DateTime periodStart, DateTime periodEnd, int daysInMonth, string runId)
    {
        // Fetch members assigned to this PCP from coverage-service
        var coverages = await FetchCoveragesByPcpAsync(contract.ProviderNPI);

        // Filter to plan IDs covered by this contract (if contract specifies plans)
        if (contract.PlanIds.Count > 0)
            coverages = coverages.Where(c => contract.PlanIds.Contains(c.PlanId ?? string.Empty)).ToList();

        var statement = new CapitationStatement
        {
            StatementNumber = $"CAPSTMT-{contract.ProviderNPI}-{periodStart:yyyy-MM}",
            CapitationRunId = runId,
            ContractId = contract.Id,
            ContractNumber = contract.ContractNumber,
            ProviderNPI = contract.ProviderNPI,
            ProviderName = contract.ProviderName,
            CapitationPeriodStart = periodStart,
            CapitationPeriodEnd = periodEnd,
            CreatedBy = "capitation-run"
        };

        foreach (var coverage in coverages)
        {
            // Skip coverages not active during capitation period
            if (coverage.EffectiveDate > periodEnd)
                continue;
            if (coverage.TerminationDate.HasValue && coverage.TerminationDate.Value < periodStart)
                continue;

            // Calculate member age and resolve rate tier
            var memberAge = CalculateMemberAge(coverage.DateOfBirth, periodStart);
            var ageSexCategory = ResolveAgeSexCategory(memberAge, coverage.Gender);
            var rateTier = FindRateTier(contract.RateTiers, memberAge, coverage.Gender, ageSexCategory);
            var basePmpm = rateTier?.BasePMPM ?? 0m;

            // Fetch risk score (or use contract default)
            var riskScore = contract.RiskAdjusted
                ? await FetchRiskScoreAsync(coverage.MemberId, periodStart.Year)
                : contract.DefaultRiskScore;

            if (riskScore <= 0)
                riskScore = contract.DefaultRiskScore;

            var adjustedPmpm = Math.Round(basePmpm * riskScore, 2);

            // Calculate proration for mid-month adds/terms
            var coverageStart = coverage.EffectiveDate > periodStart ? coverage.EffectiveDate : periodStart;
            var coverageEnd = coverage.TerminationDate.HasValue && coverage.TerminationDate.Value < periodEnd
                ? coverage.TerminationDate.Value
                : periodEnd;

            var coveredDays = (coverageEnd - coverageStart).Days + 1;
            var prorationFactor = coveredDays >= daysInMonth ? 1.0m : (decimal)coveredDays / daysInMonth;
            prorationFactor = Math.Round(prorationFactor, 4);

            var grossAmount = Math.Round(adjustedPmpm * prorationFactor, 2);
            var withholdAmount = Math.Round(grossAmount * contract.WithholdPercentage, 2);
            var netAmount = grossAmount - withholdAmount;

            var lineItem = new CapitationLineItem
            {
                MemberId = coverage.MemberId,
                MemberName = coverage.MemberName ?? coverage.MemberId,
                CoverageId = coverage.CoverageId,
                PlanId = coverage.PlanId,
                AgeSexCategory = ageSexCategory,
                MemberAge = memberAge,
                Gender = coverage.Gender,
                BasePMPM = basePmpm,
                RiskScore = riskScore,
                AdjustedPMPM = adjustedPmpm,
                ProrationFactor = prorationFactor,
                GrossAmount = grossAmount,
                WithholdAmount = withholdAmount,
                NetAmount = netAmount,
                AssignmentEffectiveDate = coverage.PcpAssignmentDate ?? coverage.EffectiveDate,
                AssignmentTermDate = coverage.TerminationDate,
                IsRetroactive = coverage.EffectiveDate < periodStart && coverage.EffectiveDate.Month != periodStart.Month,
                AdjustmentReason = prorationFactor < 1.0m
                    ? $"Prorated: {coveredDays}/{daysInMonth} days"
                    : null
            };

            statement.LineItems.Add(lineItem);
        }

        // Process retroactive adjustments: members whose PCP changed (PreviousPcpNpi populated)
        foreach (var coverage in coverages.Where(c =>
            !string.IsNullOrEmpty(c.PreviousPcpNpi) &&
            c.PreviousPcpNpi == contract.ProviderNPI &&
            c.PcpNpi != contract.ProviderNPI))
        {
            statement.Adjustments.Add(new CapitationAdjustment
            {
                Type = CapitationAdjustmentType.RetroDisenrollment,
                Description = $"Member {coverage.MemberId} reassigned from {contract.ProviderNPI} to {coverage.PcpNpi}",
                Amount = 0m, // Amount would be calculated from prior period statement; placeholder for reconciliation
                RelatedMemberId = coverage.MemberId,
                RelatedPeriod = periodStart,
                AdjustmentDate = DateTime.UtcNow
            });
        }

        statement.RecalculateTotals();

        return await _statementRepository.CreateAsync(statement);
    }

    private static int CalculateMemberAge(DateTime dateOfBirth, DateTime asOfDate)
    {
        var age = asOfDate.Year - dateOfBirth.Year;
        if (dateOfBirth.Date > asOfDate.AddYears(-age))
            age--;
        return Math.Max(0, age);
    }

    private static AgeSexCategory ResolveAgeSexCategory(int age, string? gender)
    {
        var isMale = string.Equals(gender, "M", StringComparison.OrdinalIgnoreCase);
        var isFemale = string.Equals(gender, "F", StringComparison.OrdinalIgnoreCase);

        return age switch
        {
            <= 1 => AgeSexCategory.Infant_0_1,
            <= 11 => AgeSexCategory.Child_2_11,
            <= 17 => AgeSexCategory.Adolescent_12_17,
            <= 34 when isMale => AgeSexCategory.AdultMale_18_34,
            <= 34 when isFemale => AgeSexCategory.AdultFemale_18_34,
            <= 34 => AgeSexCategory.AdultMale_18_34, // Default unknown gender to male tiers
            <= 44 when isMale => AgeSexCategory.AdultMale_35_44,
            <= 44 when isFemale => AgeSexCategory.AdultFemale_35_44,
            <= 44 => AgeSexCategory.AdultMale_35_44,
            <= 54 when isMale => AgeSexCategory.AdultMale_45_54,
            <= 54 when isFemale => AgeSexCategory.AdultFemale_45_54,
            <= 54 => AgeSexCategory.AdultMale_45_54,
            <= 64 when isMale => AgeSexCategory.AdultMale_55_64,
            <= 64 when isFemale => AgeSexCategory.AdultFemale_55_64,
            <= 64 => AgeSexCategory.AdultMale_55_64,
            _ => AgeSexCategory.Senior_65Plus
        };
    }

    private static CapitationRateTier? FindRateTier(
        List<CapitationRateTier> tiers, int age, string? gender, AgeSexCategory? category)
    {
        // First try exact age-sex category match
        if (category.HasValue)
        {
            var tierByCategory = tiers.FirstOrDefault(t => t.AgeSexCategory == category);
            if (tierByCategory != null)
                return tierByCategory;
        }

        // Fall back to age range + gender match
        var tierByRange = tiers.FirstOrDefault(t =>
            age >= t.AgeFrom && age <= t.AgeTo &&
            (string.IsNullOrEmpty(t.Gender) || string.Equals(t.Gender, gender, StringComparison.OrdinalIgnoreCase)));

        if (tierByRange != null)
            return tierByRange;

        // Fall back to age range only (ignore gender)
        return tiers.FirstOrDefault(t => age >= t.AgeFrom && age <= t.AgeTo);
    }

    private async Task<List<CapitationCoverageDto>> FetchCoveragesByPcpAsync(string providerNpi)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("CoverageService");
            var response = await client.GetAsync($"/api/v1/coverage/by-pcp/{providerNpi}?status=Active");
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadFromJsonAsync<List<CapitationCoverageDto>>(JsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch coverages for PCP {NPI}", providerNpi);
            return new List<CapitationCoverageDto>();
        }
    }

    private async Task<decimal> FetchRiskScoreAsync(string memberId, int year)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("RiskAdjustmentService");
            var response = await client.GetAsync($"/api/risk-adjustment/members/{memberId}/scores/{year}");

            if (!response.IsSuccessStatusCode)
                return 1.0m;

            var scoreDto = await response.Content.ReadFromJsonAsync<RiskScoreDto>(JsonOptions);
            return scoreDto?.RiskScore ?? 1.0m;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Risk score not available for member {MemberId}, year {Year}, using default",
                memberId, year);
            return 1.0m;
        }
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

// --- DTOs ---

/// <summary>
/// DTO for coverage data fetched from coverage-service for capitation calculations.
/// Extends the base coverage fields with PCP assignment and demographic data.
/// </summary>
public class CapitationCoverageDto
{
    public string CoverageId { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string? MemberName { get; set; }
    public string? PlanId { get; set; }
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }

    /// <summary>
    /// Member date of birth (for age-sex tier calculation)
    /// </summary>
    public DateTime DateOfBirth { get; set; }

    /// <summary>
    /// Member gender (M/F/U)
    /// </summary>
    public string? Gender { get; set; }

    /// <summary>
    /// NPI of the assigned PCP
    /// </summary>
    public string? PcpNpi { get; set; }

    /// <summary>
    /// Date the PCP was assigned
    /// </summary>
    public DateTime? PcpAssignmentDate { get; set; }

    /// <summary>
    /// Previous PCP NPI for retro cap adjustments
    /// </summary>
    public string? PreviousPcpNpi { get; set; }
}

/// <summary>
/// DTO for risk score data fetched from risk-adjustment-service
/// </summary>
public class RiskScoreDto
{
    public string MemberId { get; set; } = string.Empty;
    public int Year { get; set; }
    public decimal RiskScore { get; set; } = 1.0m;
    public string? Model { get; set; }
}

/// <summary>
/// Summary of capitation activity for a specific period.
/// Returned by GetCapitationSummaryAsync.
/// </summary>
public class CapitationPeriodSummary
{
    /// <summary>
    /// Capitation period (first of month)
    /// </summary>
    public DateTime Period { get; set; }

    /// <summary>
    /// Number of distinct providers with statements
    /// </summary>
    public int TotalProviders { get; set; }

    /// <summary>
    /// Total member-months across all statements
    /// </summary>
    public int TotalMemberMonths { get; set; }

    /// <summary>
    /// Total gross capitation before withholds
    /// </summary>
    public decimal TotalGrossCapitation { get; set; }

    /// <summary>
    /// Total quality withholds
    /// </summary>
    public decimal TotalWithholds { get; set; }

    /// <summary>
    /// Total net payable to providers
    /// </summary>
    public decimal TotalNetPayable { get; set; }

    /// <summary>
    /// Breakdown of net payable by line of business
    /// </summary>
    public Dictionary<string, decimal> ByLineOfBusiness { get; set; } = new();

    /// <summary>
    /// Breakdown of net payable by contract type
    /// </summary>
    public Dictionary<string, decimal> ByContractType { get; set; } = new();
}
