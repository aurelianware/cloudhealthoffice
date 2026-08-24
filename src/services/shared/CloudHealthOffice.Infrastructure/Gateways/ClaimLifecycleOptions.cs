namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Persistence for claim transmissions, 277CA acknowledgments, outbox, and
/// polling cursors. Bound from <c>HealthcareTransactions:ClaimLifecycle</c>.
/// </summary>
public sealed class ClaimLifecycleOptions
{
    /// <summary>
    /// <c>InMemory</c> (Development/tests) or <c>Mongo</c> (production).
    /// Empty means auto: InMemory in Development, Mongo otherwise.
    /// </summary>
    public string Store { get; set; } = string.Empty;

    public string MongoDatabaseName { get; set; } = "CloudHealthOffice";

    public string TransmissionsCollection { get; set; } = "claim_transmissions";

    public string AcknowledgmentsCollection { get; set; } = "claim_acknowledgments";

    public string CursorsCollection { get; set; } = "claim_acknowledgment_cursors";

    public string AttachmentsCollection { get; set; } = "claim_attachment_transmissions";

    public string InboundAttachmentsCollection { get; set; } = "inbound_claim_attachment_receipts";

    /// <summary>
    /// Hours of poll-window overlap when Stedi returns no next page token.
    /// Default 24 matches Stedi's "overlap by at least one day" guidance.
    /// </summary>
    public int PollOverlapHours { get; set; } = 24;

    /// <summary>Background outbox dispatch interval. 0 disables the publisher.</summary>
    public int OutboxIntervalSeconds { get; set; } = 15;

    /// <summary>
    /// Escape hatch for non-Development hosts that intentionally use process-local
    /// stores (never the default). Production 277CA ingress must not use this.
    /// </summary>
    public bool AllowInMemoryInNonDevelopment { get; set; }

    public bool UseMongo =>
        string.Equals(Store, "Mongo", StringComparison.OrdinalIgnoreCase);

    public bool UseInMemory =>
        string.Equals(Store, "InMemory", StringComparison.OrdinalIgnoreCase) ||
        string.IsNullOrWhiteSpace(Store);
}
