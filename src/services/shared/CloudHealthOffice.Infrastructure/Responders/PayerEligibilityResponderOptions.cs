namespace CloudHealthOffice.Infrastructure.Responders;

/// <summary>
/// Configuration for the payer-side eligibility responder. Bound from
/// <c>PayerEligibilityResponder</c>.
/// </summary>
public sealed class PayerEligibilityResponderOptions
{
    public const string SectionName = "PayerEligibilityResponder";

    /// <summary>
    /// When true, <c>AddChoPayerEligibilityResponder</c> registers the
    /// in-memory CHO Demo Health Plan directory. Production hosts should
    /// leave this false (the appsettings default) and register an
    /// <c>IPayerEligibilityDirectory</c> backed by member / coverage /
    /// benefit / accumulator services. Development sets this true via
    /// <c>appsettings.Development.json</c>.
    /// </summary>
    public bool UseInMemoryDirectory { get; set; }
}
