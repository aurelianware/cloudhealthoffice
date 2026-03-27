using Microsoft.AspNetCore.Mvc;
using CapitationService.Models;
using CapitationService.Repositories;

namespace CapitationService.Controllers;

[ApiController]
[Route("api/v1/capitation/contracts")]
[Produces("application/json")]
public class CapitationContractsController : ControllerBase
{
    private readonly ICapitationContractRepository _contractRepository;
    private readonly ILogger<CapitationContractsController> _logger;

    public CapitationContractsController(
        ICapitationContractRepository contractRepository,
        ILogger<CapitationContractsController> logger)
    {
        _contractRepository = contractRepository;
        _logger = logger;
    }

    /// <summary>
    /// Search capitation rate configs (alias: /api/v1/capitation/rate-configs)
    /// </summary>
    [HttpGet]
    [HttpGet("/api/v1/capitation/rate-configs")]
    [ProducesResponseType(typeof(IEnumerable<CapitationContract>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<CapitationContract>>> SearchContracts(
        [FromQuery] string? npi = null,
        [FromQuery] LineOfBusiness? lob = null,
        [FromQuery] CapitationRateConfigStatus? status = null,
        [FromQuery] ContractType? type = null,
        [FromQuery] string? planId = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        if (!string.IsNullOrEmpty(planId))
        {
            var contracts = await _contractRepository.GetByPlanIdAsync(planId);
            return Ok(contracts);
        }

        var results = await _contractRepository.SearchAsync(npi, lob, type, status, page, pageSize);
        return Ok(results);
    }

    /// <summary>
    /// Get capitation contract by ID
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(CapitationContract), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CapitationContract>> GetContractById(string id)
    {
        var contract = await _contractRepository.GetByIdAsync(id);
        if (contract == null)
            return NotFound(new { error = $"Contract {id} not found" });
        return Ok(contract);
    }

    /// <summary>
    /// Get all rate configs for a given parent ProviderContract
    /// </summary>
    [HttpGet("{id}/rate-configs")]
    [ProducesResponseType(typeof(IEnumerable<CapitationContract>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<CapitationContract>>> GetRateConfigsByContractId(string id)
    {
        var results = await _contractRepository.SearchAsync(
            providerNpi: null, lob: null, type: null, status: null, page: 1, pageSize: 1000);
        var filtered = results.Where(c => c.ContractId == id).ToList();
        return Ok(filtered);
    }

    /// <summary>
    /// Create a new capitation rate config
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(CapitationContract), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CapitationContract>> CreateContract([FromBody] CapitationContract contract)
    {
        if (string.IsNullOrEmpty(contract.ContractId))
            return BadRequest(new { error = "ContractId is required — must reference a parent ProviderContract" });

        _logger.LogInformation("Creating capitation rate config for contract {ContractId}, provider {NPI}",
            contract.ContractId, SanitizeForLog(contract.ProviderNPI));

        contract.Status = CapitationRateConfigStatus.Draft;
        var created = await _contractRepository.CreateAsync(contract);

        return CreatedAtAction(nameof(GetContractById), new { id = created.Id }, created);
    }

    /// <summary>
    /// Update a capitation contract
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(CapitationContract), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CapitationContract>> UpdateContract(string id, [FromBody] CapitationContract contract)
    {
        var existing = await _contractRepository.GetByIdAsync(id);
        if (existing == null)
            return NotFound(new { error = $"Contract {id} not found" });

        contract.Id = id;
        contract.TenantId = existing.TenantId;
        contract.CreatedAt = existing.CreatedAt;
        contract.CreatedBy = existing.CreatedBy;

        var updated = await _contractRepository.UpdateAsync(contract);
        return Ok(updated);
    }

    /// <summary>
    /// Activate a draft contract
    /// </summary>
    [HttpPut("{id}/activate")]
    [ProducesResponseType(typeof(CapitationContract), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CapitationContract>> ActivateContract(string id)
    {
        var contract = await _contractRepository.GetByIdAsync(id);
        if (contract == null)
            return NotFound(new { error = $"Contract {id} not found" });

        if (contract.Status != CapitationRateConfigStatus.Draft && contract.Status != CapitationRateConfigStatus.Suspended)
            return BadRequest(new { error = $"Can only activate Draft or Suspended contracts, current: {contract.Status}" });

        contract.Status = CapitationRateConfigStatus.Active;
        _logger.LogInformation("Activated capitation contract {ContractNumber}", contract.ContractNumber);

        var updated = await _contractRepository.UpdateAsync(contract);
        return Ok(updated);
    }

    /// <summary>
    /// Terminate a capitation contract
    /// </summary>
    [HttpPut("{id}/terminate")]
    [ProducesResponseType(typeof(CapitationContract), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CapitationContract>> TerminateContract(string id, [FromBody] TerminateContractRequest request)
    {
        var contract = await _contractRepository.GetByIdAsync(id);
        if (contract == null)
            return NotFound(new { error = $"Contract {id} not found" });

        if (contract.Status == CapitationRateConfigStatus.Terminated)
            return BadRequest(new { error = "Contract is already terminated" });

        contract.Status = CapitationRateConfigStatus.Terminated;
        contract.TerminationDate = request.TerminationDate ?? DateTime.UtcNow;
        _logger.LogInformation("Terminated capitation contract {ContractNumber}: {Reason}",
            contract.ContractNumber, SanitizeForLog(request.Reason));

        var updated = await _contractRepository.UpdateAsync(contract);
        return Ok(updated);
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

public class TerminateContractRequest
{
    public string Reason { get; set; } = string.Empty;
    public DateTime? TerminationDate { get; set; }
}
