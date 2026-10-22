using CloudHealthOffice.FeeScheduleEngine.Configuration;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Persistence;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.FeeScheduleEngine.Services;

/// <summary>
/// The shadow half of ADR 016 phases 4 and 5: prices every claim twice and
/// reports per line what the store it was <i>not</i> paid on would have allowed.
/// During phase 4's window the claim is paid on legacy; after phase 5's cutover
/// it is paid on canonical and legacy becomes the shadow, so the comparison keeps
/// running for a cycle in the other direction.
///
/// <para>
/// The paid pass is the engine exactly as it is, reading contracts through
/// <see cref="DualReadProviderContractRepository"/> — which answers with the
/// contract the tenant's mode says to pay on and records both in
/// <see cref="ContractShadowLedger"/>. The shadow pass is a second engine whose
/// contract repository is that ledger, so it reads no database and sees the same
/// pair of contracts the paid pass did.
/// </para>
///
/// <para>
/// <b>The returned result is always the paid pass.</b> Nothing the shadow pass
/// does can change an allowed amount, and nothing it throws can stop a claim
/// being paid: a failure is recorded as
/// <see cref="ParityOutcome.ShadowFailed"/> and the paid result is returned
/// unchanged. The window costs one extra pricing pass per claim, which is why
/// it is per-tenant and time-boxed to one monthly claim cycle.
/// </para>
/// </summary>
public sealed class ShadowPricingRateResolutionService : IRateResolutionService
{
    private readonly IRateResolutionService _paid;
    private readonly IRateResolutionService _shadow;
    private readonly ContractShadowLedger _ledger;
    private readonly IPricingParityRecorder _recorder;
    private readonly ILogger<ShadowPricingRateResolutionService> _logger;

    public ShadowPricingRateResolutionService(
        IRateResolutionService paid,
        IRateResolutionService shadow,
        ContractShadowLedger ledger,
        IPricingParityRecorder recorder,
        ILogger<ShadowPricingRateResolutionService> logger)
    {
        _paid = paid;
        _shadow = shadow;
        _ledger = ledger;
        _recorder = recorder;
        _logger = logger;
    }

    public async Task<PricingResult> ResolveAsync(PricingRequest request, CancellationToken ct = default)
    {
        _ledger.Clear();
        var paid = await _paid.ResolveAsync(request, ct);

        await ShadowAsync([request], [paid], single: true, ct);
        return paid;
    }

    public async Task<PricingResultSet> ResolveBatchAsync(
        IReadOnlyList<PricingRequest> requests, CancellationToken ct = default)
    {
        _ledger.Clear();
        var paid = await _paid.ResolveBatchAsync(requests, ct);

        await ShadowAsync(requests, paid.LineResults, single: false, ct);
        return paid;
    }

    private async Task ShadowAsync(
        IReadOnlyList<PricingRequest> requests,
        IReadOnlyList<PricingResult> paid,
        bool single,
        CancellationToken ct)
    {
        // Nothing was looked up, so there is nothing this window can say. A
        // claim whose lines never reached a contract lookup is priced the same
        // way by both stores by construction.
        if (_ledger.Entries.Count == 0)
            return;

        var canonicalExists = _ledger.HasCanonicalContract;
        var mode = _ledger.Mode;

        try
        {
            var shadow = single
                ? [await _shadow.ResolveAsync(requests[0], ct)]
                : (await _shadow.ResolveBatchAsync(requests, ct)).LineResults;

            RecordComparison(requests, paid, shadow, mode, canonicalExists);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                ex,
                "The canonical contract shadow pass failed for {LineCount} line(s). The claim is paid on the " +
                "legacy contract, as it is for the whole window, and this claim contributes nothing to parity.",
                requests.Count);

            foreach (var (request, result) in Zip(requests, paid))
                _recorder.Record(Failed(request, result, mode, ex));
        }
    }

    private void RecordComparison(
        IReadOnlyList<PricingRequest> requests,
        IReadOnlyList<PricingResult> paid,
        IReadOnlyList<PricingResult> shadow,
        ContractPricingMode mode,
        bool canonicalExists)
    {
        // Both passes price the same requests in the same order, so a length
        // that disagrees is a bug in the engine rather than a parity finding,
        // and comparing by position would then compare the wrong lines.
        if (shadow.Count != paid.Count)
        {
            _logger.LogError(
                "The shadow pass returned {ShadowCount} line(s) for {PaidCount} priced line(s); the two cannot " +
                "be compared. The claim is paid on the legacy contract.",
                shadow.Count, paid.Count);

            foreach (var (request, result) in Zip(requests, paid))
            {
                _recorder.Record(new LineParity
                {
                    TenantId = request.TenantId,
                    ProviderNpi = request.ProviderNpi,
                    PlanId = request.PlanId,
                    ServiceDate = request.ServiceDate,
                    LineNumber = request.LineNumber,
                    ProcedureCode = request.ProcedureCode,
                    Outcome = ParityOutcome.ShadowFailed,
                    Mode = mode,
                    LegacyAmount = result.AllowedAmount,
                    CanonicalAmount = result.AllowedAmount,
                    Reason = $"the shadow pass returned {shadow.Count} line(s) for {paid.Count} priced line(s)",
                });
            }

            return;
        }

        for (var i = 0; i < paid.Count; i++)
            _recorder.Record(PricingParityComparer.Compare(
                requests[i], paid[i], shadow[i], mode, canonicalExists));
    }

    private static LineParity Failed(
        PricingRequest request, PricingResult paid, ContractPricingMode mode, Exception ex) => new()
    {
        TenantId = request.TenantId,
        ProviderNpi = request.ProviderNpi,
        PlanId = request.PlanId,
        ServiceDate = request.ServiceDate,
        LineNumber = request.LineNumber,
        ProcedureCode = request.ProcedureCode,
        Outcome = ParityOutcome.ShadowFailed,
        Mode = mode,
        // Nothing is known about the other store on this line, so neither amount
        // is a finding: a failed shadow pass measures nothing, and recording a
        // zero would read as a delta the size of the claim.
        LegacyAmount = paid.AllowedAmount,
        CanonicalAmount = paid.AllowedAmount,
        Reason = ex.Message,
    };

    private static IEnumerable<(PricingRequest Request, PricingResult Result)> Zip(
        IReadOnlyList<PricingRequest> requests, IReadOnlyList<PricingResult> results)
    {
        for (var i = 0; i < Math.Min(requests.Count, results.Count); i++)
            yield return (requests[i], results[i]);
    }
}
