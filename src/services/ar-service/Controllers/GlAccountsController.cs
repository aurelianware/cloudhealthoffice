using Microsoft.AspNetCore.Mvc;
using ArService.Models;
using ArService.Repositories;

namespace ArService.Controllers;

[ApiController]
[Route("api/v1/ar/accounts")]
[Produces("application/json")]
public class GlAccountsController : ControllerBase
{
    private readonly IGlAccountRepository _accountRepository;
    private readonly ILogger<GlAccountsController> _logger;

    public GlAccountsController(
        IGlAccountRepository accountRepository,
        ILogger<GlAccountsController> logger)
    {
        _accountRepository = accountRepository;
        _logger = logger;
    }

    /// <summary>
    /// Search GL accounts with optional filters
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<GlAccount>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<GlAccount>>> SearchAccounts(
        [FromQuery] GlAccountType? accountType = null,
        [FromQuery] LineOfBusiness? lob = null,
        [FromQuery] GlAccountStatus? status = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var results = await _accountRepository.SearchAsync(accountType, lob, status, page, pageSize);
        return Ok(results);
    }

    /// <summary>
    /// Get GL account by ID
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(GlAccount), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GlAccount>> GetAccountById(string id)
    {
        var account = await _accountRepository.GetByIdAsync(id);
        if (account == null)
            return NotFound(new { error = $"GL account {id} not found" });
        return Ok(account);
    }

    /// <summary>
    /// Create a new GL account
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(GlAccount), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<GlAccount>> CreateAccount([FromBody] GlAccount account)
    {
        _logger.LogInformation("Creating GL account {AccountNumber} ({AccountName})",
            SanitizeForLog(account.AccountNumber), SanitizeForLog(account.AccountName));

        var created = await _accountRepository.CreateAsync(account);
        return CreatedAtAction(nameof(GetAccountById), new { id = created.Id }, created);
    }

    /// <summary>
    /// Update a GL account
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(GlAccount), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GlAccount>> UpdateAccount(string id, [FromBody] GlAccount account)
    {
        var existing = await _accountRepository.GetByIdAsync(id);
        if (existing == null)
            return NotFound(new { error = $"GL account {id} not found" });

        account.Id = id;
        account.TenantId = existing.TenantId;
        account.CreatedAt = existing.CreatedAt;
        account.CreatedBy = existing.CreatedBy;

        var updated = await _accountRepository.UpdateAsync(account);
        return Ok(updated);
    }

    /// <summary>
    /// Deactivate a GL account (set Status = Inactive)
    /// </summary>
    [HttpPut("{id}/deactivate")]
    [ProducesResponseType(typeof(GlAccount), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GlAccount>> DeactivateAccount(string id)
    {
        var account = await _accountRepository.GetByIdAsync(id);
        if (account == null)
            return NotFound(new { error = $"GL account {id} not found" });

        account.Status = GlAccountStatus.Inactive;
        _logger.LogInformation("Deactivated GL account {AccountNumber}", account.AccountNumber);

        var updated = await _accountRepository.UpdateAsync(account);
        return Ok(updated);
    }

    /// <summary>
    /// Activate a GL account (set Status = Active)
    /// </summary>
    [HttpPut("{id}/activate")]
    [ProducesResponseType(typeof(GlAccount), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GlAccount>> ActivateAccount(string id)
    {
        var account = await _accountRepository.GetByIdAsync(id);
        if (account == null)
            return NotFound(new { error = $"GL account {id} not found" });

        account.Status = GlAccountStatus.Active;
        _logger.LogInformation("Activated GL account {AccountNumber}", account.AccountNumber);

        var updated = await _accountRepository.UpdateAsync(account);
        return Ok(updated);
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
