using System.Text.Json.Serialization;

namespace AppealsService.Models;

/// <summary>
/// Envelope shape published by the
/// <c>infrastructure/argo-workflows/x12-275-ingest.yaml</c> workflow onto
/// the <c>attachments-in</c> Kafka topic. Argo parses the X12 envelope
/// and emits this JSON; appeals-service consumes it without re-parsing
/// the EDI.
///
/// <see cref="Context"/>, <see cref="ClaimId"/>, and <see cref="ControlNumber"/>
/// are populated by the Argo workflow follow-up that ships alongside
/// this consumer (tracked separately under
/// <c>infrastructure/argo-workflows/</c>). Until that ships, every
/// production message will be skipped (<c>Context</c> absent) or
/// dead-lettered (<c>ClaimId</c> absent) — see
/// <see cref="HostedServices.Attachment275ConsumerHostedService"/> for
/// the routing decision tree.
/// </summary>
public sealed class Attachment275EnvelopeDto
{
    /// <summary>Tenant the attachment belongs to. Required for routing.</summary>
    [JsonPropertyName("tenantId")]
    public required string TenantId { get; init; }

    /// <summary>
    /// Routing discriminator. <c>"appeal"</c> for appeal-context 275s.
    /// Other values (including <c>null</c>) are skipped at the consumer
    /// for routing to a future authorization-service consumer of the same
    /// topic.
    /// </summary>
    [JsonPropertyName("context")]
    public string? Context { get; init; }

    /// <summary>
    /// Claim identifier the 275 references. Used by
    /// <c>IAppealRepository.GetMostRecentAppealByClaimIdAsync</c> to
    /// locate the open appeal. Required for appeal-context routing;
    /// absence triggers dead-letter.
    /// </summary>
    [JsonPropertyName("claimId")]
    public string? ClaimId { get; init; }

    [JsonPropertyName("authorizationId")] public string? AuthorizationId { get; init; }
    [JsonPropertyName("rfaiReference")]   public string? RfaiReference { get; init; }
    [JsonPropertyName("payerId")]         public string? PayerId { get; init; }
    [JsonPropertyName("payerName")]       public string? PayerName { get; init; }
    [JsonPropertyName("providerId")]      public string? ProviderId { get; init; }
    [JsonPropertyName("providerName")]    public string? ProviderName { get; init; }
    [JsonPropertyName("subscriberId")]    public string? SubscriberId { get; init; }
    [JsonPropertyName("patientFirstName")] public string? PatientFirstName { get; init; }
    [JsonPropertyName("patientLastName")]  public string? PatientLastName { get; init; }
    [JsonPropertyName("documentType")]    public string? DocumentType { get; init; }
    [JsonPropertyName("documentFormat")]  public string? DocumentFormat { get; init; }

    /// <summary>
    /// Raw X12 envelope as published by Argo. Carried opaquely through
    /// this consumer — appeals-service does NOT parse X12. May contain
    /// PHI; never log this field.
    /// </summary>
    [JsonPropertyName("rawX12")]          public string? RawX12 { get; init; }

    [JsonPropertyName("submittedDate")]   public DateTime SubmittedDate { get; init; }

    /// <summary>
    /// Free-text supplementary notes from the upstream submitter.
    /// May contain PHI; encrypted at rest before persistence and never
    /// logged.
    /// </summary>
    [JsonPropertyName("notes")]           public string? Notes { get; init; }

    /// <summary>
    /// 275 transaction-set reference (X12 BHT03). Used as
    /// <c>AppealEvent.CorrelationId</c> on the resulting audit event so
    /// downstream payer responses (277CA, 824) that echo BHT03 correlate
    /// end-to-end.
    /// </summary>
    [JsonPropertyName("controlNumber")]   public string? ControlNumber { get; init; }

    /// <summary>
    /// X12 PWK02 transmission code when surfaced by the Argo extractor.
    /// Defaults to <c>"EL"</c> at the mapper when absent or unrecognized.
    /// </summary>
    [JsonPropertyName("transmissionCode")] public string? TransmissionCode { get; init; }
}
