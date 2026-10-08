using CloudHealthOffice.FeeScheduleEngine.Models;

namespace ClaimsService.Models.Adjudication;

/// <summary>
/// Per-claim pricing outcome populated by
/// <see cref="Services.Adjudication.Stages.PricingStage"/> (Order=250) and
/// consumed by <see cref="Services.Adjudication.Stages.BenefitCalculationStage"/>
/// (Order=300) as the per-line allowed amounts that drive cost-share.
///
/// <para>
/// <see cref="IsFullyPriced"/> is the only signal the benefit stage trusts:
/// when any line could not be priced against a contracted or plan-default
/// fee schedule (or the pricing service was unreachable), the benefit stage
/// refuses to calculate rather than letting the engine fall back to
/// allowed = billed.
/// </para>
/// </summary>
public sealed class PricingOutcome
{
    /// <summary>Allowed amount per claim line number, for every line that priced.</summary>
    public IReadOnlyDictionary<int, decimal> AllowedAmounts { get; init; } =
        new Dictionary<int, decimal>();

    /// <summary>Lines that could not be priced, with the reason for each.</summary>
    public IReadOnlyList<UnpricedLine> UnpricedLines { get; init; } = Array.Empty<UnpricedLine>();

    /// <summary>
    /// True when the fee-schedule resolution call itself failed (transport
    /// error, non-success status, unreadable body) — no line was priced.
    /// </summary>
    public bool PricingUnavailable { get; init; }

    /// <summary>Raw engine response, kept on the context for audit / remittance (CO-45).</summary>
    public PricingResultSet? RawResult { get; init; }

    /// <summary>True only when the service answered and every claim line has an allowed amount.</summary>
    public bool IsFullyPriced => !PricingUnavailable && UnpricedLines.Count == 0;
}

/// <summary>One claim line the pricing stage could not price.</summary>
public sealed record UnpricedLine(int LineNumber, string? ProcedureCode, string Reason);
