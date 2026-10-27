using System.ComponentModel.DataAnnotations;
using AuthorizationService.Models;
using AuthorizationService.Services.Fhir;
using CloudHealthOffice.Infrastructure.Data;

namespace AuthorizationService.Services.Cms0057;

public static class PaReportingEvidence
{
    // BSON DateTime is millisecond precision. Canonicalize before comparing
    // idempotency payloads so a persisted retry cannot conflict on lost ticks.
    private static DateTime Milliseconds(DateTime value)
    {
        var utc = AuthorizationDeadlinePolicy.Utc(value);
        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
    }

    public static void Initialize(Authorization authorization, DateTime recordedAt)
    {
        // Never accept an incoming ledger, even from an internal create caller.
        authorization.ReportingLedger = new PaReportingLedger
        {
            ReceivedAt = Milliseconds(AuthorizationDeadlinePolicy.Receipt(authorization)), RecordedAt = Milliseconds(recordedAt),
            LineOfBusiness = authorization.LineOfBusiness,
            Expedited = Cms0057Reporting.IsExpedited(authorization),
            DrugExcluded = !PriorAuthorizationApiScope.IsInScope(authorization),
        };
        CaptureDecision(authorization, null, recordedAt);
    }

    public static void PreserveAndCapture(Authorization authorization, Authorization previous, DateTime recordedAt)
    {
        // Existing records without evidence remain legacy; do not invent a history.
        authorization.ReportingLedger = previous.ReportingLedger;
        CaptureDecision(authorization, previous, recordedAt);
    }

    private static void CaptureDecision(Authorization authorization, Authorization? previous, DateTime now)
    {
        var ledger = authorization.ReportingLedger;
        var outcome = Cms0057Reporting.DecisionStatus(authorization);
        if (ledger is null || ledger.Version != 1 || !Cms0057Reporting.IsDecided(outcome)) return;
        if (previous is not null && Cms0057Reporting.DecisionStatus(previous) == outcome
            && (previous.ReviewedDate is { } p ? Milliseconds(p) : (DateTime?)null)
                == (authorization.ReviewedDate is { } a ? Milliseconds(a) : (DateTime?)null)) return;
        now = Milliseconds(now);
        DateTime? decisionAt = authorization.ReviewedDate is { } value
            && Milliseconds(value) >= ledger.ReceivedAt && Milliseconds(value) <= now ? Milliseconds(value) : null;
        ledger.Facts.Add(new PaReportingFact
        {
            EventId = $"native-decision:{authorization.Revision}", Kind = PaReportingFactKind.Determination,
            EffectiveAt = decisionAt ?? now, RecordedAt = now, DecisionAt = decisionAt,
            Outcome = outcome, SourceSystem = "cho-native",
            EvidenceReference = $"revision:{authorization.Revision}",
            RecordedBy = authorization.LastUpdatedBy ?? authorization.CreatedBy ?? "cho-native",
        });
    }

    /// <summary>
    /// The reporting fact for an extension CHO itself granted (the native
    /// decision-extension endpoint), or null for a legacy record without evidence.
    /// Sourced extension evidence from another system still goes through <see cref="Prepare"/>.
    /// </summary>
    public static PaReportingFact? NativeExtension(Authorization authorization,
        AuthorizationDecisionExtension extension, long revision)
    {
        var ledger = authorization.ReportingLedger;
        if (ledger is null || ledger.Version != 1) return null;
        return new PaReportingFact
        {
            EventId = $"native-extension:{revision}", Kind = PaReportingFactKind.ReviewExtension,
            EffectiveAt = Milliseconds(extension.GrantedAt), RecordedAt = Milliseconds(extension.GrantedAt),
            OriginalDueAt = Milliseconds(extension.OriginalDueAt), ExtendedDueAt = Milliseconds(extension.ExtendedDueAt),
            SourceSystem = "cho-native", EvidenceReference = $"revision:{revision}", RecordedBy = extension.GrantedBy,
        };
    }

    /// <summary>Returns an existing exact replay; reused keys with different facts conflict.</summary>
    public static PaReportingFact Prepare(Authorization authorization, RecordPaReportingFact command,
        string actor, DateTime now)
    {
        Validator.ValidateObject(command, new ValidationContext(command), validateAllProperties: true);
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("An authenticated actor is required.");
        var ledger = authorization.ReportingLedger
            ?? throw new ConcurrencyConflictException("Legacy authorization has no verified reporting receipt; reconcile it first.");
        if (ledger.Version != 1) throw new ConcurrencyConflictException("Reporting evidence version is not supported by this writer.");
        if (command.Kind is not (PaReportingFactKind.AppealDecision or PaReportingFactKind.ReviewExtension)
            || string.Equals(command.SourceSystem, "cho-native", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only sourced PA appeal decisions and review-extension facts may be recorded.");
        now = Milliseconds(now);
        var at = Milliseconds(command.EffectiveAt.UtcDateTime);
        DateTime? originalDue = command.OriginalDueAt is { } original ? Milliseconds(original.UtcDateTime) : null;
        DateTime? extendedDue = command.ExtendedDueAt is { } extended ? Milliseconds(extended.UtcDateTime) : null;
        if (at < ledger.ReceivedAt || at > now)
            throw new ArgumentException("Fact time must be between receipt and server time.");
        if (command.Kind == PaReportingFactKind.AppealDecision)
        {
            if (command.Outcome is not (AuthorizationStatus.Approved or AuthorizationStatus.Modified or AuthorizationStatus.Denied)
                || command.OriginalDueAt is not null || command.ExtendedDueAt is not null)
                throw new ArgumentException("An appeal fact needs a clinical outcome and no extension dates.");
            if (!ledger.Facts.Any(f => f.Kind == PaReportingFactKind.Determination && f.EffectiveAt <= at
                && f.Outcome is AuthorizationStatus.Denied or AuthorizationStatus.Modified))
                throw new ArgumentException("A PA appeal must follow an evidenced adverse determination.");
        }
        else if (command.Outcome is not null || originalDue is null || extendedDue is null
            || extendedDue <= originalDue || at > originalDue.Value)
            throw new ArgumentException("An extension fact needs increasing due dates and a grant no later than the original deadline.");

        var fact = new PaReportingFact
        {
            EventId = command.EventId, Kind = command.Kind, EffectiveAt = at, RecordedAt = now,
            Outcome = command.Outcome,
            OriginalDueAt = originalDue, ExtendedDueAt = extendedDue,
            SourceSystem = command.SourceSystem,
            EvidenceReference = command.EvidenceReference, RecordedBy = actor,
        };
        var existing = ledger.Facts.SingleOrDefault(f => f.EventId == fact.EventId && f.SourceSystem == fact.SourceSystem);
        if (existing is not null)
        {
            if (existing.Kind != fact.Kind || existing.EffectiveAt != fact.EffectiveAt || existing.Outcome != fact.Outcome
                || existing.OriginalDueAt != fact.OriginalDueAt || existing.ExtendedDueAt != fact.ExtendedDueAt
                || existing.EvidenceReference != fact.EvidenceReference)
                throw new ConcurrencyConflictException("Reporting event key already identifies different evidence.");
            return existing;
        }
        if (ledger.Facts.Count >= 10000)
            throw new ConcurrencyConflictException("Reporting evidence limit reached; archive design is required.");
        return fact;
    }
}
