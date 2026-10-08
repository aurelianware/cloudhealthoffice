using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PaymentService.Models;

/// <summary>
/// Persistence record for one batched 835 envelope produced during a
/// PaymentRun execution. Mirrors <c>Payment</c> / <c>PaymentRun</c> in
/// shape: tenant-scoped, MongoDB-driver-backed, separate
/// <c>EraEnvelopes</c> collection.
///
/// <see cref="EdiContent"/> is stored inline. Phase 1 envelopes are
/// well under the MongoDB 16MB document limit (typical 835 with 50
/// claims is ~25KB; even 200 claims stays under 100KB). Phase 2 may
/// move to blob storage if envelope sizes grow or if retention rules
/// favor blob lifecycle policies over Mongo's TTL story.
/// </summary>
public class EraEnvelopeRecord
{
    /// <summary>Multi-tenant partition key (set by repository from request tenant context).</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Unique envelope identifier (Mongo document id).</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Originating <see cref="PaymentRun.Id"/>. Populated for envelopes
    /// produced by 5.10 PaymentRun execution. Mutually exclusive with
    /// <see cref="ReversalRunId"/>: exactly one of the two is set per
    /// envelope. Stays as a non-nullable string default for serialization
    /// stability with pre-5.12b rows; reversal envelopes leave this empty.
    /// </summary>
    [StringLength(100)]
    public string PaymentRunId { get; set; } = string.Empty;

    /// <summary>
    /// Originating <see cref="ReversalRun.Id"/>. Populated for envelopes
    /// produced by 5.12b ReversalRun execution. Mutually exclusive with
    /// <see cref="PaymentRunId"/>. Null on PaymentRun-produced envelopes
    /// (the steady state pre-5.12b).
    /// </summary>
    [StringLength(100)]
    public string? ReversalRunId { get; set; }

    /// <summary>Trading partner this envelope routes to.</summary>
    [Required]
    [StringLength(100)]
    public string TradingPartnerId { get; set; } = string.Empty;

    /// <summary>Raw 835 EDI content (one ISA/IEA file with one ST/SE envelope).</summary>
    [Required]
    public string EdiContent { get; set; } = string.Empty;

    /// <summary>Number of CLP loops within the envelope.</summary>
    public int ClaimCount { get; set; }

    /// <summary>
    /// BPR02 — sum of CLP04 less PLB across the envelope; never negative. A
    /// net-negative envelope (reversal recoupment) records 0 here and the
    /// negative balance in <see cref="ForwardBalanceAmount"/>.
    /// </summary>
    public decimal TotalPaymentAmount { get; set; }

    /// <summary>
    /// The negative balance this 835 carried forward in a PLB forward-balance
    /// (FB) adjustment, i.e. what the provider owes the plan; 0 when none.
    /// No ledger recovers it yet: a later 835's positive FB is a follow-up.
    /// </summary>
    public decimal ForwardBalanceAmount { get; set; }

    /// <summary>ISA13 / IEA02 control number (9-digit zero-padded).</summary>
    [Required]
    [StringLength(9)]
    public string ControlNumber { get; set; } = string.Empty;

    /// <summary>UTC timestamp when the envelope was generated.</summary>
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Claim ids included in this envelope (audit-trail crumb for reconciliation).</summary>
    public List<string> ClaimIds { get; set; } = new();

    /// <summary>The user who released the run that generated this 835 (the approver).</summary>
    public string? CreatedBy { get; set; }
}
