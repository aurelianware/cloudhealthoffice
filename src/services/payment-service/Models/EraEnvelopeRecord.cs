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

    /// <summary>Originating <see cref="PaymentRun.Id"/>.</summary>
    [Required]
    [StringLength(100)]
    public string PaymentRunId { get; set; } = string.Empty;

    /// <summary>Trading partner this envelope routes to.</summary>
    [Required]
    [StringLength(100)]
    public string TradingPartnerId { get; set; } = string.Empty;

    /// <summary>Raw 835 EDI content (one ISA/IEA file with one ST/SE envelope).</summary>
    [Required]
    public string EdiContent { get; set; } = string.Empty;

    /// <summary>Number of CLP loops within the envelope.</summary>
    public int ClaimCount { get; set; }

    /// <summary>BPR02 — sum of payment amounts across all claims in the envelope.</summary>
    public decimal TotalPaymentAmount { get; set; }

    /// <summary>ISA13 / IEA02 control number (9-digit zero-padded).</summary>
    [Required]
    [StringLength(9)]
    public string ControlNumber { get; set; } = string.Empty;

    /// <summary>UTC timestamp when the envelope was generated.</summary>
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Claim ids included in this envelope (audit-trail crumb for reconciliation).</summary>
    public List<string> ClaimIds { get; set; } = new();
}
