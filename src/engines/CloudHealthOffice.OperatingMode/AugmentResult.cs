using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.OperatingMode;

/// <summary>
/// Wraps an engine's output with operating mode context.
/// In Augment mode, this holds both CHO's result and the legacy system's
/// result, along with any discrepancies found between them.
/// In Replace mode, only ChoResult is populated and Authoritative is true.
/// </summary>
public class AugmentResult<T>
{
    /// <summary>
    /// CHO's computed result.
    /// </summary>
    [JsonPropertyName("choResult")]
    public T ChoResult { get; init; } = default!;

    /// <summary>
    /// The legacy system's result (passthrough), if available.
    /// Only populated in Augment mode when a legacy result is provided.
    /// </summary>
    [JsonPropertyName("legacyResult")]
    public T? LegacyResult { get; init; }

    /// <summary>
    /// The operating mode under which this result was produced.
    /// </summary>
    [JsonPropertyName("mode")]
    public EngineOperatingMode Mode { get; init; }

    /// <summary>
    /// Whether CHO's result is the authoritative (official) result.
    /// </summary>
    [JsonPropertyName("authoritative")]
    public bool Authoritative { get; init; }

    /// <summary>
    /// Discrepancies found between CHO and legacy results.
    /// Only populated in Augment mode when both results are present.
    /// </summary>
    [JsonPropertyName("discrepancies")]
    public string[] Discrepancies { get; init; } = [];

    /// <summary>
    /// Timestamp when the comparison was performed.
    /// </summary>
    [JsonPropertyName("comparedAt")]
    public DateTime? ComparedAt { get; init; }
}

/// <summary>
/// Helper for building AugmentResult instances.
/// </summary>
public static class AugmentResult
{
    /// <summary>
    /// Creates a Replace-mode result (CHO is authoritative, no comparison).
    /// </summary>
    public static AugmentResult<T> ForReplace<T>(T choResult)
    {
        return new AugmentResult<T>
        {
            ChoResult = choResult,
            Mode = EngineOperatingMode.Replace,
            Authoritative = true
        };
    }

    /// <summary>
    /// Creates an Augment-mode result with comparison.
    /// </summary>
    public static AugmentResult<T> ForAugment<T>(
        T choResult,
        T? legacyResult,
        string[] discrepancies)
    {
        return new AugmentResult<T>
        {
            ChoResult = choResult,
            LegacyResult = legacyResult,
            Mode = EngineOperatingMode.Augment,
            Authoritative = false,
            Discrepancies = discrepancies,
            ComparedAt = legacyResult is not null ? DateTime.UtcNow : null
        };
    }

    /// <summary>
    /// Creates an Augment-mode result, logging discrepancies.
    /// </summary>
    public static AugmentResult<T> ForAugment<T>(
        T choResult,
        T? legacyResult,
        string[] discrepancies,
        ILogger logger,
        string engineName,
        string tenantId)
    {
        if (discrepancies.Length > 0)
        {
            logger.LogWarning(
                "Operating mode discrepancies for {Engine} (tenant {TenantId}): {DiscrepancyCount} found. {Discrepancies}",
                engineName, tenantId, discrepancies.Length,
                string.Join("; ", discrepancies));
        }
        else if (legacyResult is not null)
        {
            logger.LogInformation(
                "Operating mode comparison for {Engine} (tenant {TenantId}): CHO and legacy results match",
                engineName, tenantId);
        }

        return ForAugment(choResult, legacyResult, discrepancies);
    }
}
