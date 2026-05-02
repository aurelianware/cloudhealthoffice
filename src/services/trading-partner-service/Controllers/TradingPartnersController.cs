using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using CloudHealthOffice.TradingPartnerService.Models;
using CloudHealthOffice.TradingPartnerService.Services;

namespace CloudHealthOffice.TradingPartnerService.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class TradingPartnersController : ControllerBase
{
    private readonly ITradingPartnerRepository _repository;
    private readonly PathResolver _pathResolver;
    private readonly ILogger<TradingPartnersController> _logger;

    public TradingPartnersController(
        ITradingPartnerRepository repository,
        PathResolver pathResolver,
        ILogger<TradingPartnersController> logger)
    {
        _repository = repository;
        _pathResolver = pathResolver;
        _logger = logger;
    }

    /// <summary>
    /// Get all trading partners for a tenant
    /// </summary>
    [HttpGet("tenant/{tenantId}")]
    public async Task<ActionResult<IEnumerable<TradingPartner>>> GetByTenant(string tenantId)
    {
        var partners = await _repository.GetByTenantAsync(tenantId);
        return Ok(partners);
    }

    /// <summary>
    /// Get specific trading partner configuration
    /// </summary>
    [HttpGet("{tenantId}/{tradingPartnerId}/{environment}")]
    public async Task<ActionResult<TradingPartner>> Get(string tenantId, string tradingPartnerId, string environment)
    {
        var partner = await _repository.GetAsync(tenantId, tradingPartnerId, environment);

        if (partner == null)
        {
            return NotFound(new {
                message = $"Trading partner not found: {tenantId}/{tradingPartnerId}/{environment}"
            });
        }

        return Ok(partner);
    }

    /// <summary>
    /// Resolve the trading partner that handles ERAs for a given
    /// billing-provider NPI within a tenant + environment. Consumed by
    /// payment-service during PaymentRun execution (5.10) to group
    /// claims into per-trading-partner 835 envelopes.
    ///
    /// Returns 404 when no trading partner declares the NPI in its
    /// <c>BillingProviderNpis</c> list. Multiple partners declaring the
    /// same NPI is an operator-configuration error; the first match
    /// (insertion order from <see cref="ITradingPartnerRepository.GetByTenantAsync"/>)
    /// wins.
    ///
    /// <para>
    /// Service-to-service surface — mirrors claims-service's
    /// <c>GET /api/claims/search</c> by being unauthenticated at the
    /// endpoint level (cluster-internal isolation is the auth boundary).
    /// </para>
    /// </summary>
    [AllowAnonymous]
    [HttpGet("by-npi/{tenantId}/{npi}/{environment}")]
    public async Task<ActionResult<TradingPartner>> GetByBillingProviderNpi(
        string tenantId,
        string npi,
        string environment)
    {
        var partners = await _repository.GetByTenantAsync(tenantId);
        var match = partners.FirstOrDefault(p =>
            string.Equals(p.Environment, environment, StringComparison.OrdinalIgnoreCase)
            && p.BillingProviderNpis.Contains(npi));

        if (match == null)
        {
            return NotFound(new
            {
                message = $"No trading partner found for NPI {npi} in tenant {tenantId} ({environment})"
            });
        }

        return Ok(match);
    }

    /// <summary>
    /// Resolve SFTP path for specific transaction type
    /// </summary>
    [HttpGet("{tenantId}/{tradingPartnerId}/{environment}/sftp/{direction}/{transactionType}")]
    public async Task<ActionResult<object>> GetSftpPath(
        string tenantId, 
        string tradingPartnerId, 
        string environment,
        string direction,
        string transactionType)
    {
        var partner = await _repository.GetAsync(tenantId, tradingPartnerId, environment);
        
        if (partner == null)
            return NotFound();

        try
        {
            var path = _pathResolver.ResolveSftpPath(partner, direction, transactionType);
            var fileName = $"test-{transactionType}-{DateTime.UtcNow:yyyyMMddHHmmss}.edi";
            var fullPath = _pathResolver.BuildSftpFilePath(partner, direction, transactionType, fileName);

            return Ok(new
            {
                tenantId,
                tradingPartnerId,
                environment,
                direction,
                transactionType,
                basePath = path,
                exampleFullPath = fullPath,
                sftpHost = partner.SftpConfig?.Host,
                sftpUsername = partner.SftpConfig?.Username
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Resolve blob storage path for specific transaction type
    /// </summary>
    [HttpGet("{tenantId}/{tradingPartnerId}/{environment}/blob/{stage}/{transactionType}")]
    public async Task<ActionResult<object>> GetBlobPath(
        string tenantId,
        string tradingPartnerId,
        string environment,
        string stage,
        string transactionType)
    {
        var partner = await _repository.GetAsync(tenantId, tradingPartnerId, environment);

        if (partner == null)
            return NotFound();

        try
        {
            var path = _pathResolver.ResolveBlobPath(partner, stage, transactionType);
            var container = _pathResolver.GetBlobContainer(partner);
            var retention = _pathResolver.GetRetentionDays(partner, stage);
            var fileName = $"test-{transactionType}-{DateTime.UtcNow:yyyyMMddHHmmss}.edi";
            var fullPath = _pathResolver.BuildBlobFilePath(partner, stage, transactionType, fileName);

            return Ok(new
            {
                tenantId,
                tradingPartnerId,
                environment,
                stage,
                transactionType,
                containerName = container,
                basePath = path,
                exampleFullPath = fullPath,
                retentionDays = retention
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Get X12 configuration for trading partner
    /// </summary>
    [HttpGet("{tenantId}/{tradingPartnerId}/{environment}/x12")]
    public async Task<ActionResult<object>> GetX12Config(
        string tenantId,
        string tradingPartnerId,
        string environment)
    {
        var partner = await _repository.GetAsync(tenantId, tradingPartnerId, environment);

        if (partner == null)
            return NotFound();

        return Ok(new
        {
            tenantId,
            tradingPartnerId,
            environment,
            x12SenderId = partner.X12Config?.SenderId,
            x12ReceiverId = partner.X12Config?.ReceiverId,
            isaQualifier = partner.X12Config?.IsaQualifier,
            testIndicator = partner.X12Config?.TestIndicator
        });
    }

    /// <summary>
    /// Create new trading partner configuration
    /// </summary>
    [HttpPost]
    [Authorize(Policy = "RequireAdministratorRole")]
    public async Task<ActionResult<TradingPartner>> Create([FromBody] TradingPartner partner)
    {
        partner.Id = $"{partner.TradingPartnerId}-{partner.TenantId}-{partner.Environment}";
        partner.CreatedAt = DateTime.UtcNow;

        var created = await _repository.CreateAsync(partner);
        
        _logger.LogInformation(
            "Created trading partner: {TenantId}/{TradingPartnerId}/{Environment}",
            SanitizeForLog(partner.TenantId), SanitizeForLog(partner.TradingPartnerId), SanitizeForLog(partner.Environment));

        return CreatedAtAction(
            nameof(Get),
            new { tenantId = partner.TenantId, tradingPartnerId = partner.TradingPartnerId, environment = partner.Environment },
            created);
    }

    /// <summary>
    /// Update trading partner configuration
    /// </summary>
    [HttpPut("{tenantId}/{tradingPartnerId}/{environment}")]
    [Authorize(Policy = "RequireAdministratorRole")]
    public async Task<ActionResult<TradingPartner>> Update(
        string tenantId,
        string tradingPartnerId,
        string environment,
        [FromBody] TradingPartner partner)
    {
        var existing = await _repository.GetAsync(tenantId, tradingPartnerId, environment);
        if (existing == null)
            return NotFound();

        partner.Id = existing.Id;
        partner.TenantId = tenantId;
        partner.TradingPartnerId = tradingPartnerId;
        partner.Environment = environment;
        partner.CreatedAt = existing.CreatedAt;

        var updated = await _repository.UpdateAsync(partner);

        _logger.LogInformation(
            "Updated trading partner: {TenantId}/{TradingPartnerId}/{Environment}",
            SanitizeForLog(tenantId), SanitizeForLog(tradingPartnerId), SanitizeForLog(environment));

        return Ok(updated);
    }

    /// <summary>
    /// Delete trading partner configuration
    /// </summary>
    [HttpDelete("{tenantId}/{tradingPartnerId}/{environment}")]
    [Authorize(Policy = "RequireAdministratorRole")]
    public async Task<ActionResult> Delete(string tenantId, string tradingPartnerId, string environment)
    {
        var partner = await _repository.GetAsync(tenantId, tradingPartnerId, environment);
        if (partner == null)
            return NotFound();

        await _repository.DeleteAsync(partner.Id, tenantId);

        _logger.LogWarning(
            "Deleted trading partner: {TenantId}/{TradingPartnerId}/{Environment}",
            SanitizeForLog(tenantId), SanitizeForLog(tradingPartnerId), SanitizeForLog(environment));

        return NoContent();
    }

    /// <summary>
    /// Test trading partner connectivity
    /// </summary>
    [HttpPost("{tenantId}/{tradingPartnerId}/{environment}/test")]
    public async Task<ActionResult<object>> TestConnection(
        string tenantId,
        string tradingPartnerId,
        string environment)
    {
        var partner = await _repository.GetAsync(tenantId, tradingPartnerId, environment);
        if (partner == null)
            return NotFound();

        // Update last tested timestamp
        partner.LastTestedAt = DateTime.UtcNow;
        await _repository.UpdateAsync(partner);

        return Ok(new
        {
            message = "Test initiated",
            tenantId,
            tradingPartnerId,
            environment,
            sftpHost = partner.SftpConfig?.Host,
            status = partner.Status,
            testedAt = partner.LastTestedAt
        });
    }

    /// <summary>
    /// Health check endpoint
    /// </summary>
    [AllowAnonymous]
    [HttpGet("/health")]
    public IActionResult Health()
    {
        return Ok(new { status = "healthy", service = "trading-partner-service" });
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
