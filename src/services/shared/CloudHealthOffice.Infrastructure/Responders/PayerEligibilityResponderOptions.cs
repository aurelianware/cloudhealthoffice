namespace CloudHealthOffice.Infrastructure.Responders;

/// <summary>
/// Configuration for the payer-side eligibility responder. Bound from
/// <c>PayerEligibilityResponder</c>.
/// </summary>
public sealed class PayerEligibilityResponderOptions
{
    public const string SectionName = "PayerEligibilityResponder";

    /// <summary>
    /// When true (the Development default), the in-memory CHO Demo Health
    /// Plan directory is used. Production hosts should set this false and
    /// register a directory backed by member / coverage / benefit /
    /// accumulator services.
    /// </summary>
    public bool UseInMemoryDirectory { get; set; } = true;
}
