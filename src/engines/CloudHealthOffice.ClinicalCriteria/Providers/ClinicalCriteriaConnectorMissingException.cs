namespace CloudHealthOffice.ClinicalCriteria.Providers;

/// <summary>
/// Thrown when a licensed criteria provider is enabled in configuration but
/// the deployment carries no connector for it.
///
/// The platform fails loudly rather than quietly pretending the guidelines
/// were consulted: the request pends to a human reviewer and the error is
/// logged against the provider by name.
/// </summary>
public sealed class ClinicalCriteriaConnectorMissingException(string providerId)
    : InvalidOperationException(
        $"Clinical criteria provider '{providerId}' is enabled but no connector is installed. " +
        $"InterQual and MCG content is licensed and is not distributed with Cloud Health Office; " +
        $"see src/engines/CloudHealthOffice.ClinicalCriteria/README.md for the connector contract.")
{
    public string ProviderId { get; } = providerId;
}
