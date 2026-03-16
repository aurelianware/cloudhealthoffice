using CloudHealthOffice.ClaimsScrubEngine.Models;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.ClaimsScrubEngine.Services;

/// <summary>
/// Determines claim routing after validation:
///   clean  → adjudication pipeline
///   errors → work-queue (claims-errors, high priority)
///   warnings only → work-queue (claims-warnings, medium priority)
/// </summary>
public interface IClaimRoutingService
{
    /// <summary>
    /// Validate a claim and return the routing decision.
    /// This is the main entry point for the claims scrub step in the adjudication pipeline.
    /// </summary>
    Task<ClaimsScrubResponse> ScrubAndRouteAsync(
        ClaimsScrubRequest request,
        CancellationToken ct = default);
}

public sealed class ClaimRoutingService : IClaimRoutingService
{
    private readonly IValidationRuleEngine _engine;
    private readonly ILogger<ClaimRoutingService> _logger;

    public ClaimRoutingService(
        IValidationRuleEngine engine,
        ILogger<ClaimRoutingService> logger)
    {
        _engine = engine;
        _logger = logger;
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }

    public async Task<ClaimsScrubResponse> ScrubAndRouteAsync(
        ClaimsScrubRequest request,
        CancellationToken ct = default)
    {
        var options = (request.SkipRules is not null || request.OnlyRules is not null)
            ? new ClaimValidationOptions
            {
                SkipRules = request.SkipRules,
                OnlyRules = request.OnlyRules,
            }
            : null;

        var result = await _engine.ValidateClaimAsync(request.Claim, options, ct);

        _logger.LogInformation(
            "Claim {ClaimId} scrub complete: status={Status}, errors={Errors}, warnings={Warnings}, route={Destination}",
            SanitizeForLog(result.ClaimId), result.Status, result.ErrorCount, result.WarningCount,
            result.Routing.Destination);

        return new ClaimsScrubResponse
        {
            Result = result,
            Timestamp = DateTime.UtcNow.ToString("o"),
        };
    }
}
