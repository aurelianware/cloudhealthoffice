using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Capabilities;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.ReferenceData.Payers;
using Microsoft.AspNetCore.Mvc;
using ProviderEligibilityApi.Contracts;
using ProviderEligibilityApi.Eligibility;
using ProviderEligibilityApi.Security;

namespace ProviderEligibilityApi.Controllers;

/// <summary>
/// Outbound (CHO → payer) eligibility for provider applications. Every request
/// is authenticated and tenant-bound by <see cref="ProviderApiAuthenticationMiddleware"/>.
/// Logs carry tenant, client, payer, outcome and timing only — never member
/// identifiers, names, dates of birth or payer response text.
/// </summary>
[ApiController]
[Route("api/v1")]
[Produces("application/json")]
public sealed class ProviderEligibilityController : ControllerBase
{
    private const int MaxPayerResults = 25;

    private readonly IHealthcareGatewayResolver _resolver;
    private readonly IPayerReferenceService _payers;
    private readonly TimeProvider _time;
    private readonly ILogger<ProviderEligibilityController> _logger;

    public ProviderEligibilityController(
        IHealthcareGatewayResolver resolver,
        IPayerReferenceService payers,
        TimeProvider time,
        ILogger<ProviderEligibilityController> logger)
    {
        _resolver = resolver;
        _payers = payers;
        _time = time;
        _logger = logger;
    }

    [HttpPost("eligibility/check")]
    [ProducesResponseType(typeof(ProviderEligibilityCheckResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProviderEligibilityCheckResponse), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProviderEligibilityCheckResponse), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(typeof(ProviderEligibilityCheckResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Check(
        [FromBody] ProviderEligibilityCheckRequest? request,
        CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var errors = ProviderEligibilityRequestValidator.Validate(request, today);
        if (errors.Count > 0)
        {
            return BadRequest(new ValidationErrorResponse { Fields = errors });
        }

        var tenantId = (string)HttpContext.Items[ProviderApiAuthenticationMiddleware.TenantItemKey]!;
        var client = (string)HttpContext.Items[ProviderApiAuthenticationMiddleware.ClientItemKey]!;
        var gatewayRequest = ProviderEligibilityMapper.ToGatewayRequest(request!, tenantId, today);

        var eligibility = _resolver.ResolveCapability<IEligibilityGateway>();
        var gatewayResponse = await eligibility.CheckEligibilityAsync(gatewayRequest, ct);

        var response = ProviderEligibilityMapper.ToResponse(gatewayResponse, now);
        var statusCode = ProviderEligibilityMapper.ToStatusCode(gatewayResponse);

        _logger.LogInformation(
            "Provider eligibility check for client {Client} tenant {TenantId} payer {PayerId}: " +
            "outcome {Outcome}, category {ErrorCategory}, coverage {CoverageStatus}, " +
            "latency {LatencyMs} ms, correlation {CorrelationId}",
            client,
            tenantId,
            gatewayRequest.PayerId,
            response.Outcome,
            response.ErrorCategory,
            response.CoverageStatus,
            (long)gatewayResponse.Metadata.Latency.TotalMilliseconds,
            gatewayRequest.CorrelationId);

        return StatusCode(statusCode, response);
    }

    /// <summary>
    /// Search the payer directory so staff can link a practice's insurance plan
    /// to a payer this API can route. Search text is not logged.
    /// </summary>
    [HttpGet("payers")]
    [ProducesResponseType(typeof(IReadOnlyList<ProviderPayerSummary>), StatusCodes.Status200OK)]
    public async Task<IActionResult> SearchPayers(
        [FromQuery] string? q,
        [FromQuery] int maxResults = 10,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2 || q.Length > 80)
        {
            return BadRequest(new { error = "Query must be 2 to 80 characters." });
        }

        var payers = await _payers.SearchAsync(new PayerSearchQuery
        {
            Text = q.Trim(),
            Active = true,
            MaxResults = Math.Clamp(maxResults, 1, MaxPayerResults)
        }, ct);

        return Ok(payers.Select(ToSummary).ToList());
    }

    private static ProviderPayerSummary ToSummary(PayerReference payer)
    {
        var eligibility = payer.SupportedTransactions
            .FirstOrDefault(t => t.Transaction == HealthcareTransactionType.Eligibility270271)?.Support
            ?? PayerTransactionSupport.NotSupported;

        var payerIds = payer.ExternalIdentifiers
            .Where(i => i.Type is "primaryPayerId" or "tradingPartnerServiceId")
            .Select(i => i.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ProviderPayerSummary
        {
            Id = payer.Id,
            Name = payer.Name,
            PayerIds = payerIds,
            Eligibility = eligibility.ToString()
        };
    }
}
