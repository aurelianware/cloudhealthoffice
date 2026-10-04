using MongoDB.Bson.Serialization.Attributes;
using System.Text.Json.Serialization;

namespace PaymentService.Models;

/// <summary>
/// A claim reservation a run holds that reconciliation could not release
/// safely: a person must look, then release it with
/// POST /api/{paymentruns|reversalruns}/{id}/reservations/{claimId}/release.
/// </summary>
public class ReservationAttention
{
    public string ClaimId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTime FlaggedAt { get; set; }
}

/// <summary>
/// What reconciliation (or a manual release) changes on one run. Applied as a
/// partial update so it never overwrites the run's own status or results.
/// </summary>
public sealed class ReservationOutcomes
{
    /// <summary>Claims whose reservation was released (added to ReleasedReservationClaimIds).</summary>
    public List<string> Released { get; } = new();

    /// <summary>Claims flagged, or re-flagged with a new reason (replace any earlier entry for the claim).</summary>
    public List<ReservationAttention> Attention { get; } = new();

    /// <summary>Claims no longer needing attention (their entry is removed).</summary>
    public List<string> Cleared { get; } = new();

    public List<string> Warnings { get; } = new();

    public bool IsEmpty => Released.Count == 0 && Attention.Count == 0 && Cleared.Count == 0 && Warnings.Count == 0;

    /// <summary>Every claim whose attention entry is replaced or removed.</summary>
    public IReadOnlyCollection<string> TouchedClaimIds
        => Released.Concat(Cleared).Concat(Attention.Select(a => a.ClaimId)).ToHashSet(StringComparer.Ordinal);

    /// <summary>Applies the outcomes to a run's lists (stores that read, modify and conditionally replace).</summary>
    public void ApplyTo(List<string> released, List<ReservationAttention> attention, List<string> warnings)
    {
        var touched = TouchedClaimIds;
        attention.RemoveAll(a => touched.Contains(a.ClaimId));
        attention.AddRange(Attention);
        foreach (var claimId in Released)
        {
            if (!released.Contains(claimId))
                released.Add(claimId);
        }
        warnings.AddRange(Warnings);
    }
}

public enum ReservationAuditAction
{
    /// <summary>Released by the reconciliation job (run failed or cancelled before paying; no payment, no 835).</summary>
    AutoReleased,

    /// <summary>Flagged NeedsAttention by the reconciliation job.</summary>
    FlaggedNeedsAttention,

    /// <summary>Released by a second approver with a reason.</summary>
    Released,
}

/// <summary>
/// One reservation release or flag. Collection / container
/// <c>PaymentReservationAudit</c> (Cosmos partition key <c>/tenantId</c>).
/// Written once, never updated.
/// </summary>
public class ReservationAuditEntry
{
    [BsonId]
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>"Payment" or "Reversal".</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("runId")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("runNumber")]
    public string? RunNumber { get; set; }

    [JsonPropertyName("claimId")]
    public string ClaimId { get; set; } = string.Empty;

    [JsonPropertyName("action")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    [BsonRepresentation(MongoDB.Bson.BsonType.String)]
    public ReservationAuditAction Action { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    /// <summary>The releasing user, or "payment-service" for the job.</summary>
    [JsonPropertyName("actor")]
    public string Actor { get; set; } = string.Empty;

    [JsonPropertyName("actorIsService")]
    public bool ActorIsService { get; set; }

    [JsonPropertyName("at")]
    public DateTime At { get; set; }

    [JsonPropertyName("runStatus")]
    public string? RunStatus { get; set; }

    [JsonPropertyName("runExecutedBy")]
    public string? RunExecutedBy { get; set; }

    [JsonPropertyName("reservedAt")]
    public DateTime ReservedAt { get; set; }

    [JsonPropertyName("reservedBy")]
    public string? ReservedBy { get; set; }

    /// <summary>A payment record (any status) for the claim existed when the entry was written.</summary>
    [JsonPropertyName("paymentFound")]
    public bool PaymentFound { get; set; }

    /// <summary>An 835 envelope listing the claim existed when the entry was written.</summary>
    [JsonPropertyName("envelopeFound")]
    public bool EnvelopeFound { get; set; }
}

/// <summary>Configuration section <c>PaymentRuns</c>.</summary>
public sealed class ReservationReconciliationOptions
{
    public const string SectionName = "PaymentRuns";

    /// <summary>A failed or cancelled run's reservation is auto-released only once this old. Default 30 minutes.</summary>
    public TimeSpan ReservationGracePeriod { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>A run still Running this long after it started is flagged as stuck. Default 2 hours.</summary>
    public TimeSpan ReservationStuckThreshold { get; set; } = TimeSpan.FromHours(2);

    /// <summary>How often the hosted job runs (its first pass waits one interval). Default 5 minutes.</summary>
    public TimeSpan ReservationReconciliationInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Turns the hosted job off; the manual release and the read endpoint still work.</summary>
    public bool ReservationReconciliationEnabled { get; set; } = true;
}

/// <summary>A reservation flagged NeedsAttention, as the read endpoint lists it.</summary>
public sealed class ReservationNeedingAttentionView
{
    public string Kind { get; set; } = string.Empty;
    public string ClaimId { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string? RunNumber { get; set; }
    public string? Reason { get; set; }
    public DateTime? FlaggedAt { get; set; }
    public DateTime ReservedAt { get; set; }
    public string? ReservedBy { get; set; }

    /// <summary>Where a second approver releases it.</summary>
    public string ReleasePath { get; set; } = string.Empty;
}

/// <summary>The result of a manual release.</summary>
public sealed class ReservationReleaseResult
{
    public string Kind { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string? RunNumber { get; set; }
    public string ClaimId { get; set; } = string.Empty;
    public string ReleasedBy { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTime ReleasedAt { get; set; }
    public bool EnvelopeFound { get; set; }
}

/// <summary>Body of a manual release.</summary>
public sealed class ReleaseReservationRequest
{
    /// <summary>Required: why the claim may be paid (or reversed) again.</summary>
    public string? Reason { get; set; }
}

/// <summary>What one reconciliation pass did in one tenant.</summary>
public sealed class ReconciliationPassResult
{
    public string TenantId { get; set; } = string.Empty;
    public int Examined { get; set; }
    public List<string> AutoReleased { get; } = new();
    public List<string> Flagged { get; } = new();
    public List<string> Cleared { get; } = new();
    public List<string> SkippedChanged { get; } = new();
}
