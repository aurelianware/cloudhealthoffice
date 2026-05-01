namespace ClaimsService.Models.Adjudication;

/// <summary>
/// Discriminator for the scrubbing stage's decision on a single claim.
///
/// <para>
/// Default rules (<c>DefaultStandardRules.Create()</c>) only produce
/// Errors and Warnings — no rule pends a claim today, so no
/// <c>PendForReview</c> value exists. Add the value when a rule that
/// produces it ships, not before.
/// </para>
/// </summary>
public enum ScrubbingDecision
{
    /// <summary>Engine returned clean OR warnings only — claim continues.</summary>
    Approve,

    /// <summary>Engine returned at least one Error result — pipeline rejects.</summary>
    RejectStructural,
}

/// <summary>
/// One rule violation surfaced by <c>CloudHealthOffice.ClaimsScrubEngine</c>.
/// Mirrors the engine's <c>ValidationResult</c> shape, projected onto the
/// fields downstream consumers (277CA generation, work-queue UI) actually
/// need. The engine's full result list lives on the response; this is the
/// pipeline-side audit shape.
/// </summary>
public sealed record RuleViolation(
    string RuleId,
    string RuleName,
    string Message,
    string? Field,
    string? EditCode,
    IReadOnlyList<int>? ServiceLines);

/// <summary>
/// Structured outcome the scrubbing stage publishes onto
/// <see cref="Services.Adjudication.ClaimAdjudicationContext.ScrubbingResult"/>.
/// One entry per claim run. Mirrors 5.6's
/// <see cref="EnforcementOutcome"/> shape (immutable record-style payload
/// the stage writes once and downstream stages read).
///
/// <para>
/// PersistenceStage (5.5) does not currently project this onto the
/// persisted claim version — that's the same audit-trail gap that 5.6's
/// <see cref="EnforcementOutcome"/> accumulator has. A focused follow-up
/// will backfill both at once; in the meantime the outcome flows through
/// stage results (Reject reason on the emitted Service Bus message).
/// </para>
/// </summary>
public sealed class ScrubbingOutcome
{
    public ScrubbingDecision Decision { get; init; }

    public IReadOnlyList<RuleViolation> Errors { get; init; } = Array.Empty<RuleViolation>();

    public IReadOnlyList<RuleViolation> Warnings { get; init; } = Array.Empty<RuleViolation>();

    /// <summary>
    /// Engine's <c>ClaimRoutingDecision.Reason</c> — human-readable
    /// summary of why the claim was routed where it was.
    /// </summary>
    public string? RoutingNote { get; init; }

    /// <summary>Total rules executed by the engine for this claim.</summary>
    public int RulesExecuted { get; init; }

    /// <summary>
    /// Engine's status string: <c>"clean"</c>, <c>"flagged"</c>, or
    /// <c>"rejected"</c>. Pass-through for telemetry / debugging.
    /// </summary>
    public string EngineStatus { get; init; } = "clean";
}
