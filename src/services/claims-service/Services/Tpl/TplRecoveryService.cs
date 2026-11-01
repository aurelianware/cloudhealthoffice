using System.Globalization;
using System.Text;
using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Models.Tpl;
using ClaimsService.Repositories;
using ClaimsService.Services.Adjudication.Tpl;
using ClaimsService.Services.Resolution;
using Microsoft.Extensions.Options;

namespace ClaimsService.Services.Tpl;

public sealed record TplRecoverySyncResult(int ClaimsScanned, int CasesOpened, int AlreadyOpen, decimal AmountOpened, bool MoreToScan);

public sealed record TplRetroactiveResult(int MembersChecked, int ClaimsChecked, int CasesOpened, decimal AmountOpened, int LookupFailures);

public sealed record TplClockResult(int CasesChecked, int TransferredToState, int KeptUnderRepaymentPlan);

public sealed record TplRecoveryEventRequest(
    TplRecoveryEventType Type, decimal? Amount, DateTime? At, string? Reference, string? Note);

public interface ITplRecoveryService
{
    Task<TplRecoverySyncResult> SyncPayAndChaseAsync(string tenantId, DateTime from, DateTime to, int limit, string actor, CancellationToken ct = default);
    Task<TplRetroactiveResult> CheckRetroactiveAsync(string tenantId, IReadOnlyCollection<string> memberIds, DateTime asOf, string actor, CancellationToken ct = default);
    Task<TplRecoveryCase?> RecordEventAsync(string tenantId, string caseId, TplRecoveryEventRequest request, string actor, CancellationToken ct = default);
    Task<TplClockResult> ApplyClocksAsync(string tenantId, DateTime asOf, string actor, CancellationToken ct = default);
    Task<string> BuildStateReportCsvAsync(string tenantId, string state, DateTime month, CancellationToken ct = default);
}

/// <summary>
/// Medicaid TPL recovery cases. Opens a case for every paid claim whose
/// pay-and-chase lines Medicaid paid, and for paid claims that TPL found
/// later covers; records bills, payments, repayment plans and write-offs;
/// moves cases past the state's retention window to the state; and writes the
/// monthly state report.
/// </summary>
public sealed class TplRecoveryService : ITplRecoveryService
{
    private readonly IClaimRepository _claims;
    private readonly ITplRecoveryRepository _cases;
    private readonly ITplClient _tpl;
    private readonly ThirdPartyLiabilityOptions _options;
    private readonly ILogger<TplRecoveryService> _logger;

    public TplRecoveryService(
        IClaimRepository claims, ITplRecoveryRepository cases, ITplClient tpl,
        IOptions<ThirdPartyLiabilityOptions> options, ILogger<TplRecoveryService> logger)
    {
        _claims = claims;
        _cases = cases;
        _tpl = tpl;
        _options = options.Value;
        _logger = logger;
    }

    public static string CaseKey(string claimId, TplRecoveryOrigin origin) => $"{claimId}|{origin}";

    public async Task<TplRecoverySyncResult> SyncPayAndChaseAsync(
        string tenantId, DateTime from, DateTime to, int limit, string actor, CancellationToken ct = default)
    {
        var claims = await _claims.FindFinalizedMedicaidAsync(from, to, null, payAndChaseOnly: true, limit, ct).ConfigureAwait(false);
        var candidates = new List<TplRecoveryCase>();
        foreach (var claim in claims)
        {
            var record = claim.AdjudicationResult?.ThirdPartyLiability;
            if (record is null || record.PayAndChaseLines.Count == 0) continue;
            var lines = record.PayAndChaseLines
                .Select(p => (Line: claim.ClaimLines.FirstOrDefault(l => l.LineNumber == p.LineNumber), p.Reason))
                .Where(x => x.Line is not null)
                .Select(x => new TplRecoveryLine
                {
                    LineNumber = x.Line!.LineNumber,
                    ProcedureCode = x.Line.ProcedureCode,
                    PaidAmount = x.Line.AdjudicationResult?.PaidAmount ?? 0m,
                    Reason = x.Reason,
                })
                .Where(l => l.PaidAmount > 0m)
                .ToList();
            if (lines.Count == 0) continue;

            var state = record.Resources.Select(r => r.State).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? string.Empty;
            candidates.Add(NewCase(tenantId, claim, TplRecoveryOrigin.PayAndChase, state, lines,
                record.Resources.Select(r => new TplRecoveryResource
                {
                    TplPolicyId = r.TplPolicyId, CarrierName = r.CarrierName, CarrierCode = r.CarrierCode, PolicyNumber = r.PolicyNumber,
                }).ToList(), actor));
        }

        var (opened, already) = await InsertNewAsync(tenantId, candidates, ct).ConfigureAwait(false);
        return new TplRecoverySyncResult(claims.Count, opened.Count, already, opened.Sum(c => c.AmountPaid), claims.Count >= limit);
    }

    public async Task<TplRetroactiveResult> CheckRetroactiveAsync(
        string tenantId, IReadOnlyCollection<string> memberIds, DateTime asOf, string actor, CancellationToken ct = default)
    {
        var from = asOf.Date.AddMonths(-Math.Max(1, _options.RetroactiveLookbackMonths));
        var candidates = new List<TplRecoveryCase>();
        var claimsChecked = 0;
        var failures = 0;

        foreach (var memberId in memberIds.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.Ordinal))
        {
            var claims = await _claims.FindFinalizedMedicaidAsync(from, asOf.Date.AddDays(1), memberId, payAndChaseOnly: false, 1000, ct)
                .ConfigureAwait(false);
            foreach (var claim in claims)
            {
                // Claims the TPL stage already handled are not retroactive:
                // cost-avoided ones were not paid, pay-and-chase ones have their
                // own case, and other-payer-billed ones went through COB.
                var decided = claim.AdjudicationResult?.ThirdPartyLiability?.Decision;
                if (decided is "PayAndChase" or "CostAvoided" or "OtherPayerBilled") continue;
                claimsChecked++;

                var serviceDate = claim.ClaimLines.Where(l => l.ServiceDateFrom != default)
                    .Select(l => l.ServiceDateFrom).DefaultIfEmpty(claim.ServiceDateFrom).Min();
                var resources = await _tpl.GetActiveAsync(tenantId, memberId, serviceDate, ct).ConfigureAwait(false);
                if (resources is null) { failures++; continue; }
                var active = resources.Where(r => r.EffectiveDate.Date <= serviceDate.Date
                                                  && (r.EndDate is null || r.EndDate.Value.Date >= serviceDate.Date)).ToList();
                if (active.Count == 0) continue;

                var adapter = AdapterClaim.From(claim);
                var accident = TplServiceRules.IsAccidentRelated(adapter);
                var lines = new List<TplRecoveryLine>();
                var used = new Dictionary<string, TplResource>();
                foreach (var line in adapter.ClaimLines)
                {
                    var category = TplServiceRules.Categorise(adapter, line);
                    var covering = active.Where(r => TplServiceRules.Covers(r.Scope, category, accident)).ToList();
                    var paid = claim.ClaimLines.FirstOrDefault(l => l.LineNumber == line.LineNumber)?.AdjudicationResult?.PaidAmount ?? 0m;
                    if (covering.Count == 0 || paid <= 0m) continue;
                    foreach (var r in covering) used[r.Id] = r;
                    lines.Add(new TplRecoveryLine
                    {
                        LineNumber = line.LineNumber,
                        ProcedureCode = line.ProcedureCode,
                        PaidAmount = paid,
                        Reason = "Other coverage found after payment covered this service date: " +
                                 string.Join(", ", covering.Select(r => r.CarrierName).Distinct()) + ".",
                    });
                }
                if (lines.Count == 0) continue;

                var state = used.Values.Select(r => r.State).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? string.Empty;
                candidates.Add(NewCase(tenantId, claim, TplRecoveryOrigin.Retroactive, state, lines,
                    used.Values.Select(r => new TplRecoveryResource
                    {
                        TplPolicyId = r.Id, CarrierName = r.CarrierName, CarrierCode = r.CarrierCode, PolicyNumber = r.PolicyNumber,
                    }).ToList(), actor));
            }
        }

        var (opened, _) = await InsertNewAsync(tenantId, candidates, ct).ConfigureAwait(false);
        return new TplRetroactiveResult(memberIds.Count, claimsChecked, opened.Count, opened.Sum(c => c.AmountPaid), failures);
    }

    public async Task<TplRecoveryCase?> RecordEventAsync(
        string tenantId, string caseId, TplRecoveryEventRequest request, string actor, CancellationToken ct = default)
    {
        var c = await _cases.GetAsync(tenantId, caseId, ct).ConfigureAwait(false);
        if (c is null) return null;
        Apply(c, request, actor);
        await _cases.SaveAsync(c, ct).ConfigureAwait(false);
        return c;
    }

    /// <summary>Applies one event to a case. Pure, so the rules test without a store.</summary>
    public static void Apply(TplRecoveryCase c, TplRecoveryEventRequest request, string actor)
    {
        if (c.IsClosed && request.Type != TplRecoveryEventType.Note)
            throw new InvalidOperationException($"Case is {c.Status}; only notes can be added.");
        if (request.Type is TplRecoveryEventType.PaymentReceived or TplRecoveryEventType.WrittenOff
            && request.Amount is not > 0m)
            throw new ArgumentException("A payment or write-off needs a positive amount.");

        var at = (request.At ?? DateTime.UtcNow).ToUniversalTime();
        switch (request.Type)
        {
            case TplRecoveryEventType.Billed:
                if (c.Status == TplRecoveryStatus.Open) c.Status = TplRecoveryStatus.Billed;
                break;
            case TplRecoveryEventType.PaymentReceived:
                c.AmountRecovered += request.Amount!.Value;
                c.Status = c.AmountOutstanding == 0m ? TplRecoveryStatus.Recovered : TplRecoveryStatus.PartiallyRecovered;
                break;
            case TplRecoveryEventType.RepaymentPlanAgreed:
                c.RepaymentPlanAgreedAt ??= at;
                break;
            case TplRecoveryEventType.WrittenOff:
                c.AmountWrittenOff += request.Amount!.Value;
                if (c.AmountOutstanding == 0m)
                    c.Status = c.AmountRecovered > 0m ? TplRecoveryStatus.Recovered : TplRecoveryStatus.WrittenOff;
                break;
            case TplRecoveryEventType.TransferredToState:
                c.Status = TplRecoveryStatus.TransferredToState;
                c.TransferredToStateAt = at;
                break;
        }
        c.Events.Add(new TplRecoveryEvent
        {
            Type = request.Type, At = at, Amount = request.Amount, Reference = request.Reference, Note = request.Note, By = actor,
        });
        c.UpdatedAt = DateTime.UtcNow;
    }

    public async Task<TplClockResult> ApplyClocksAsync(string tenantId, DateTime asOf, string actor, CancellationToken ct = default)
    {
        var due = await _cases.ListAsync(tenantId, new TplRecoveryQuery(OpenOnly: true, DeadlineBefore: asOf.Date, Limit: 5000), ct)
            .ConfigureAwait(false);
        var transferred = 0;
        var kept = 0;
        foreach (var c in due)
        {
            if (c.HasActiveRepaymentPlan) { kept++; continue; }
            Apply(c, new TplRecoveryEventRequest(TplRecoveryEventType.TransferredToState, null,
                c.RetentionDeadline!.Value.AddDays(1), null,
                $"Plan's recovery window closed {c.RetentionDeadline:yyyy-MM-dd}; the state recovers from here."), actor);
            await _cases.SaveAsync(c, ct).ConfigureAwait(false);
            transferred++;
        }
        if (transferred > 0)
            _logger.LogInformation("TPL recovery clock: {Count} case(s) transferred to the state", transferred);
        return new TplClockResult(due.Count, transferred, kept);
    }

    /// <summary>
    /// Monthly state report: cases whose window closed in the month (handed
    /// to the state), and recoveries received in the month. CHO's own CSV;
    /// the state's file layout (DHCS asks for one by the 15th of each month
    /// over SFTP) is mapped from it once the plan has the spec.
    /// </summary>
    public async Task<string> BuildStateReportCsvAsync(string tenantId, string state, DateTime month, CancellationToken ct = default)
    {
        var start = new DateTime(month.Year, month.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddMonths(1);
        var cases = await _cases.ListAsync(tenantId, new TplRecoveryQuery(State: state, Limit: 100_000), ct).ConfigureAwait(false);

        var sb = new StringBuilder();
        sb.AppendLine("report_month,row_type,case_id,claim_number,member_id,origin,carrier,policy_number,service_date,payment_date,amount_paid,amount_recovered,amount_outstanding,status,retention_deadline,repayment_plan_agreed,event_date,event_amount");
        foreach (var c in cases)
        {
            var carrier = c.Resources.FirstOrDefault();
            string Row(string type, DateTime? eventDate, decimal? eventAmount) => string.Join(',',
                start.ToString("yyyy-MM", CultureInfo.InvariantCulture), type, c.Id, Csv(c.ClaimNumber), Csv(c.MemberId), c.Origin,
                Csv(carrier?.CarrierName), Csv(carrier?.PolicyNumber), D(c.ServiceDateFrom), D(c.PaymentDate),
                M(c.AmountPaid), M(c.AmountRecovered), M(c.AmountOutstanding), c.Status, D(c.RetentionDeadline),
                D(c.RepaymentPlanAgreedAt), D(eventDate), eventAmount is null ? string.Empty : M(eventAmount.Value));

            if (c.TransferredToStateAt is { } t && t >= start && t < end)
                sb.AppendLine(Row("transferred_to_state", t, c.AmountOutstanding));
            foreach (var e in c.Events.Where(e => e.Type == TplRecoveryEventType.PaymentReceived && e.At >= start && e.At < end))
                sb.AppendLine(Row("recovery_received", e.At, e.Amount));
        }
        return sb.ToString();

        static string D(DateTime? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
        static string M(decimal m) => m.ToString("0.00", CultureInfo.InvariantCulture);
        static string Csv(string? v) => v is null ? string.Empty
            : v.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    private TplRecoveryCase NewCase(
        string tenantId, Claim claim, TplRecoveryOrigin origin, string state,
        List<TplRecoveryLine> lines, List<TplRecoveryResource> resources, string actor)
    {
        var paymentDate = (claim.AdjudicationResult?.PaymentDate ?? claim.PaidDate ?? claim.AdjudicatedDate ?? DateTime.UtcNow).ToUniversalTime();
        var stateCode = state.ToUpperInvariant();
        DateTime? deadline = _options.RecoveryRetentionMonths.TryGetValue(stateCode, out var months) && months > 0
            ? paymentDate.Date.AddMonths(months)
            : null;
        var c = new TplRecoveryCase
        {
            TenantId = tenantId,
            CaseKey = CaseKey(claim.Id, origin),
            ClaimId = claim.Id,
            ClaimNumber = claim.ClaimNumber,
            MemberId = claim.MemberId,
            State = stateCode,
            Origin = origin,
            Lines = lines,
            Resources = resources,
            ServiceDateFrom = claim.ServiceDateFrom,
            PaymentDate = paymentDate,
            AmountPaid = lines.Sum(l => l.PaidAmount),
            RetentionDeadline = deadline,
        };
        c.Events.Add(new TplRecoveryEvent { Type = TplRecoveryEventType.Opened, By = actor, Amount = c.AmountPaid });
        return c;
    }

    private async Task<(List<TplRecoveryCase> Opened, int Already)> InsertNewAsync(
        string tenantId, List<TplRecoveryCase> candidates, CancellationToken ct)
    {
        var unique = candidates.GroupBy(c => c.CaseKey).Select(g => g.First()).ToList();
        var existing = await _cases.ExistingKeysAsync(tenantId, unique.Select(c => c.CaseKey).ToList(), ct).ConfigureAwait(false);
        var fresh = unique.Where(c => !existing.Contains(c.CaseKey)).ToList();
        await _cases.InsertManyAsync(fresh, ct).ConfigureAwait(false);
        return (fresh, unique.Count - fresh.Count);
    }
}
