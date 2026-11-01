using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using ProviderContractsService.Models;
using ProviderContractsService.Repositories;
using ProviderContractsService.Services;
using CloudHealthOffice.Infrastructure.Logging;
// The engine declares a ProviderContract of its own (the pricing contract a
// legal contract projects to), so the term type is aliased rather than the
// namespace imported.
using ReimbursementTerm = CloudHealthOffice.FeeScheduleEngine.Models.ReimbursementTerm;

namespace ProviderContractsService.Controllers;

/// <summary>
/// Provider contract management. Every caller presents a CHO token: reads need
/// contracts:read and writes contracts:write (the defaults set in Program.cs).
/// The tenant comes from the token (the repository scopes every query to it),
/// and the acting user is the token subject: CreatedBy / LastUpdatedBy are
/// stamped by the repository, an amendment's ApprovedBy here. Actor and tenant
/// values in a request body are ignored.
///
/// ProviderTin: list and search always return it masked to the last 4 digits.
/// A single-record read (by id or number) returns it in full only to callers
/// who may edit the contract (contracts:write, which ProviderRelations and
/// admins hold) and to service tokens; read-only holders (Finance and
/// ComplianceOfficer through *:read) get it masked.
/// </summary>
[ApiController]
[Route("api/v1/contracts")]
[Produces("application/json")]
public class ProviderContractsController : ControllerBase
{
    /// <summary>Largest page a search may ask for.</summary>
    public const int MaxPageSize = 200;

    private readonly IProviderContractRepository _contractRepository;
    private readonly ILogger<ProviderContractsController> _logger;

    public ProviderContractsController(
        IProviderContractRepository contractRepository,
        ILogger<ProviderContractsController> logger)
    {
        _contractRepository = contractRepository;
        _logger = logger;
    }

    /// <summary>
    /// Search provider contracts with optional filters.
    /// IMPORTANT: ProviderTin is masked to last 4 digits in list responses.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<ProviderContract>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<ProviderContract>>> SearchContracts(
        [FromQuery] string? npi = null,
        [FromQuery] LineOfBusiness? lob = null,
        [FromQuery] ProviderContractStatus? status = null,
        [FromQuery] PaymentMethodology? paymentMethodology = null,
        [FromQuery] NetworkParticipationStatus? networkStatus = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        if (page < 1)
            return BadRequest(new { error = "page must be 1 or greater" });
        if (pageSize < 1 || pageSize > MaxPageSize)
            return BadRequest(new { error = $"pageSize must be between 1 and {MaxPageSize}" });

        var results = await _contractRepository.SearchAsync(npi, lob, status, paymentMethodology, networkStatus, page, pageSize);

        // Mask TIN in list responses — full TIN only via GET /{id}
        var masked = results.Select(c =>
        {
            c.ProviderTin = MaskTin(c.ProviderTin);
            return c;
        });

        return Ok(masked);
    }

    /// <summary>
    /// Get provider contract by ID. ProviderTin is full for contracts:write
    /// holders and service tokens, masked to the last 4 digits for others.
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(ProviderContract), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProviderContract>> GetContractById(string id)
    {
        var contract = await _contractRepository.GetByIdAsync(id);
        if (contract == null)
            return NotFound(new { error = $"Contract {id} not found" });
        return Ok(ForCaller(contract));
    }

    /// <summary>
    /// Get provider contract by contract number (ProviderTin as for GET by id)
    /// </summary>
    [HttpGet("number/{number}")]
    [ProducesResponseType(typeof(ProviderContract), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProviderContract>> GetContractByNumber(string number)
    {
        var contract = await _contractRepository.GetByContractNumberAsync(number);
        if (contract == null)
            return NotFound(new { error = $"Contract number {number} not found" });
        return Ok(ForCaller(contract));
    }

    /// <summary>
    /// Create a new provider contract. ContractNumber is auto-generated: CTR-{NPI}-{Year}
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(ProviderContract), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ProviderContract>> CreateContract([FromBody] ProviderContract contract)
    {
        if (contract.TerminationDate.HasValue && contract.TerminationDate.Value < contract.EffectiveDate)
            return BadRequest(new { error = "Termination date cannot be before effective date" });

        // Auto-generate contract number if not provided
        if (string.IsNullOrEmpty(contract.ContractNumber))
            contract.ContractNumber = $"CTR-{contract.ProviderNPI}-{DateTime.UtcNow.Year}";

        contract.Status = ProviderContractStatus.Draft;
        // Verification results are server-set only.
        contract.IntegrityScore = null;
        contract.IntegrityRating = null;
        contract.LastVerifiedAt = null;
        contract.NextVerificationDue = null;
        _logger.LogInformation("Creating provider contract {ContractNumber} for provider {NPI}",
            LogSanitizer.Sanitize(contract.ContractNumber), LogSanitizer.Sanitize(contract.ProviderNPI));

        var created = await _contractRepository.CreateAsync(contract);
        return CreatedAtAction(nameof(GetContractById), new { id = created.Id }, created);
    }

    /// <summary>
    /// Update a provider contract
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(ProviderContract), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProviderContract>> UpdateContract(string id, [FromBody] ProviderContract contract,
        [FromServices] ICapitationChildSync? childSync = null)
    {
        var existing = await _contractRepository.GetByIdAsync(id);
        if (existing == null)
            return NotFound(new { error = $"Contract {id} not found" });

        contract.Id = id;
        contract.TenantId = existing.TenantId;
        contract.CreatedAt = existing.CreatedAt;
        contract.CreatedBy = existing.CreatedBy;
        // Lifecycle and amendments change only through their own endpoints
        // (activate/suspend/terminate/reinstate, POST amendments), so a PUT
        // body cannot activate a contract or rewrite who approved an amendment.
        contract.Status = existing.Status;
        contract.Amendments = existing.Amendments;
        // Termination is recorded only by the terminate action, so a PUT body
        // cannot back-date, clear or invent a termination.
        contract.TerminationDate = existing.TerminationDate;
        contract.TerminationReason = existing.TerminationReason;
        // Verification results are server-set (cached from provider
        // verification); a PUT body cannot raise a score or mark a contract
        // verified. The portal does not send them, so a portal edit used to
        // clear them.
        contract.IntegrityScore = existing.IntegrityScore;
        contract.IntegrityRating = existing.IntegrityRating;
        contract.LastVerifiedAt = existing.LastVerifiedAt;
        contract.NextVerificationDue = existing.NextVerificationDue;
        // The body replaces the contract as read just now: the write is
        // conditional on that revision, so a concurrent amendment or
        // lifecycle change in between answers 409 instead of being erased.
        contract.Revision = existing.Revision;
        // Rates are money and have their own validated endpoint
        // (PUT /{id}/reimbursement-terms), so a PUT body can neither set nor
        // clear them. The portal does not send them, so a portal edit would
        // otherwise wipe a contract's whole rate sheet.
        contract.ReimbursementTerms = existing.ReimbursementTerms;
        // CapitationRateConfigIds / FfsRateConfigIds stay editable here: no
        // other endpoint links rate configs to a contract (rate-configs is
        // read-only), and the portal sends them.

        var updated = await _contractRepository.UpdateAsync(contract);

        // A change to a field the capitation rate configs copy is pushed to
        // them now, so capitation runs and statements name the contract as it
        // stands. The contract edit stands either way: a failed push is
        // reported in X-Capitation-Sync and finished by PUT {id}/sync-children,
        // which is safe to repeat.
        if (childSync != null && ParentFieldsChanged(existing, updated))
        {
            var sync = await childSync.SyncAsync(updated, HttpContext?.RequestAborted ?? default);
            if (HttpContext != null)
                Response.Headers["X-Capitation-Sync"] = sync.Succeeded
                    ? $"synced; rateConfigs={sync.RateConfigsUpdated}"
                    : "failed; retry PUT sync-children";
        }
        return Ok(updated);
    }

    /// <summary>
    /// Activate a draft contract (Draft → Active)
    /// </summary>
    /// <remarks>
    /// contracts:write, like the other transitions: no payment path reads a
    /// provider contract's status yet (capitation and FFS rates live in their
    /// own services). When one does, activation and reinstatement should need a
    /// second-user approval permission, which ChoRolePermissions does not define.
    /// </remarks>
    [HttpPut("{id}/activate")]
    [ProducesResponseType(typeof(ProviderContract), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ProviderContract>> ActivateContract(string id)
    {
        var contract = await _contractRepository.GetByIdAsync(id);
        if (contract == null)
            return NotFound(new { error = $"Contract {id} not found" });

        if (contract.Status != ProviderContractStatus.Draft && contract.Status != ProviderContractStatus.Suspended)
            return BadRequest(new { error = $"Can only activate Draft or Suspended contracts, current: {contract.Status}" });

        contract.Status = ProviderContractStatus.Active;
        _logger.LogInformation("Activated provider contract {ContractNumber}", LogSanitizer.Sanitize(contract.ContractNumber));

        var updated = await _contractRepository.UpdateAsync(contract);
        return Ok(updated);
    }

    /// <summary>
    /// Suspend an active contract (Active → Suspended)
    /// </summary>
    [HttpPut("{id}/suspend")]
    [ProducesResponseType(typeof(ProviderContract), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ProviderContract>> SuspendContract(string id, [FromBody] SuspendContractRequest request)
    {
        var contract = await _contractRepository.GetByIdAsync(id);
        if (contract == null)
            return NotFound(new { error = $"Contract {id} not found" });

        if (contract.Status != ProviderContractStatus.Active)
            return BadRequest(new { error = $"Can only suspend Active contracts, current: {contract.Status}" });

        contract.Status = ProviderContractStatus.Suspended;
        _logger.LogInformation("Suspended provider contract {ContractNumber}: {Reason}",
            contract.ContractNumber, LogSanitizer.Sanitize(request.Reason));

        var updated = await _contractRepository.UpdateAsync(contract);
        return Ok(updated);
    }

    /// <summary>
    /// Terminate a provider contract (Any → Terminated)
    /// </summary>
    [HttpPut("{id}/terminate")]
    [ProducesResponseType(typeof(ProviderContract), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ProviderContract>> TerminateContract(string id, [FromBody] TerminateContractRequest request)
    {
        var contract = await _contractRepository.GetByIdAsync(id);
        if (contract == null)
            return NotFound(new { error = $"Contract {id} not found" });

        if (contract.Status == ProviderContractStatus.Terminated)
            return BadRequest(new { error = "Contract is already terminated" });

        contract.Status = ProviderContractStatus.Terminated;
        contract.TerminationDate = request.TerminationDate ?? DateTime.UtcNow;
        contract.TerminationReason = request.Reason;
        _logger.LogInformation("Terminated provider contract {ContractNumber}: {Reason}",
            contract.ContractNumber, LogSanitizer.Sanitize(request.Reason));

        var updated = await _contractRepository.UpdateAsync(contract);
        return Ok(updated);
    }

    /// <summary>
    /// Reinstate a suspended contract (Suspended → Active)
    /// </summary>
    [HttpPut("{id}/reinstate")]
    [ProducesResponseType(typeof(ProviderContract), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ProviderContract>> ReinstateContract(string id)
    {
        var contract = await _contractRepository.GetByIdAsync(id);
        if (contract == null)
            return NotFound(new { error = $"Contract {id} not found" });

        if (contract.Status != ProviderContractStatus.Suspended)
            return BadRequest(new { error = $"Can only reinstate Suspended contracts, current: {contract.Status}" });

        contract.Status = ProviderContractStatus.Active;
        _logger.LogInformation("Reinstated provider contract {ContractNumber}", LogSanitizer.Sanitize(contract.ContractNumber));

        var updated = await _contractRepository.UpdateAsync(contract);
        return Ok(updated);
    }

    /// <summary>
    /// Add a mid-term amendment to the contract
    /// </summary>
    [HttpPost("{id}/amendments")]
    [ProducesResponseType(typeof(ProviderContract), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProviderContract>> AddAmendment(
        string id,
        [FromBody] ContractAmendment amendment,
        [FromServices] ICurrentActor actor)
    {
        var contract = await _contractRepository.GetByIdAsync(id);
        if (contract == null)
            return NotFound(new { error = $"Contract {id} not found" });

        amendment.Id = Guid.NewGuid().ToString();
        amendment.CreatedAt = DateTime.UtcNow;
        // The user who records the amendment, from the token; a body-supplied
        // ApprovedBy is ignored.
        amendment.ApprovedBy = actor.UserId;
        contract.Amendments.Add(amendment);
        _logger.LogInformation("Added amendment to provider contract {ContractNumber}: {Type}",
            contract.ContractNumber, LogSanitizer.Sanitize(amendment.AmendmentType));

        var updated = await _contractRepository.UpdateAsync(contract);
        return Ok(updated);
    }

    /// <summary>
    /// Propagate the contract's denormalized fields (contract number, NPI,
    /// provider name, line of business) to every capitation rate config whose
    /// parent is this contract, in capitation-service. Idempotent: run it again
    /// after a failure. Fee-for-service rates need nothing: they are
    /// reimbursement terms on this contract (ADR 016 phase 2) and keep no copy.
    /// </summary>
    /// <returns>200 with the number of rate configs updated; 502 when
    /// capitation-service did not apply it (nothing should be assumed synced).</returns>
    [HttpPut("{id}/sync-children")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult> SyncChildren(string id, [FromServices] ICapitationChildSync childSync)
    {
        var contract = await _contractRepository.GetByIdAsync(id);
        if (contract == null)
            return NotFound(new { error = $"Contract {id} not found" });

        var result = await childSync.SyncAsync(contract, HttpContext?.RequestAborted ?? default);
        if (!result.Succeeded)
        {
            _logger.LogWarning("Sync-children for provider contract {ContractNumber} failed: {Error}",
                LogSanitizer.Sanitize(contract.ContractNumber), result.Error);
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                error = "Capitation rate configs were not updated: " + result.Error + ". Retry; the sync is safe to repeat.",
                contractId = id
            });
        }

        _logger.LogInformation("Sync-children for provider contract {ContractNumber}: {Count} capitation rate configs",
            LogSanitizer.Sanitize(contract.ContractNumber), result.RateConfigsUpdated);
        return Ok(new
        {
            contractId = id,
            capitationRateConfigsUpdated = result.RateConfigsUpdated,
            syncedAt = result.SyncedAt
        });
    }

    /// <summary>Whether an edit changed a field the capitation rate configs keep a copy of.</summary>
    public static bool ParentFieldsChanged(ProviderContract before, ProviderContract after) =>
        !string.Equals(before.ContractNumber, after.ContractNumber, StringComparison.Ordinal)
        || !string.Equals(before.ProviderNPI, after.ProviderNPI, StringComparison.Ordinal)
        || !string.Equals(before.ProviderName, after.ProviderName, StringComparison.Ordinal)
        || before.LineOfBusiness != after.LineOfBusiness;

    /// <summary>
    /// Get rate config IDs (capitation + FFS) for this contract
    /// </summary>
    [HttpGet("{id}/rate-configs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetRateConfigs(string id)
    {
        var contract = await _contractRepository.GetByIdAsync(id);
        if (contract == null)
            return NotFound(new { error = $"Contract {id} not found" });

        return Ok(new
        {
            contractId = id,
            capitationRateConfigIds = contract.CapitationRateConfigIds,
            ffsRateConfigIds = contract.FfsRateConfigIds
        });
    }

    // ── Reimbursement terms (ADR 016 §2) ──────────────────────────────────

    /// <summary>
    /// The contract's rate sheet, in evaluation order: the first term whose
    /// scope covers a claim line and whose effective dates cover the service
    /// date prices it.
    /// </summary>
    [HttpGet("{id}/reimbursement-terms")]
    [ProducesResponseType(typeof(IEnumerable<ReimbursementTerm>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IEnumerable<ReimbursementTerm>>> GetReimbursementTerms(string id)
    {
        var contract = await _contractRepository.GetByIdAsync(id);
        if (contract == null)
            return NotFound(new { error = $"Contract {id} not found" });

        return Ok(contract.ReimbursementTerms);
    }

    /// <summary>
    /// Replace the contract's whole rate sheet.
    /// </summary>
    /// <remarks>
    /// The whole sheet, not one term, because the order is the contract: a
    /// carve-out placed after the default term it carves out of would never
    /// price a line. Every term is validated, and so is the list — a term an
    /// earlier unconditional term always answers before is rejected rather than
    /// stored, since at pricing time an unreachable term is invisible.
    ///
    /// A term with no id is given one, so the stored sheet can always name the
    /// term that priced a line.
    /// </remarks>
    [HttpPut("{id}/reimbursement-terms")]
    [ProducesResponseType(typeof(IEnumerable<ReimbursementTerm>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IEnumerable<ReimbursementTerm>>> ReplaceReimbursementTerms(
        string id, [FromBody] ReimbursementTermsRequest request)
    {
        var contract = await _contractRepository.GetByIdAsync(id);
        if (contract == null)
            return NotFound(new { error = $"Contract {id} not found" });

        var terms = request.Terms ?? [];
        foreach (var term in terms.Where(t => string.IsNullOrWhiteSpace(t.Id)))
            term.Id = Guid.NewGuid().ToString();

        var errors = ReimbursementTermSheet.Validate(terms);
        if (errors.Count > 0)
            return BadRequest(new { error = "The rate sheet is not valid", details = errors });

        contract.ReimbursementTerms = terms.ToList();
        _logger.LogInformation(
            "Replaced the rate sheet of provider contract {ContractNumber} with {TermCount} term(s)",
            LogSanitizer.Sanitize(contract.ContractNumber), terms.Count);

        var updated = await _contractRepository.UpdateAsync(contract);
        return Ok(updated.ReimbursementTerms);
    }

    /// <summary>
    /// Append a fee-for-service rate to the contract as a reimbursement term.
    /// </summary>
    /// <remarks>
    /// The ffs-service fold of ADR 016 phase 2. A fee-for-service rate used to
    /// be an <c>FfsRateConfig</c> document in a service that never grew a
    /// controller or a repository, so the rate was unreachable and nothing
    /// priced from it. The same fields — a fee schedule, a percentage of it, and
    /// the two modifier toggles — are one term on the contract, which the
    /// pricing engine already reads.
    ///
    /// The term is appended, so a sheet that already has a default term rejects
    /// the new one as unreachable: reorder it with
    /// <c>PUT /{id}/reimbursement-terms</c>, which is the endpoint that owns the
    /// order.
    /// </remarks>
    [HttpPost("{id}/reimbursement-terms/from-ffs-rate-config")]
    [ProducesResponseType(typeof(IEnumerable<ReimbursementTerm>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IEnumerable<ReimbursementTerm>>> AddFfsRateConfigTerm(
        string id, [FromBody] FfsRateConfigTermRequest request)
    {
        var contract = await _contractRepository.GetByIdAsync(id);
        if (contract == null)
            return NotFound(new { error = $"Contract {id} not found" });

        if (contract.PaymentMethodology is PaymentMethodology.FullCapitation)
        {
            return BadRequest(new
            {
                error = "This contract is full capitation, so it has no fee-for-service rates. " +
                        "Change the payment methodology first, or add a capitation term.",
            });
        }

        var term = request.ToReimbursementTerm();
        var terms = contract.ReimbursementTerms.Append(term).ToList();

        var errors = ReimbursementTermSheet.Validate(terms);
        if (errors.Count > 0)
            return BadRequest(new { error = "The rate sheet is not valid", details = errors });

        contract.ReimbursementTerms = terms;
        _logger.LogInformation(
            "Added a fee-for-service term on schedule {FeeScheduleId} to provider contract {ContractNumber}",
            LogSanitizer.Sanitize(request.FeeScheduleId), LogSanitizer.Sanitize(contract.ContractNumber));

        var updated = await _contractRepository.UpdateAsync(contract);
        return Ok(updated.ReimbursementTerms);
    }

    /// <summary>
    /// The contract as the pricing engine's contract: what a claim against this
    /// provider would be priced from.
    /// </summary>
    /// <remarks>
    /// Read-only, and the point of ADR 016. The legal agreement and the document
    /// the engine prices from are two unrelated records today; this is the
    /// derivation of the second from the first, which the phase 3 backfill will
    /// write and the phase 4 dual-read window will regenerate after every edit.
    /// Reading it is how a reviewer sees what a contract would price as before
    /// anything is written.
    ///
    /// One engine contract per plan the legal contract covers, because the
    /// engine looks a contract up by a single plan. <c>warnings</c> carries
    /// everything that needs a person's judgement first: a contract that is not
    /// active, one with no terms, a plan scope that has to be expanded against
    /// the plan catalogue, a network status the engine has no value for. No TIN
    /// is projected: pricing needs the NPI, the plan and the terms.
    /// </remarks>
    [HttpGet("{id}/pricing-contract")]
    [ProducesResponseType(typeof(PricingContractProjectionResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PricingContractProjectionResult>> GetPricingContract(string id)
    {
        var contract = await _contractRepository.GetByIdAsync(id);
        if (contract == null)
            return NotFound(new { error = $"Contract {id} not found" });

        return Ok(PricingContractProjection.Project(contract));
    }

    /// <summary>
    /// The contract as this caller may see it: full ProviderTin for
    /// contracts:write holders and service tokens (HasPermission is true for a
    /// service token), masked for everyone else, including callers with no
    /// token context.
    /// </summary>
    private ProviderContract ForCaller(ProviderContract contract)
    {
        var user = HttpContext?.User;
        if (user == null || !ChoPrincipal.HasPermission(user, "contracts:write"))
            contract.ProviderTin = MaskTin(contract.ProviderTin);
        return contract;
    }

    /// <summary>
    /// Masks TIN to ***-**-XXXX format (last 4 digits only).
    /// Strips non-digits to handle both formatted (12-3456789) and raw (123456789) input.
    /// </summary>
    private static string? MaskTin(string? tin)
    {
        if (string.IsNullOrWhiteSpace(tin))
            return tin;

        var digits = new string(tin.Where(char.IsDigit).ToArray());
        if (digits.Length < 4)
            return "***-**-****";

        var last4 = digits[^4..];
        return $"***-**-{last4}";
    }
}

public class SuspendContractRequest
{
    public string Reason { get; set; } = string.Empty;
}

public class TerminateContractRequest
{
    public string Reason { get; set; } = string.Empty;
    public DateTime? TerminationDate { get; set; }
}
