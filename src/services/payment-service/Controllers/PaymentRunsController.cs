using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using PaymentService.Models;
using PaymentService.Services;

namespace PaymentService.Controllers;

/// <summary>
/// Payment runs. Reads need payments:read; creating and cancelling a run
/// (preparation) need payments:run (Program.cs defaults). Executing a run
/// releases money (check numbers, Posted payments, 835 envelopes, claims
/// finalized as paid), so it needs payments:approve from a user who did not
/// create the run (maker-checker, RunSeparationOfDuties); a service token is
/// refused. The tenant and the acting user come from the CHO token. During
/// execution, claims-service and trading-partner calls carry payment-service's
/// own service token (RunExecutionGrant), opened only after that check.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class PaymentRunsController : ControllerBase
{
    private readonly IPaymentRunService _paymentRunService;
    private readonly ICurrentActor _actor;
    private readonly ILogger<PaymentRunsController> _logger;

    public PaymentRunsController(
        IPaymentRunService paymentRunService,
        ICurrentActor actor,
        ILogger<PaymentRunsController> logger)
    {
        _paymentRunService = paymentRunService;
        _actor = actor;
        _logger = logger;
    }

    /// <summary>
    /// Release a claim's payment reservation that this run holds but did not pay
    /// (the run failed, was cancelled, or is stuck), so a later run may pay the
    /// claim. Needs payments:approve from a user who did not execute the run (a
    /// service token is refused) and a reason; audited and listed on the run.
    /// 409 when the claim has a Posted or PaidPendingFinalize payment (not
    /// unpaid: retry its finalize instead) or the run is still executing.
    /// </summary>
    [HttpPost("{id}/reservations/{claimId}/release")]
    [RequirePermission("payments:approve")]
    [ProducesResponseType(typeof(ReservationReleaseResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ActionResult<ReservationReleaseResult>> ReleaseReservation(
        string id, string claimId, [FromBody] ReleaseReservationRequest? request,
        [FromServices] IReservationReconciliationService reconciliation)
        => ReservationRelease.HandleAsync(this, reconciliation, Repositories.ClaimReservationKind.Payment, id, claimId, request);

    /// <summary>
    /// Create a new payment run (does not execute)
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(PaymentRun), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaymentRun>> CreatePaymentRun([FromBody] CreatePaymentRunRequest request)
    {
        _logger.LogInformation("Creating payment run with criteria: LOB={LOB}, Provider={Provider}",
            request.Criteria.LineOfBusiness, SanitizeForLog(request.Criteria.ProviderNPI));

        // The creator is the token subject; request.CreatedBy is never read.
        PaymentRun paymentRun;
        try
        {
            paymentRun = await _paymentRunService.CreatePaymentRunAsync(
                request.Criteria,
                _actor.UserId,
                request.PaymentDate);
        }
        catch (ArgumentException ex)
        {
            return Problem(title: "Invalid payment date", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }

        return CreatedAtAction(
            nameof(GetPaymentRunById),
            new { id = paymentRun.Id },
            paymentRun);
    }

    /// <summary>
    /// Create and immediately execute a payment run. Always refused (403): the
    /// creator would be the executor, and releasing money needs a second user.
    /// Create the run with POST /api/paymentruns; a different user with
    /// payments:approve executes it with POST /api/paymentruns/{id}/execute.
    /// Nothing is created.
    /// </summary>
    [HttpPost("execute")]
    [RequirePermission("payments:approve")]
    [ProducesResponseType(typeof(PaymentRun), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<PaymentRun> CreateAndExecutePaymentRun([FromBody] CreatePaymentRunRequest request)
    {
        _logger.LogWarning("Refused create-and-execute payment run by {User}: the creator cannot execute",
            SanitizeForLog(_actor.UserId));
        return SeparationOfDuties(new SeparationOfDutiesException(
            "Separation of duties: a payment run cannot be created and executed by the same caller. " +
            "Create it with POST /api/paymentruns; a different user with payments:approve executes it."));
    }

    /// <summary>
    /// Execute an existing payment run
    /// </summary>
    [HttpPost("{id}/execute")]
    [RequirePermission("payments:approve")]
    [ProducesResponseType(typeof(PaymentRun), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaymentRun>> ExecutePaymentRun(string id)
    {
        _logger.LogInformation("Executing payment run {PaymentRunId}", SanitizeForLog(id));

        try
        {
            var paymentRun = await _paymentRunService.ExecutePaymentRunAsync(id);
            return Ok(paymentRun);
        }
        catch (SeparationOfDutiesException ex)
        {
            return SeparationOfDuties(ex);
        }
        catch (RunConflictException ex)
        {
            return Problem(title: "Run already started", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Retry the claims-service finalize for the claims an executed run paid but
    /// claims-service did not finalize (<c>PendingFinalizeClaimIds</c>, payments
    /// <c>PaidPendingFinalize</c>). Creates no payment and reuses each payment's
    /// check number, which claims-service treats as idempotent. Needs
    /// payments:run: the money was already released by the run's approver, who
    /// stays the recorded actor; the calls carry payment-service's service token.
    /// </summary>
    [HttpPost("{id}/finalize")]
    [RequirePermission("payments:run")]
    [ProducesResponseType(typeof(PaymentRun), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaymentRun>> RetryFinalize(string id)
    {
        _logger.LogInformation("Retrying finalize for payment run {PaymentRunId} by {User}",
            SanitizeForLog(id), SanitizeForLog(_actor.UserId));
        try
        {
            return Ok(await _paymentRunService.RetryFinalizeAsync(id));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Generate (first call) or reproduce (later calls) the NACHA CCD+ credit
    /// file of a completed ACH payment run: one credit per payee (TIN + approved
    /// account) per 835 trace, the addenda carrying the 835's TRN. Accounts come
    /// only from provider-service's approved accounts; payees without one are
    /// listed as check fallbacks. Returns the file's facts (SHA-256, counts,
    /// totals, entry hash, masked entries), never its content. A later call must
    /// reproduce the pinned file byte for byte, else 409 with nothing changed.
    /// Needs payments:approve (it reads full bank numbers); a service token is refused.
    /// </summary>
    [HttpPost("{id}/eft-file")]
    [RequirePermission("payments:approve")]
    [ProducesResponseType(typeof(PaymentRunEftFile), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PaymentRunEftFile>> GenerateEftFile(
        string id, [FromServices] IFfsEftFileService eftFiles, CancellationToken cancellationToken)
    {
        if (_actor.IsService)
            return SeparationOfDuties(new SeparationOfDutiesException(
                "A service token cannot generate a payment run's EFT file; a user with payments:approve does."));
        try
        {
            var outcome = await eftFiles.GenerateAsync(id, _actor.UserId, cancellationToken);
            return Ok(outcome.Run.EftFile);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (RunConflictException ex)
        {
            return Problem(title: "EFT file differs", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// The bank transmission record of a run's NACHA file: status, attempts
    /// (operator, hash, result), receipt. Never the file or a bank number.
    /// </summary>
    [HttpGet("{id}/eft-file/transmission")]
    [ProducesResponseType(typeof(PaymentFileTransmission), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PaymentFileTransmission>> GetEftFileTransmission(
        string id, [FromServices] IPaymentFileTransmissionService transmissions, CancellationToken cancellationToken)
    {
        try
        {
            var record = await transmissions.GetAsync(id, cancellationToken);
            return record == null
                ? Problem(title: "Not transmitted", detail: "This run's NACHA file was never approved for transmission.", statusCode: StatusCodes.Status404NotFound)
                : Ok(record);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
    }

    /// <summary>
    /// Approve and send a completed ACH run's NACHA file to the tenant's bank
    /// (SFTP, pinned host key), or retry one whose last attempt Failed. The bytes
    /// are regenerated and must hash to the SHA-256 pinned at generation and at
    /// approval, or nothing is sent (409). Exactly once: a Transmitted file answers
    /// 200 without sending; a NeedsReview file (outcome unknown) is never re-sent
    /// (409) until reconciled or resolved. Needs payments:approve from a user who
    /// did not create the run; a service token is refused. 409 while
    /// BankTransmission:Enabled is false (nothing is done). 200 Transmitted,
    /// 502 Failed (nothing reached the bank; may be retried), 409 NeedsReview.
    /// </summary>
    [HttpPost("{id}/eft-file/transmission")]
    [RequirePermission("payments:approve")]
    [ProducesResponseType(typeof(PaymentFileTransmission), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PaymentFileTransmission), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ActionResult<PaymentFileTransmission>> TransmitEftFile(
        string id, [FromServices] IPaymentFileTransmissionService transmissions, CancellationToken cancellationToken)
        => TransmissionAction(async () =>
        {
            var record = await transmissions.TransmitAsync(id, cancellationToken);
            return record.Status switch
            {
                PaymentFileTransmissionStatus.Transmitted => Ok(record),
                PaymentFileTransmissionStatus.Failed => StatusCode(StatusCodes.Status502BadGateway, record),
                _ => StatusCode(StatusCodes.Status409Conflict, record),
            };
        });

    /// <summary>
    /// For a NeedsReview file: list the bank's drop (read-only). Found with the
    /// expected size: Transmitted. Otherwise it stays NeedsReview (the bank may have
    /// collected it already) for a second user to resolve. Needs payments:approve
    /// from a user who did not create the run.
    /// </summary>
    [HttpPost("{id}/eft-file/transmission/reconcile")]
    [RequirePermission("payments:approve")]
    [ProducesResponseType(typeof(PaymentFileTransmission), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ActionResult<PaymentFileTransmission>> ReconcileEftFileTransmission(
        string id, [FromServices] IPaymentFileTransmissionService transmissions, CancellationToken cancellationToken)
        => TransmissionAction(async () => Ok(await transmissions.ReconcileAsync(id, cancellationToken)));

    /// <summary>
    /// For a NeedsReview file: record what the bank said, with the evidence.
    /// bankReceived true: Transmitted. False: Failed (it may then be retried).
    /// Needs payments:approve from a user who neither created the run, approved
    /// the transmission nor attempted it.
    /// </summary>
    [HttpPost("{id}/eft-file/transmission/resolve")]
    [RequirePermission("payments:approve")]
    [ProducesResponseType(typeof(PaymentFileTransmission), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ActionResult<PaymentFileTransmission>> ResolveEftFileTransmission(
        string id, [FromBody] ResolvePaymentFileTransmissionRequest? request,
        [FromServices] IPaymentFileTransmissionService transmissions, CancellationToken cancellationToken)
    {
        if (request?.BankReceived == null || string.IsNullOrWhiteSpace(request.Reason))
            return Task.FromResult<ActionResult<PaymentFileTransmission>>(Problem(title: "Bank answer required",
                detail: "bankReceived (true/false) and reason (what the bank said, who, their reference) are required.",
                statusCode: StatusCodes.Status400BadRequest));
        return TransmissionAction(async () =>
            Ok(await transmissions.ResolveAsync(id, request.BankReceived.Value, request.Reason, cancellationToken)));
    }

    /// <summary>
    /// Re-date a run's NACHA file whose effective entry date can no longer be sent:
    /// a new file (new creation time, effective date chosen now, new file ID modifier,
    /// reference and name ending -R{n}) replaces the pinned one. The old file's
    /// transmission record becomes Superseded (kept, linked, never sendable). Allowed
    /// only when the old file was never approved, or its record is Pending or Failed
    /// (including "bank did not receive it"); never when Transmitting, Transmitted or
    /// NeedsReview. The new file needs a fresh approval (POST .../transmission).
    /// payments:approve, a user who neither created nor executed the run; reason required.
    /// </summary>
    [HttpPost("{id}/eft-file/redate")]
    [RequirePermission("payments:approve")]
    [ProducesResponseType(typeof(PaymentRunEftFile), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PaymentRunEftFile>> RedateEftFile(
        string id, [FromBody] RedateEftFileRequest? request,
        [FromServices] IPaymentFileTransmissionService transmissions, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.Reason))
            return Problem(title: "Reason required", detail: "A reason is required to re-date a NACHA file.",
                statusCode: StatusCodes.Status400BadRequest);
        var result = await TransmissionAction(async () =>
        {
            var file = await transmissions.RedateAsync(id, request.Reason, cancellationToken);
            return new ObjectResult(file) { StatusCode = StatusCodes.Status200OK };
        });
        return result.Result!;
    }

    private async Task<ActionResult<PaymentFileTransmission>> TransmissionAction(Func<Task<ActionResult<PaymentFileTransmission>>> action)
    {
        if (_actor.IsService)
            return SeparationOfDuties(new SeparationOfDutiesException(
                "A service token cannot approve, transmit or settle a NACHA file; a user with payments:approve does."));
        try
        {
            return await action();
        }
        catch (BankTransmissionDisabledException ex)
        {
            return Problem(title: "Bank transmission disabled", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (SeparationOfDutiesException ex)
        {
            return SeparationOfDuties(ex);
        }
        catch (KeyNotFoundException ex)
        {
            return Problem(title: "Not found", detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (PaymentFileHashMismatchException ex)
        {
            return Problem(title: "File differs from the approved file", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (PaymentFileApprovalStaleException ex)
        {
            return Problem(title: "Approval no longer valid", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (PaymentFileTransmissionStateException ex)
        {
            return Problem(title: "Transmission state", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (ArgumentException ex)
        {
            return Problem(title: "Invalid request", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (InvalidOperationException ex)
        {
            return Problem(title: "Cannot transmit", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>
    /// Get payment run by ID
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(PaymentRun), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PaymentRun>> GetPaymentRunById(string id)
    {
        try
        {
            var paymentRun = await _paymentRunService.GetPaymentRunAsync(id);
            return Ok(paymentRun);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ex.Message);
        }
    }

    /// <summary>
    /// Get all payment runs with optional date filter
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<PaymentRun>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<PaymentRun>>> GetPaymentRuns(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to)
    {
        var paymentRuns = await _paymentRunService.GetPaymentRunsAsync(from, to);
        return Ok(paymentRuns);
    }

    /// <summary>
    /// Cancel a pending payment run
    /// </summary>
    [HttpPost("{id}/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> CancelPaymentRun(string id)
    {
        try
        {
            await _paymentRunService.CancelPaymentRunAsync(id);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    private ObjectResult SeparationOfDuties(SeparationOfDutiesException ex)
        => Problem(title: "Separation of duties", detail: ex.Message, statusCode: StatusCodes.Status403Forbidden);

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

/// <summary>The manual release, shared by payment runs and reversal runs.</summary>
internal static class ReservationRelease
{
    public static async Task<ActionResult<ReservationReleaseResult>> HandleAsync(
        ControllerBase controller, IReservationReconciliationService reconciliation,
        Repositories.ClaimReservationKind kind, string runId, string claimId, ReleaseReservationRequest? request)
    {
        if (string.IsNullOrWhiteSpace(request?.Reason))
            return controller.Problem(title: "Reason required",
                detail: "A reason is required to release a claim reservation.",
                statusCode: StatusCodes.Status400BadRequest);
        try
        {
            return controller.Ok(await reconciliation.ReleaseManuallyAsync(kind, runId, claimId, request.Reason));
        }
        catch (SeparationOfDutiesException ex)
        {
            return controller.Problem(title: "Separation of duties", detail: ex.Message, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (ReservationNotFoundException ex)
        {
            return controller.Problem(title: "Not found", detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (ReservationConflictException ex)
        {
            return controller.Problem(title: "Reservation not released", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}

public class RedateEftFileRequest
{
    /// <summary>Why the file is re-dated (e.g. "approved after its effective date passed").</summary>
    public string? Reason { get; set; }
}

public class CreatePaymentRunRequest
{
    public PaymentRunCriteria Criteria { get; set; } = new();
    /// <summary>Ignored: the creator is the token subject.</summary>
    public string? CreatedBy { get; set; }

    /// <summary>
    /// Optional payment (ACH effective entry) date: not in the past, at most a year
    /// ahead (else 400). A weekend or holiday is rolled to the next banking day when
    /// the NACHA file is pinned. Omitted: the next banking day, re-chosen at pinning.
    /// </summary>
    public DateTime? PaymentDate { get; set; }
    public string? Description { get; set; }
}
