using Microsoft.AspNetCore.Mvc;
using CapitationService.Models;
using CapitationService.Repositories;
using CapitationService.Services;

namespace CapitationService.Controllers;

[ApiController]
[Route("api/v1/capitation/statements")]
[Produces("application/json")]
public class CapitationStatementsController : ControllerBase
{
    private readonly ICapitationRunService _runService;
    private readonly ICapitationStatementRepository _statementRepository;
    private readonly ICapitationContractRepository _contractRepository;
    private readonly ICapitationEraService _eraService;
    private readonly ILogger<CapitationStatementsController> _logger;

    public CapitationStatementsController(
        ICapitationRunService runService,
        ICapitationStatementRepository statementRepository,
        ICapitationContractRepository contractRepository,
        ICapitationEraService eraService,
        ILogger<CapitationStatementsController> logger)
    {
        _runService = runService;
        _statementRepository = statementRepository;
        _contractRepository = contractRepository;
        _eraService = eraService;
        _logger = logger;
    }

    /// <summary>
    /// Search capitation statements with optional filters
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<CapitationStatement>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<CapitationStatement>>> SearchStatements(
        [FromQuery] string? npi = null,
        [FromQuery] DateTime? periodFrom = null,
        [FromQuery] DateTime? periodTo = null,
        [FromQuery] CapitationStatementStatus? status = null)
    {
        if (!string.IsNullOrEmpty(npi))
        {
            var statements = await _statementRepository.GetByProviderNpiAsync(npi, periodFrom, periodTo);
            return Ok(statements);
        }

        if (status.HasValue)
        {
            var statements = await _statementRepository.GetByStatusAsync(status.Value);
            return Ok(statements);
        }

        // Period-only queries without NPI or status are not supported
        if (periodFrom.HasValue || periodTo.HasValue)
        {
            return BadRequest(new
            {
                error = "When filtering by period range, at least one of 'npi' or 'status' must be provided."
            });
        }

        // No filters: return Generated statements as default view
        var all = await _statementRepository.GetByStatusAsync(CapitationStatementStatus.Generated);
        return Ok(all);
    }

    /// <summary>
    /// Get capitation statement by ID (includes line items, adjustments)
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(CapitationStatement), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CapitationStatement>> GetStatementById(string id)
    {
        var statement = await _statementRepository.GetByIdAsync(id);
        if (statement == null)
            return NotFound(new { error = $"Statement {id} not found" });
        return Ok(statement);
    }

    /// <summary>
    /// Approve a capitation statement for payment
    /// </summary>
    [HttpPut("{id}/approve")]
    [ProducesResponseType(typeof(CapitationStatement), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CapitationStatement>> ApproveStatement(string id)
    {
        try
        {
            var statement = await _runService.ApproveStatementAsync(id);
            return Ok(statement);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Void a capitation statement
    /// </summary>
    [HttpPut("{id}/void")]
    [ProducesResponseType(typeof(CapitationStatement), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CapitationStatement>> VoidStatement(string id, [FromBody] ReasonRequest request)
    {
        try
        {
            var statement = await _runService.VoidStatementAsync(id, request.Reason);
            return Ok(statement);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Place a capitation statement on hold
    /// </summary>
    [HttpPut("{id}/hold")]
    [ProducesResponseType(typeof(CapitationStatement), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CapitationStatement>> HoldStatement(string id, [FromBody] ReasonRequest request)
    {
        try
        {
            var statement = await _runService.HoldStatementAsync(id, request.Reason);
            return Ok(statement);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get capitation period summary (totals by LOB and contract type)
    /// </summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(CapitationPeriodSummary), StatusCodes.Status200OK)]
    public async Task<ActionResult<CapitationPeriodSummary>> GetCapitationSummary([FromQuery] DateTime period)
    {
        var summary = await _runService.GetCapitationSummaryAsync(period);
        return Ok(summary);
    }

    /// <summary>
    /// Get all unpaid capitation statements
    /// </summary>
    [HttpGet("unpaid")]
    [ProducesResponseType(typeof(IEnumerable<CapitationStatement>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<CapitationStatement>>> GetUnpaidStatements()
    {
        var statements = await _statementRepository.GetUnpaidStatementsAsync();
        return Ok(statements);
    }

    /// <summary>
    /// Generate an X12 835 Electronic Remittance Advice for a capitation statement.
    /// Returns the raw EDI string as text/plain.
    /// </summary>
    [HttpPost("{id}/era")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK, "text/plain")]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> GenerateEra(string id, [FromBody] CapitationEraTradingPartnerInfo? tradingPartner = null)
    {
        var statement = await _statementRepository.GetByIdAsync(id);
        if (statement == null)
            return NotFound(new { error = $"Statement {id} not found" });

        var contract = await _contractRepository.GetByIdAsync(statement.ContractId);
        if (contract == null)
            return BadRequest(new { error = $"Contract {statement.ContractId} not found for statement" });

        var tp = tradingPartner ?? new CapitationEraTradingPartnerInfo
        {
            PayerName = "Cloud Health Office",
            PayerId = "CHO"
        };

        var edi = _eraService.Generate835ForStatement(statement, contract, tp);

        return Content(edi, "text/plain");
    }
}

public class ReasonRequest
{
    public string Reason { get; set; } = string.Empty;
}
