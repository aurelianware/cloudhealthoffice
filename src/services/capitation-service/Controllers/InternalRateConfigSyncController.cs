using CapitationService.Models;
using CapitationService.Repositories;
using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace CapitationService.Controllers;

/// <summary>
/// Where provider-contracts-service pushes a contract's denormalized fields
/// (contract number, NPI, provider name, line of business) to that contract's
/// capitation rate configs: the receiving end of
/// <c>PUT /api/v1/contracts/{id}/sync-children</c>.
///
/// Only provider-contracts-service's own service token is admitted; a user's
/// token is refused whatever its permissions, so the copies change only from
/// the contract that owns them. The tenant comes from that token.
/// </summary>
[ApiController]
[Route("api/v1/internal/capitation/contracts/{contractId}/parent-fields")]
[Produces("application/json")]
[RequireServiceClient(ProviderContractsClientId)]
public sealed class InternalRateConfigSyncController : ControllerBase
{
    public const string ProviderContractsClientId = "provider-contracts-service";

    private readonly ICapitationContractRepository _repository;
    private readonly ICurrentActor _actor;
    private readonly ILogger<InternalRateConfigSyncController> _logger;

    public InternalRateConfigSyncController(
        ICapitationContractRepository repository,
        ICurrentActor actor,
        ILogger<InternalRateConfigSyncController> logger)
    {
        _repository = repository;
        _actor = actor;
        _logger = logger;
    }

    [HttpPut]
    [ProducesResponseType(typeof(ParentFieldsSyncResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ParentFieldsSyncResult>> Sync(string contractId, [FromBody] ParentContractFields fields)
    {
        if (string.IsNullOrWhiteSpace(fields.ContractNumber) || string.IsNullOrWhiteSpace(fields.ProviderNpi))
            return BadRequest(new { error = "ContractNumber and ProviderNpi are required" });
        if (fields.ProviderNpi.Length > 10 || fields.ContractNumber.Length > 50 || fields.ProviderName.Length > 300)
            return BadRequest(new { error = "A parent field is longer than a rate config can hold" });

        fields.SyncedBy = _actor.UserId;
        var result = await _repository.SyncParentFieldsAsync(contractId, fields, DateTime.UtcNow);
        _logger.LogInformation("Parent fields of contract {ContractId} applied to {Count} rate configs",
            LogSanitizer.Sanitize(contractId), result.RateConfigsUpdated);
        return Ok(result);
    }
}
