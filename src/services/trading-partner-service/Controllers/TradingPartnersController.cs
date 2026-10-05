using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using CloudHealthOffice.TradingPartnerService.Models;
using CloudHealthOffice.TradingPartnerService.Services;

namespace CloudHealthOffice.TradingPartnerService.Controllers;

/// <summary>
/// Trading partner (EDI counterparty) configuration for the caller's tenant.
/// <para>
/// Every caller presents a CHO token. The tenant comes from that token. The
/// <c>{tenantId}</c> route segments are kept for existing callers
/// (payment-service's NPI lookup) but never select a tenant: a path tenant that
/// differs from the token's tenant is refused with 403 before any data is read.
/// </para>
/// <para>
/// Permissions: reads need trading-partners:read (the default; service tokens
/// satisfy it). Writes, including the connection test, need settings:manage
/// (no trading-partners:write permission exists). The acting user recorded on a
/// write is the token subject.
/// </para>
/// <para>
/// Credentials are never returned: responses are built from
/// <see cref="TradingPartnerView"/>, and a literal credential in a request body
/// is refused (see <see cref="TradingPartnerSecrets"/>).
/// </para>
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class TradingPartnersController : ControllerBase
{
    private readonly ITradingPartnerRepository _repository;
    private readonly PathResolver _pathResolver;
    private readonly ICurrentActor _actor;
    private readonly JsonSerializerOptions _json;
    private readonly ILogger<TradingPartnersController> _logger;

    public TradingPartnersController(
        ITradingPartnerRepository repository,
        PathResolver pathResolver,
        ICurrentActor actor,
        IOptions<Microsoft.AspNetCore.Mvc.JsonOptions> jsonOptions,
        ILogger<TradingPartnersController> logger)
    {
        _repository = repository;
        _pathResolver = pathResolver;
        _actor = actor;
        _json = jsonOptions.Value.JsonSerializerOptions;
        _logger = logger;
    }

    /// <summary>
    /// Get all trading partners for the caller's tenant (the path tenant must match the token).
    /// </summary>
    [HttpGet("tenant/{tenantId}")]
    public async Task<ActionResult<IEnumerable<TradingPartnerView>>> GetByTenant(string tenantId)
    {
        if (PathTenantMismatch(tenantId) is { } refused) return refused;

        var partners = await _repository.GetByTenantAsync(_actor.TenantId);
        return Ok(partners.Select(TradingPartnerView.From).ToList());
    }

    /// <summary>
    /// Get specific trading partner configuration
    /// </summary>
    [HttpGet("{tenantId}/{tradingPartnerId}/{environment}")]
    public async Task<ActionResult<TradingPartnerView>> Get(string tenantId, string tradingPartnerId, string environment)
    {
        if (PathTenantMismatch(tenantId) is { } refused) return refused;

        var partner = await _repository.GetAsync(_actor.TenantId, tradingPartnerId, environment);

        if (partner == null)
        {
            return NotFound(new {
                message = $"Trading partner not found: {tradingPartnerId}/{environment}"
            });
        }

        return Ok(TradingPartnerView.From(partner));
    }

    /// <summary>
    /// Resolve the trading partner that handles ERAs for a given
    /// billing-provider NPI within the caller's tenant + environment. Consumed by
    /// payment-service during PaymentRun and ReversalRun execution (5.10) to group
    /// claims into per-trading-partner 835 envelopes.
    ///
    /// Returns 404 when no trading partner declares the NPI in its
    /// <c>BillingProviderNpis</c> list. Multiple partners declaring the
    /// same NPI is an operator-configuration error; the first match
    /// (insertion order from <see cref="ITradingPartnerRepository.GetByTenantAsync"/>)
    /// wins.
    ///
    /// <para>
    /// payment-service forwards the caller's token (or, with no caller, mints a
    /// service token for the run's tenant). Run execution is done by a
    /// FinanceApprover, who holds payments:read but not trading-partners:read, so
    /// this lookup admits either. The response is the routing subset only
    /// (<see cref="TradingPartnerRoutingView"/>). The route keeps its
    /// <c>{tenantId}</c> segment for compatibility; it must equal the token tenant.
    /// </para>
    /// </summary>
    [HttpGet("by-npi/{tenantId}/{npi}/{environment}")]
    [RequirePermission("trading-partners:read,payments:read")]
    public async Task<ActionResult<TradingPartnerRoutingView>> GetByBillingProviderNpi(
        string tenantId,
        string npi,
        string environment)
    {
        if (PathTenantMismatch(tenantId) is { } refused) return refused;

        var partners = await _repository.GetByTenantAsync(_actor.TenantId);
        var match = partners.FirstOrDefault(p =>
            string.Equals(p.Environment, environment, StringComparison.OrdinalIgnoreCase)
            && p.BillingProviderNpis.Contains(npi));

        if (match == null)
        {
            return NotFound(new
            {
                message = $"No trading partner found for NPI {npi} ({environment})"
            });
        }

        return Ok(TradingPartnerRoutingView.From(match));
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
        if (PathTenantMismatch(tenantId) is { } refused) return refused;
        tenantId = _actor.TenantId;

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
        if (PathTenantMismatch(tenantId) is { } refused) return refused;
        tenantId = _actor.TenantId;

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
        if (PathTenantMismatch(tenantId) is { } refused) return refused;
        tenantId = _actor.TenantId;

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
    /// Create a trading partner configuration in the caller's tenant (settings:manage).
    /// The body's tenantId, id and actor fields are ignored.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<TradingPartnerView>> Create([FromBody] JsonElement body)
    {
        var (request, invalid) = ReadRequest(body);
        if (invalid is not null) return invalid;

        var tenantId = _actor.TenantId;
        if (string.IsNullOrWhiteSpace(request!.TradingPartnerId) || string.IsNullOrWhiteSpace(request.Environment))
            return BadRequest(new { message = "tradingPartnerId and environment are required." });

        if (InvalidSecretReferences(request.SftpConfig, tenantId) is { } badRef) return badRef;

        var now = DateTime.UtcNow;
        var partner = new TradingPartner
        {
            // Unambiguous per (tenant, partner id, environment); see TradingPartnerIds.
            Id = TradingPartnerIds.For(tenantId, request.TradingPartnerId, request.Environment),
            TenantId = tenantId,
            TradingPartnerId = request.TradingPartnerId,
            Environment = request.Environment,
            CreatedAt = now,
            CreatedBy = _actor.UserId,
            UpdatedAt = now,
            UpdatedBy = _actor.UserId
        };
        Apply(request, partner, existingSftp: null);

        TradingPartner created;
        try
        {
            created = await _repository.CreateAsync(partner);
        }
        catch (DuplicateTradingPartnerException)
        {
            return Conflict(new
            {
                message = $"Trading partner {request.TradingPartnerId} ({request.Environment}) already exists. Use PUT to change it."
            });
        }

        _logger.LogInformation(
            "AUDIT trading partner created: {TenantId}/{TradingPartnerId}/{Environment} by {Actor}",
            SanitizeForLog(tenantId), SanitizeForLog(partner.TradingPartnerId), SanitizeForLog(partner.Environment),
            SanitizeForLog(_actor.UserId));

        return CreatedAtAction(
            nameof(Get),
            new { tenantId, tradingPartnerId = partner.TradingPartnerId, environment = partner.Environment },
            TradingPartnerView.From(created));
    }

    /// <summary>
    /// Update trading partner configuration (settings:manage). A Key Vault reference
    /// left out of the body (null) keeps the stored one; an empty string removes it.
    /// </summary>
    [HttpPut("{tenantId}/{tradingPartnerId}/{environment}")]
    public async Task<ActionResult<TradingPartnerView>> Update(
        string tenantId,
        string tradingPartnerId,
        string environment,
        [FromBody] JsonElement body)
    {
        if (PathTenantMismatch(tenantId) is { } refused) return refused;
        tenantId = _actor.TenantId;

        var (request, invalid) = ReadRequest(body);
        if (invalid is not null) return invalid;
        if (InvalidSecretReferences(request!.SftpConfig, tenantId) is { } badRef) return badRef;

        var existing = await _repository.GetAsync(tenantId, tradingPartnerId, environment);
        if (existing == null)
            return NotFound();

        // Identity, creation stamps and test/transmission history come from the stored record.
        var existingSftp = existing.SftpConfig;
        Apply(request, existing, existingSftp);
        existing.UpdatedAt = DateTime.UtcNow;
        existing.UpdatedBy = _actor.UserId;

        var updated = await _repository.UpdateAsync(existing);

        _logger.LogInformation(
            "AUDIT trading partner updated: {TenantId}/{TradingPartnerId}/{Environment} by {Actor}",
            SanitizeForLog(tenantId), SanitizeForLog(tradingPartnerId), SanitizeForLog(environment),
            SanitizeForLog(_actor.UserId));

        return Ok(TradingPartnerView.From(updated));
    }

    /// <summary>
    /// Delete trading partner configuration (settings:manage)
    /// </summary>
    [HttpDelete("{tenantId}/{tradingPartnerId}/{environment}")]
    public async Task<ActionResult> Delete(string tenantId, string tradingPartnerId, string environment)
    {
        if (PathTenantMismatch(tenantId) is { } refused) return refused;
        tenantId = _actor.TenantId;

        var partner = await _repository.GetAsync(tenantId, tradingPartnerId, environment);
        if (partner == null)
            return NotFound();

        await _repository.DeleteAsync(partner.Id, tenantId);

        _logger.LogWarning(
            "AUDIT trading partner deleted: {TenantId}/{TradingPartnerId}/{Environment} by {Actor}",
            SanitizeForLog(tenantId), SanitizeForLog(tradingPartnerId), SanitizeForLog(environment),
            SanitizeForLog(_actor.UserId));

        return NoContent();
    }

    /// <summary>
    /// Test trading partner connectivity (a write: it records the test; settings:manage)
    /// </summary>
    [HttpPost("{tenantId}/{tradingPartnerId}/{environment}/test")]
    public async Task<ActionResult<object>> TestConnection(
        string tenantId,
        string tradingPartnerId,
        string environment)
    {
        if (PathTenantMismatch(tenantId) is { } refused) return refused;
        tenantId = _actor.TenantId;

        var partner = await _repository.GetAsync(tenantId, tradingPartnerId, environment);
        if (partner == null)
            return NotFound();

        // Update last tested timestamp and who started the test
        partner.LastTestedAt = DateTime.UtcNow;
        partner.LastTestedBy = _actor.UserId;
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
    /// Health check endpoint (no tenant data)
    /// </summary>
    [AllowAnonymous]
    [HttpGet("/health")]
    public IActionResult Health()
    {
        return Ok(new { status = "healthy", service = "trading-partner-service" });
    }

    /// <summary>
    /// Refuses a body carrying a literal credential, then reads it as a
    /// <see cref="TradingPartnerRequest"/> (unknown fields such as tenantId or
    /// createdBy are ignored).
    /// </summary>
    private (TradingPartnerRequest? Request, ActionResult? Invalid) ReadRequest(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
            return (null, BadRequest(new { message = "A JSON object body is required." }));

        if (TradingPartnerSecrets.FindLiteralCredential(body) is { } path)
        {
            _logger.LogWarning(
                "Trading partner write refused: body carries a credential at {Path} (tenant {TenantId}, subject {Actor})",
                SanitizeForLog(path), SanitizeForLog(_actor.TenantId), SanitizeForLog(_actor.UserId));
            return (null, BadRequest(new
            {
                message = $"Credentials are not accepted in trading partner configuration ({path}). " +
                          "Store the credential in Key Vault and send its name in sftpConfig.passwordSecretRef " +
                          "or sftpConfig.privateKeySecretRef."
            }));
        }

        try
        {
            var request = body.Deserialize<TradingPartnerRequest>(_json);
            return request is null
                ? (null, BadRequest(new { message = "A JSON object body is required." }))
                : (request, null);
        }
        catch (JsonException ex)
        {
            return (null, BadRequest(new { message = $"Invalid trading partner body: {ex.Message}" }));
        }
    }

    private ActionResult? InvalidSecretReferences(SftpConfig? sftp, string tenantId)
    {
        if (sftp is null) return null;

        var reason = TradingPartnerSecrets.ValidateReference(sftp.PasswordSecretRef, tenantId, "sftpConfig.passwordSecretRef")
                     ?? TradingPartnerSecrets.ValidateReference(sftp.PrivateKeySecretRef, tenantId, "sftpConfig.privateKeySecretRef");
        return reason is null ? null : BadRequest(new { message = reason });
    }

    /// <summary>
    /// Copies the client-managed fields onto the stored record. Key Vault references:
    /// null keeps the stored value, an empty string clears it.
    /// </summary>
    private static void Apply(TradingPartnerRequest request, TradingPartner target, SftpConfig? existingSftp)
    {
        target.PartnerName = request.PartnerName;
        target.PartnerType = request.PartnerType;
        target.X12Config = request.X12Config;
        target.BlobConfig = request.BlobConfig;
        target.TransactionTypes = request.TransactionTypes ?? new();
        target.BillingProviderNpis = request.BillingProviderNpis ?? new();
        target.ContactInfo = request.ContactInfo;
        target.BusinessRules = request.BusinessRules;
        if (!string.IsNullOrEmpty(request.Status))
            target.Status = request.Status;

        var sftp = request.SftpConfig;
        if (sftp is not null)
        {
            sftp.PasswordSecretRef = Merge(sftp.PasswordSecretRef, existingSftp?.PasswordSecretRef);
            sftp.PrivateKeySecretRef = Merge(sftp.PrivateKeySecretRef, existingSftp?.PrivateKeySecretRef);
        }
        target.SftpConfig = sftp;

        static string? Merge(string? requested, string? stored)
            => requested is null ? stored : requested.Length == 0 ? null : requested;
    }

    /// <summary>
    /// The route tenant is only accepted as an echo of the token's tenant.
    /// </summary>
    private ObjectResult? PathTenantMismatch(string pathTenantId)
    {
        if (string.Equals(pathTenantId, _actor.TenantId, StringComparison.Ordinal))
            return null;

        _logger.LogWarning(
            "Trading partner request refused: path tenant {PathTenant} does not match the authenticated tenant {TokenTenant} (subject {Subject})",
            SanitizeForLog(pathTenantId), SanitizeForLog(_actor.TenantId), SanitizeForLog(_actor.UserId));
        return StatusCode(StatusCodes.Status403Forbidden,
            new { message = "The tenant in the path does not match the authenticated tenant." });
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
