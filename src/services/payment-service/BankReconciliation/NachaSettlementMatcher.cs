using CloudHealthOffice.BankReconciliation;
using CloudHealthOffice.BankReconciliation.Bai2;
using Microsoft.Extensions.Options;
using PaymentService.Models;

namespace PaymentService.BankReconciliation;

/// <summary>Configuration section <c>BankReconciliation:Nacha</c>.</summary>
public sealed class NachaSettlementOptions
{
    public const string SectionName = "BankReconciliation:Nacha";

    /// <summary>Debits that settle a credit file CHO sent (provider payments).</summary>
    public List<string> SettlementDebitTypeCodes { get; set; } = [.. Bai2TypeCodes.DefaultAchSettlementDebits];

    /// <summary>Credits that settle a debit file CHO sent.</summary>
    public List<string> SettlementCreditTypeCodes { get; set; } = [.. Bai2TypeCodes.DefaultAchSettlementCredits];

    /// <summary>ACH return type codes: always flagged for a person.</summary>
    public List<string> AchReturnTypeCodes { get; set; } = [.. Bai2TypeCodes.DefaultAchReturns];

    /// <summary>How many days after the effective entry date the bank may report the settlement.</summary>
    public int SettlementWindowDays { get; set; } = 3;

    /// <summary>How far back a return looks for the file it may belong to.</summary>
    public int ReturnLookbackDays { get; set; } = 60;

    /// <summary>
    /// The ACH company identification(s) CHO originates under (default:
    /// Era:OriginatingCompanyId). A bank line whose text carries one is
    /// reported as company-confirmed; it never widens a match.
    /// </summary>
    public List<string> CompanyIds { get; set; } = [];
}

/// <summary>
/// Matches the disbursement account's ACH lines to transmitted NACHA files:
/// <list type="bullet">
/// <item>A settlement debit (credit file) or settlement credit (debit file)
/// settles the one transmitted, unsettled file whose total equals the amount
/// and whose effective entry date is within the window. When several qualify,
/// a file reference or run number in the line's references or text decides;
/// otherwise the line goes to suspense with the candidates. A settled file is
/// recorded as a <see cref="NachaFileSettlement"/>.</item>
/// <item>ACH return codes are flagged as returns, with the recent files as
/// candidates, and never settled or applied.</item>
/// </list>
/// </summary>
public sealed class NachaSettlementMatcher : IBankTransactionMatcher
{
    private readonly INachaSettlementCandidateSource _candidates;
    private readonly INachaSettlementStore _settlements;
    private readonly NachaSettlementOptions _options;
    private readonly BankReconciliationOptions _reconciliation;
    private readonly TimeProvider _clock;
    private readonly BankStatementFreeze _freeze;

    public NachaSettlementMatcher(
        INachaSettlementCandidateSource candidates,
        INachaSettlementStore settlements,
        IOptions<NachaSettlementOptions> options,
        IOptions<BankReconciliationOptions> reconciliation,
        IConfiguration configuration,
        BankStatementFreeze freeze,
        TimeProvider? clock = null)
    {
        _freeze = freeze;
        _candidates = candidates;
        _settlements = settlements;
        _options = options.Value;
        _reconciliation = reconciliation.Value;
        _clock = clock ?? TimeProvider.System;
        if (_options.CompanyIds.Count == 0 && configuration["Era:OriginatingCompanyId"] is { Length: > 0 } companyId)
            _options.CompanyIds.Add(companyId);
    }

    public async Task<MatchDecision> DecideAsync(BankTransaction tx, CancellationToken ct = default)
    {
        var asOf = tx.AsOfDate.Date;
        if (_options.AchReturnTypeCodes.Contains(tx.TypeCode))
        {
            var recent = await _candidates.TransmittedAsync(tx.TenantId, asOf.AddDays(-_options.ReturnLookbackDays), asOf, ct);
            return new MatchDecision("AchReturn", ReconciliationStatus.Return,
                $"ACH return (type {tx.TypeCode}) for {tx.Amount:0.00}: find the returned entry in the bank's return detail, then reissue or void the payment.",
                Candidates: recent.OrderByDescending(f => f.EffectiveEntryDate).Take(10)
                    .Select(f => $"PaymentFileTransmission:{f.FileReference}:{f.EffectiveEntryDate:yyyy-MM-dd}").ToList());
        }

        var settlesCreditFile = tx.Direction == Bai2Direction.Debit && _options.SettlementDebitTypeCodes.Contains(tx.TypeCode);
        var settlesDebitFile = tx.Direction == Bai2Direction.Credit && _options.SettlementCreditTypeCodes.Contains(tx.TypeCode);
        if (!settlesCreditFile && !settlesDebitFile)
            return MatchDecision.Ignore($"Type {tx.TypeCode} is not an ACH settlement or return.");

        var files = await _candidates.TransmittedAsync(tx.TenantId, asOf.AddDays(-_options.SettlementWindowDays), asOf.AddDays(1), ct);
        var byAmount = new List<PaymentFileTransmission>();
        foreach (var f in files.Where(f => (settlesCreditFile ? f.TotalCreditAmount : f.TotalDebitAmount) == tx.Amount))
            if (await _settlements.GetAsync(tx.TenantId, f.FileReference, ct) is null)
                byAmount.Add(f);

        var tokens = BankReferenceTokens.From(tx, 20);
        var named = byAmount.Where(f => tokens.Any(t =>
            string.Equals(t, f.FileReference, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(t, f.PaymentRunNumber, StringComparison.OrdinalIgnoreCase))).ToList();
        var pick = named.Count == 1 ? named[0] : byAmount.Count == 1 ? byAmount[0] : null;

        if (pick is null)
            return new MatchDecision("NachaSettlement", ReconciliationStatus.Suspense,
                byAmount.Count == 0
                    ? $"No transmitted, unsettled NACHA file totals {tx.Amount:0.00} with an effective date from {asOf.AddDays(-_options.SettlementWindowDays):yyyy-MM-dd} to {asOf.AddDays(1):yyyy-MM-dd}."
                    : $"{byAmount.Count} transmitted files total {tx.Amount:0.00} in the window and the bank line names none of them.",
                Candidates: byAmount.Select(f => $"PaymentFileTransmission:{f.FileReference}:{f.EffectiveEntryDate:yyyy-MM-dd}").ToList());

        return await SettleAsync(tx, pick, "matched", ct);
    }

    /// <summary>Records <paramref name="file"/> as settled by <paramref name="tx"/> (also used to resolve a suspense item).</summary>
    public async Task<MatchDecision> SettleAsync(BankTransaction tx, PaymentFileTransmission file, string settledBy, CancellationToken ct = default)
    {
        // Commit point: a supersession requested since the run started stops the settlement.
        await _freeze.EnsureNotFrozenAsync(tx.TenantId, tx.StatementId, ct);
        var itemId = ReconciliationItem.IdFor(_reconciliation.ServiceName, tx.Id);
        var settlement = new NachaFileSettlement
        {
            Id = NachaFileSettlement.KeyFor(tx.TenantId, file.FileReference),
            TenantId = tx.TenantId,
            FileReference = file.FileReference,
            PaymentRunId = file.PaymentRunId,
            Amount = tx.Amount,
            EffectiveEntryDate = file.EffectiveEntryDate,
            BankTransactionId = tx.Id,
            StatementId = tx.StatementId,
            ReconciliationItemId = itemId,
            BankAsOfDate = tx.AsOfDate,
            SettledBy = settledBy,
            SettledAt = _clock.GetUtcNow().UtcDateTime,
        };

        // Attach before the insert: a crash after persistence must not lose
        // accounting just because subsequent matching skips settled files.
        if (file.Status == PaymentFileTransmissionStatus.Transmitted
            && file.TenantId == tx.TenantId && file.TotalDebitAmount == 0m
            && file.TotalCreditAmount == tx.Amount && tx.Amount > 0m
            && tx.Direction == Bai2Direction.Debit)
        {
            var type = CloudHealthOffice.Finance.Contracts.GlEventTypes.PaymentFileSettled;
            var eventId = CloudHealthOffice.Finance.Contracts.GlEventTypes.EventIdFor(tx.TenantId,
                "settlement:" + file.FileReference, type);
            var payload = new CloudHealthOffice.Finance.Contracts.PaymentFileSettledEvent
            {
                EventId = eventId, TenantId = tx.TenantId, PaymentRunId = file.PaymentRunId,
                FileReference = file.FileReference,
                TransmissionEventId = PaymentService.Services.PaymentFileTransmissionService.EventIdFor(file,
                    PaymentFileOutboxMessage.TransmittedType),
                BankTransactionId = tx.Id, Amount = tx.Amount, BankAsOfDate = tx.AsOfDate.Date,
                PaymentRunExecutedAt = file.RunExecutedAt, SettledBy = settledBy,
            };
            settlement.GlOutbox.Add(new PaymentFileOutboxMessage
            {
                EventId = eventId, Type = type, CreatedAt = settlement.SettledAt,
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(payload,
                    CloudHealthOffice.Finance.Contracts.GlEventTypes.Json),
            });
        }
        else
        {
            settlement.GlAccountingLimitation = "Automatic GL settlement supports matching bank debits for whole, transmitted, credits-only claims files only.";
        }

        if (!await _settlements.TryInsertAsync(settlement, ct))
        {
            var existing = await _settlements.GetAsync(tx.TenantId, file.FileReference, ct);
            if (existing?.BankTransactionId != tx.Id)
                return new MatchDecision("NachaSettlement", ReconciliationStatus.Suspense,
                    $"File {file.FileReference} is already settled by another bank line ({existing?.BankTransactionId}).",
                    Candidates: [$"PaymentFileTransmission:{file.FileReference}"]);
            // This line settled it on an earlier run that stopped before recording the decision.
        }

        var company = _options.CompanyIds.Any(c => (tx.Text ?? string.Empty).Contains(c, StringComparison.OrdinalIgnoreCase)
                                                   || string.Equals(tx.CustomerReference, c, StringComparison.OrdinalIgnoreCase))
            ? " Company id confirmed in the bank text."
            : string.Empty;
        return new MatchDecision("NachaSettlement", ReconciliationStatus.Matched,
            $"Settles NACHA file {file.FileReference} (run {file.PaymentRunNumber}, effective {file.EffectiveEntryDate:yyyy-MM-dd}).{company}",
            "PaymentFileTransmission", [file.FileReference]);
    }
}
