using System.Globalization;
using System.Text;
using CoverageService.Models.Tpl;
using CoverageService.Repositories;

namespace CoverageService.Services.Tpl;

/// <summary>How long a plan has to report coverage changes to the state, per state.</summary>
public sealed class TplReportingOptions
{
    public const string SectionName = "Tpl:Reporting";

    /// <summary>Calendar days from discovery to the report. California: 10
    /// (APL 21-002). A state not listed has no clock; its rows are still
    /// listed for reporting.</summary>
    public Dictionary<string, int> ReportDueDays { get; set; } =
        new(StringComparer.OrdinalIgnoreCase) { ["CA"] = 10 };
}

public sealed record TplDiscoveryRequest(
    string State,
    string CarrierName,
    string? CarrierCode,
    string? PolicyNumber,
    string? GroupNumber,
    string? PolicyholderName,
    /// <summary>The state's coverage code (Medi-Cal OHC code) or scope names such as <c>Comprehensive</c>.</summary>
    string Coverage,
    DateTime EffectiveDate,
    DateTime? EndDate,
    DateTime? DiscoveredAt,
    string? HowFound);

public sealed class TplDiscoveryException(string message) : Exception(message);

public interface ITplDiscoveryService
{
    Task<TplPolicy> RecordDiscoveryAsync(string tenantId, string memberId, TplDiscoveryRequest request, string actor, CancellationToken ct = default);
    Task<TplPolicy?> RequestRemovalAsync(string tenantId, string policyId, string reason, DateTime? discoveredAt, string actor, CancellationToken ct = default);
    Task<string> BuildStateReportCsvAsync(string tenantId, string state, bool includeReported, DateTime asOf, CancellationToken ct = default);
    Task<int> MarkReportedAsync(string tenantId, IReadOnlyCollection<string> policyIds, DateTime reportedAt, string? reference, string actor, CancellationToken ct = default);
}

/// <summary>
/// Coverage the plan finds itself (a provider's EOB, a member call, a data
/// match) and wrong state records it wants removed. Medi-Cal plans must report
/// both to DHCS within 10 calendar days (APL 21-002, by the OHC Addition or
/// Removal form or batch). The row counts for cost avoidance from the moment
/// it is recorded; when a later state file carries the same policy the
/// importer adopts it and the pending report is dropped.
/// </summary>
public sealed class TplDiscoveryService : ITplDiscoveryService
{
    private readonly ITplPolicyRepository _policies;
    private readonly ICoverageRepository _coverage;
    private readonly TplReportingOptions _options;

    public TplDiscoveryService(
        ITplPolicyRepository policies, ICoverageRepository coverage,
        Microsoft.Extensions.Options.IOptions<TplReportingOptions> options)
    {
        _policies = policies;
        _coverage = coverage;
        _options = options.Value;
    }

    public async Task<TplPolicy> RecordDiscoveryAsync(
        string tenantId, string memberId, TplDiscoveryRequest request, string actor, CancellationToken ct = default)
    {
        var state = request.State?.Trim().ToUpperInvariant() ?? string.Empty;
        if (state.Length != 2) throw new TplDiscoveryException("state (two letters) is required.");
        if (string.IsNullOrWhiteSpace(request.CarrierName)) throw new TplDiscoveryException("carrierName is required.");
        if (request.EndDate is { } end && end.Date < request.EffectiveDate.Date)
            throw new TplDiscoveryException("endDate is before effectiveDate.");

        var known = await _coverage.GetKnownMemberIdsAsync(tenantId, [memberId], ct).ConfigureAwait(false);
        if (!known.Contains(memberId)) throw new TplDiscoveryException("Member is not enrolled with this plan.");

        var (scope, payAndChase, code) = ResolveCoverage(state, request.Coverage);
        var effective = DateTime.SpecifyKind(request.EffectiveDate.Date, DateTimeKind.Utc);
        var key = TplPolicy.BuildNaturalKey(memberId, state, request.CarrierCode, request.CarrierName, request.PolicyNumber, effective);
        var existing = (await _policies.GetByMembersAsync(tenantId, state, [memberId], ct).ConfigureAwait(false))
            .FirstOrDefault(p => p.NaturalKey == key);
        if (existing is not null)
            throw new TplDiscoveryException($"This policy is already on file (source {existing.Source}, id {existing.Id}).");

        var discovered = (request.DiscoveredAt ?? DateTime.UtcNow).ToUniversalTime();
        var policy = new TplPolicy
        {
            TenantId = tenantId,
            MemberId = memberId,
            State = state,
            Source = TplImportService.PlanDiscoveredSource,
            CarrierCode = Blank(request.CarrierCode),
            CarrierName = request.CarrierName.Trim(),
            PolicyNumber = Blank(request.PolicyNumber),
            GroupNumber = Blank(request.GroupNumber),
            PolicyholderName = Blank(request.PolicyholderName),
            StateCoverageCode = code,
            Scope = scope,
            PayAndChaseOnly = payAndChase,
            EffectiveDate = effective,
            EndDate = request.EndDate is { } e ? DateTime.SpecifyKind(e.Date, DateTimeKind.Utc) : null,
            Status = request.EndDate is { } past && past.Date < DateTime.UtcNow.Date ? TplPolicyStatus.Ended : TplPolicyStatus.Active,
            NaturalKey = key,
            DiscoveredAt = discovered,
            PendingStateAction = TplStateAction.Add,
            PendingStateActionReason = Blank(request.HowFound),
            StateReportDueBy = DueBy(state, discovered),
            LastUpdatedBy = actor,
        };
        await _policies.UpsertManyAsync([policy], ct).ConfigureAwait(false);
        return policy;
    }

    public async Task<TplPolicy?> RequestRemovalAsync(
        string tenantId, string policyId, string reason, DateTime? discoveredAt, string actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new TplDiscoveryException("reason is required.");
        var policy = await _policies.GetByIdAsync(tenantId, policyId, ct).ConfigureAwait(false);
        if (policy is null) return null;
        if (policy.Source == TplImportService.PlanDiscoveredSource && policy.ReportedToStateAt is null)
        {
            // Never reported: nothing for the state to remove. Withdraw it here.
            policy.Status = TplPolicyStatus.Removed;
            policy.EndReason = "Withdrawn by the plan before reporting: " + reason.Trim();
            policy.PendingStateAction = null;
            policy.StateReportDueBy = null;
        }
        else
        {
            // The state's record stays in force until the state's file drops
            // it; only the request is queued.
            var found = (discoveredAt ?? DateTime.UtcNow).ToUniversalTime();
            policy.PendingStateAction = TplStateAction.Remove;
            policy.PendingStateActionReason = reason.Trim();
            policy.DiscoveredAt = found;
            policy.StateReportDueBy = DueBy(policy.State, found);
            policy.ReportedToStateAt = null;
            policy.StateReportReference = null;
        }
        policy.LastUpdatedBy = actor;
        await _policies.UpsertManyAsync([policy], ct).ConfigureAwait(false);
        return policy;
    }

    /// <summary>
    /// CHO's CSV of additions and removal requests owed to the state, oldest
    /// due first, with an overdue flag. Map it to the state's form or batch
    /// layout (DHCS OHC Addition/Removal) once the plan has it.
    /// </summary>
    public async Task<string> BuildStateReportCsvAsync(
        string tenantId, string state, bool includeReported, DateTime asOf, CancellationToken ct = default)
    {
        var rows = await _policies.GetStateReportRowsAsync(tenantId, state.ToUpperInvariant(), includeReported, ct).ConfigureAwait(false);
        var sb = new StringBuilder();
        sb.AppendLine("action,member_id,carrier_code,carrier_name,policy_number,group_number,policyholder_name,coverage_code,scope,effective_date,end_date,discovered_at,due_by,overdue,reason,reported_at,reference,tpl_policy_id");
        foreach (var p in rows)
        {
            var overdue = p.PendingStateAction is not null && p.StateReportDueBy is { } due && due.Date < asOf.Date;
            sb.AppendLine(string.Join(',',
                p.PendingStateAction?.ToString() ?? "Reported",
                Csv(p.MemberId), Csv(p.CarrierCode), Csv(p.CarrierName), Csv(p.PolicyNumber), Csv(p.GroupNumber),
                Csv(p.PolicyholderName), Csv(p.StateCoverageCode), Csv(p.Scope.ToString()), D(p.EffectiveDate), D(p.EndDate),
                D(p.DiscoveredAt), D(p.StateReportDueBy), overdue ? "Y" : "N", Csv(p.PendingStateActionReason),
                D(p.ReportedToStateAt), Csv(p.StateReportReference), p.Id));
        }
        return sb.ToString();

        static string D(DateTime? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
        static string Csv(string? v) => v is null ? string.Empty
            : v.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    public async Task<int> MarkReportedAsync(
        string tenantId, IReadOnlyCollection<string> policyIds, DateTime reportedAt, string? reference, string actor, CancellationToken ct = default)
    {
        var updated = new List<TplPolicy>();
        foreach (var id in policyIds.Distinct(StringComparer.Ordinal))
        {
            var p = await _policies.GetByIdAsync(tenantId, id, ct).ConfigureAwait(false);
            if (p?.PendingStateAction is null) continue;
            p.PendingStateAction = null;
            p.ReportedToStateAt = reportedAt.ToUniversalTime();
            p.StateReportReference = Blank(reference);
            p.LastUpdatedBy = actor;
            updated.Add(p);
        }
        await _policies.UpsertManyAsync(updated, ct).ConfigureAwait(false);
        return updated.Count;
    }

    private DateTime? DueBy(string state, DateTime discovered) =>
        _options.ReportDueDays.TryGetValue(state, out var days) && days > 0 ? discovered.Date.AddDays(days) : null;

    /// <summary>The state's own code (via its built-in layout) or scope names.</summary>
    private static (TplScope Scope, bool PayAndChase, string? Code) ResolveCoverage(string state, string coverage)
    {
        var text = coverage?.Trim() ?? string.Empty;
        if (text.Length == 0) throw new TplDiscoveryException("coverage (a state coverage code or scope names) is required.");
        var layout = TplLayouts.BuiltIn.Values.FirstOrDefault(l => string.Equals(l.State, state, StringComparison.OrdinalIgnoreCase));
        if (layout is not null)
        {
            var codes = new Dictionary<string, TplCodeMapping>(layout.Codes, StringComparer.OrdinalIgnoreCase);
            if (codes.TryGetValue(text, out var mapping))
            {
                if (mapping.Skip || mapping.MeansNoCoverage || mapping.Scope == TplScope.None)
                    throw new TplDiscoveryException($"Code '{text}' is not a coverage source ({mapping.Description}).");
                return (mapping.Scope, mapping.PayAndChaseOnly, text.ToUpperInvariant());
            }
        }
        if (TplFileParser.TryParseScope(text, out var scope)) return (scope, false, null);
        throw new TplDiscoveryException($"Coverage '{text}' is neither a {state} coverage code nor scope names.");
    }

    private static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
