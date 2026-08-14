namespace CloudHealthOffice.BenefitEngine.Domain;

/// <summary>
/// Controls whether a benefit calculation is allowed to mutate persistent
/// financial state (accumulators) or must run as a side-effect-free
/// simulation.
///
/// <para>
/// This is an explicit execution context rather than a scattered set of
/// boolean flags. The cost-sharing waterfall is identical in both modes —
/// the same pricing, benefit, deductible, copay, coinsurance and OOP-max
/// logic runs — but in <see cref="Prospective"/> mode the engine skips the
/// accumulator <c>ApplyUpdatesAsync</c> write at the end of the pipeline, so
/// no deductible/OOP/visit/dollar counter is ever persisted.
/// </para>
///
/// <para>
/// The in-memory accumulator working set is still advanced during a
/// prospective calculation so the returned snapshot reflects what the
/// balances <em>would</em> become — useful for provider-facing estimates —
/// but that projection is never written back to storage.
/// </para>
/// </summary>
public enum AdjudicationExecutionMode
{
    /// <summary>
    /// Normal claim adjudication. Accumulator updates are persisted.
    /// This is the default so existing production behavior is unchanged.
    /// </summary>
    Production = 0,

    /// <summary>
    /// Read-only prospective adjudication (payment estimate). No persistent
    /// financial state is changed: accumulators are not written, no claim,
    /// payment record, or claim history is created, and no downstream
    /// workflow is triggered.
    /// </summary>
    Prospective = 1
}
