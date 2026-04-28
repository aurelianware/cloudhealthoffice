using System.Text.Json;
using ProviderService.Models;
using ProviderService.Models.CredentialingPayloads;
using CloudHealthOffice.Infrastructure.Json;

namespace ProviderService.Services;

/// <summary>
/// Pure function projecting an append-only
/// <see cref="CredentialingEvent"/> chain into the collapsed status
/// representation surfaced on <see cref="Provider"/> and returned by
/// <c>GET /credentialing/status</c>. No I/O, no mutable state — safe to
/// register as a singleton.
///
/// <para>
/// The projector is the single authority on the
/// <c>events → CredentialingStatus</c> mapping. Both the read-side
/// (<see cref="ICredentialingService.GetCurrentStatusAsync"/>) and the
/// write-side projection-patch use it to compute the exact value to
/// store on <see cref="Provider"/>; there is no second source of truth.
/// </para>
/// </summary>
public sealed class CredentialingProjector
{
    /// <summary>
    /// Project the chain into a collapsed status snapshot. Events must be
    /// supplied in ascending <see cref="CredentialingEvent.Version"/> order
    /// (oldest first); the caller is responsible for sorting.
    /// </summary>
    public CredentialingProjectionResult Project(
        IReadOnlyList<CredentialingEvent> eventsAscendingByVersion,
        DateTimeOffset asOf)
    {
        if (eventsAscendingByVersion == null || eventsAscendingByVersion.Count == 0)
        {
            return CredentialingProjectionResult.Empty;
        }

        string? currentApplicationEventId = null;
        DateTimeOffset? applicationSubmittedAt = null;

        // Most-recent terminal decision and its dates. Survives
        // RecredentialingTriggered (re-cred opens a new chain but the prior
        // approval's dates remain the predecessor for restore-on-withdraw).
        DecisionRecordedPayload? lastDecisionPayload = null;
        DateTimeOffset? lastDecidedAt = null;

        // True between RecredentialingTriggered and the next terminal
        // decision (or withdraw of the re-cred application).
        bool recredentialingPending = false;

        foreach (var evt in eventsAscendingByVersion)
        {
            switch (evt.EventType)
            {
                case CredentialingEventType.ApplicationSubmitted:
                    var submittedPayload = TryGetPayload<ApplicationSubmittedPayload>(evt);
                    // Defense in depth: applications synthesized by the
                    // legacy PUT /credentialing shim are paired with a
                    // matching DecisionRecorded by construction. Treat
                    // them as already-closed at projection time so a
                    // future bug producing an orphaned synthesized
                    // application can never present as a stuck open
                    // chain.
                    if (submittedPayload is { SynthesizedForDelegatedAuthority: true })
                    {
                        break;
                    }
                    currentApplicationEventId = evt.EventId;
                    applicationSubmittedAt = submittedPayload?.SubmittedAt;
                    break;

                case CredentialingEventType.PrimarySourceVerificationCompleted:
                case CredentialingEventType.CommitteeReviewScheduled:
                    // Informational — no projection state change. The chain
                    // stays open until DecisionRecorded or ApplicationWithdrawn.
                    break;

                case CredentialingEventType.DecisionRecorded:
                    var decision = TryGetPayload<DecisionRecordedPayload>(evt);
                    if (decision != null)
                    {
                        lastDecisionPayload = decision;
                        lastDecidedAt = decision.DecidedAt;
                    }
                    currentApplicationEventId = null;
                    applicationSubmittedAt = null;
                    recredentialingPending = false;
                    break;

                case CredentialingEventType.RecredentialingTriggered:
                    recredentialingPending = true;
                    // The chain is "open for re-credentialing" but no new
                    // ApplicationSubmitted has fired yet. Keep
                    // currentApplicationEventId null until the next
                    // ApplicationSubmitted lands.
                    break;

                case CredentialingEventType.ApplicationWithdrawn:
                    currentApplicationEventId = null;
                    applicationSubmittedAt = null;
                    recredentialingPending = false;
                    // Predecessor decision (if any) is restored by the
                    // status-mapping below — no extra bookkeeping needed.
                    break;

                case CredentialingEventType.Unknown:
                default:
                    break;
            }
        }

        var status = ComputeStatus(
            currentApplicationEventId,
            recredentialingPending,
            lastDecisionPayload,
            asOf);

        var credentialingDate = status == CredentialingStatus.Approved || status == CredentialingStatus.Expired
            ? lastDecisionPayload?.CredentialingDate
            : null;
        var recredentialingDueDate = status == CredentialingStatus.Approved || status == CredentialingStatus.Expired
            ? lastDecisionPayload?.RecredentialingDueDate
            : null;

        return new CredentialingProjectionResult(
            Status: status,
            CredentialingDate: credentialingDate,
            RecredentialingDueDate: recredentialingDueDate,
            CurrentApplicationEventId: currentApplicationEventId,
            ApplicationSubmittedAt: applicationSubmittedAt,
            LastDecisionAuthorityId: lastDecisionPayload?.DecisionAuthorityId,
            LastDecisionAuthorityType: lastDecisionPayload?.DecisionAuthorityType,
            LastDecidedAt: lastDecidedAt,
            EventCount: eventsAscendingByVersion.Count,
            LatestVersion: eventsAscendingByVersion[^1].Version);
    }

    private static CredentialingStatus ComputeStatus(
        string? currentApplicationEventId,
        bool recredentialingPending,
        DecisionRecordedPayload? lastDecisionPayload,
        DateTimeOffset asOf)
    {
        // Open application (initial or re-cred) outranks everything else —
        // we report Pending while the workflow is in flight.
        if (currentApplicationEventId != null) return CredentialingStatus.Pending;

        // Re-credentialing triggered but new application not yet submitted.
        if (recredentialingPending) return CredentialingStatus.Pending;

        // No open chain. Status reflects the last terminal decision.
        if (lastDecisionPayload == null) return CredentialingStatus.Unknown;

        if (lastDecisionPayload.Decision == CredentialingDecision.Denied)
            return CredentialingStatus.Denied;

        if (lastDecisionPayload.Decision == CredentialingDecision.Approved)
        {
            // Compute Expired at read time when the recredentialing-due
            // date has elapsed. The flat field on Provider is patched on
            // each event transition with the projector's at-write-time
            // verdict; readers who consult the projector directly get a
            // fresh evaluation each time.
            //
            // Normalize the stored due-date to UTC explicitly. Inbound
            // JSON often deserializes DateTime with Kind=Unspecified;
            // calling ToUniversalTime() on those would interpret them as
            // local time and shift the boundary by the server's
            // timezone offset. SpecifyKind treats Unspecified as
            // already-UTC (matching how the service writes the value).
            if (lastDecisionPayload.RecredentialingDueDate.HasValue)
            {
                var due = lastDecisionPayload.RecredentialingDueDate.Value;
                var dueUtc = due.Kind switch
                {
                    DateTimeKind.Utc => due,
                    DateTimeKind.Local => due.ToUniversalTime(),
                    _ => DateTime.SpecifyKind(due, DateTimeKind.Utc),
                };
                if (dueUtc < asOf.UtcDateTime) return CredentialingStatus.Expired;
            }
            return CredentialingStatus.Approved;
        }

        return CredentialingStatus.Unknown;
    }

    private static T? TryGetPayload<T>(CredentialingEvent evt) where T : class
    {
        if (evt.Payload == null) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(
                evt.Payload.ToJsonString(),
                CloudHealthOfficeJsonOptions.DefaultOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Read-side aggregate produced by <see cref="CredentialingProjector"/>.
/// Returned by <c>GET /providers/{id}/credentialing/status</c> and
/// consumed by <see cref="CredentialingService"/> when patching the
/// flat-field projection on <see cref="Provider"/>.
/// </summary>
public sealed record CredentialingProjectionResult(
    CredentialingStatus Status,
    DateTime? CredentialingDate,
    DateTime? RecredentialingDueDate,
    string? CurrentApplicationEventId,
    DateTimeOffset? ApplicationSubmittedAt,
    string? LastDecisionAuthorityId,
    DecisionAuthorityType? LastDecisionAuthorityType,
    DateTimeOffset? LastDecidedAt,
    int EventCount,
    int LatestVersion)
{
    public static readonly CredentialingProjectionResult Empty = new(
        Status: CredentialingStatus.Unknown,
        CredentialingDate: null,
        RecredentialingDueDate: null,
        CurrentApplicationEventId: null,
        ApplicationSubmittedAt: null,
        LastDecisionAuthorityId: null,
        LastDecisionAuthorityType: null,
        LastDecidedAt: null,
        EventCount: 0,
        LatestVersion: 0);
}
