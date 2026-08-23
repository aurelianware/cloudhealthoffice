using CloudHealthOffice.Infrastructure.Gateways;

namespace CloudHealthOffice.Infrastructure.ReferenceData.Payers;

/// <summary>
/// Outcome of resolving a caller-supplied payer identifier to a canonical
/// payer (and, when requested, a specific external identifier). Resolution
/// never guesses: zero matches and multiple matches are both explicit failures.
/// </summary>
public sealed class PayerResolution
{
    public PayerResolutionStatus Status { get; init; }

    public PayerReference? Payer { get; init; }

    /// <summary>
    /// External identifier selected for the requested system/type (e.g. the
    /// Stedi trading-partner service id). Null when resolution did not produce
    /// one.
    /// </summary>
    public string? ExternalIdentifierValue { get; init; }

    public string? Message { get; init; }

    /// <summary>
    /// True when the identifier was taken from the deprecated configuration
    /// <c>PayerMap</c>/<c>TenantPayerMap</c> rather than the directory.
    /// </summary>
    public bool UsedDeprecatedFallback { get; init; }

    public static PayerResolution Found(
        PayerReference payer, string? externalIdentifier = null, bool usedDeprecatedFallback = false) =>
        new()
        {
            Status = PayerResolutionStatus.Found,
            Payer = payer,
            ExternalIdentifierValue = externalIdentifier,
            UsedDeprecatedFallback = usedDeprecatedFallback
        };

    public static PayerResolution Fail(PayerResolutionStatus status, string message) =>
        new() { Status = status, Message = message };
}

public enum PayerResolutionStatus
{
    Found = 0,
    PayerNotFound = 1,
    AmbiguousPayer = 2,
    ExternalIdentifierMissing = 3,
    TransactionUnsupported = 4,
    EnrollmentRequired = 5,
    PayerDisabled = 6,
    ReferenceDataUnavailable = 7
}

/// <summary>Lookup criteria for administrative payer search. Not used to route transactions.</summary>
public sealed class PayerSearchQuery
{
    public string? Id { get; set; }

    /// <summary>
    /// Exact match against canonical id, alias, or external identifier value;
    /// case-insensitive contains match against payer name. Search never picks
    /// a winner — it returns every match.
    /// </summary>
    public string? Text { get; set; }

    public string? ExternalSystem { get; set; }

    public string? ExternalType { get; set; }

    public string? ExternalValue { get; set; }

    public bool? Active { get; set; }

    public int MaxResults { get; set; } = 50;
}

/// <summary>Result of a directory synchronization run.</summary>
public sealed class PayerDirectorySyncResult
{
    public bool Succeeded { get; init; }

    public string Source { get; init; } = string.Empty;

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset CompletedAt { get; init; }

    public int Received { get; init; }

    public int Added { get; init; }

    public int Updated { get; init; }

    public int Disabled { get; init; }

    public int SkippedMalformed { get; init; }

    public string? Error { get; init; }

    public TimeSpan Duration => CompletedAt - StartedAt;
}

/// <summary>Last-known synchronization status for a source directory.</summary>
public sealed class PayerDirectorySyncStatus
{
    public string Source { get; set; } = string.Empty;

    public DateTimeOffset? LastAttemptedAt { get; set; }

    public DateTimeOffset? LastSucceededAt { get; set; }

    public bool LastSucceeded { get; set; }

    public int LastReceived { get; set; }

    public string? LastError { get; set; }
}
