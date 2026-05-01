namespace CloudHealthOffice.Events;

/// <summary>
/// Service Bus topic constants for the AI examination completion stream.
/// Capability 5.9. Producer is claims-examiner-service; first consumer is
/// 5.10 remittance generation.
///
/// <para>
/// Distinct from <c>claim-version-events</c> (5.5): different domain (AI
/// completion vs. claim version transitions), different cadence (sparse —
/// only NCCI bundling pends with addressable edits), different
/// subscription patterns (5.10 filters by recommended disposition). A
/// dedicated topic keeps subscription rules simple and lets the AI
/// completion stream scale independently of the much higher-volume
/// version-events topic.
/// </para>
/// </summary>
public static class AiExaminationEventTopics
{
    /// <summary>Hyphenated naming mirrors <c>claim-version-events</c>.</summary>
    public const string TopicName = "ai-examination-events";

    /// <summary>
    /// Service Bus application property used to discriminate message
    /// types within the topic. Filter rules on each subscription select
    /// the relevant subset; producer side sets this on every
    /// <c>SendOptions.Properties</c>. Mirrors
    /// <c>ClaimVersionEventTopics.MessageTypeProperty</c>.
    /// </summary>
    public const string MessageTypeProperty = "MessageType";

    /// <summary>
    /// Stable string for the completion-event message type.
    /// Adding new values requires a paired Bicep subscription/filter
    /// update if any subscriber wants to consume them.
    /// </summary>
    public const string CompletedMessageType = "ClaimAiExaminationCompleted";
}
