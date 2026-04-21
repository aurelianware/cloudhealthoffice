namespace CloudHealthOffice.Infrastructure.Messaging;

/// <summary>
/// Binds from the <c>Messaging</c> configuration section.
/// </summary>
public class MessagingOptions
{
    public const string SectionName = "Messaging";

    /// <summary>
    /// Backend selection:
    ///   <c>Auto</c>      — Service Bus when <see cref="ServiceBusConnectionString"/>
    ///                       is set AND environment is not Development, else InMemory.
    ///   <c>ServiceBus</c> — force Service Bus; throws at startup if the connection
    ///                       string is missing.
    ///   <c>InMemory</c>   — force in-process channels.
    ///   <c>Null</c>       — no-op, for explicit-disable test scenarios.
    /// </summary>
    public string Backend { get; set; } = "Auto";

    /// <summary>Azure Service Bus namespace connection string.</summary>
    public string? ServiceBusConnectionString { get; set; }

    /// <summary>
    /// Duplicate-detection window for Service Bus duplicate detection.
    /// Only applies when the queue was provisioned with
    /// <c>RequiresDuplicateDetection=true</c> — this client does not create
    /// queues; queue creation is an infra concern.
    /// </summary>
    public TimeSpan DuplicateDetectionWindow { get; set; } = TimeSpan.FromHours(1);
}
