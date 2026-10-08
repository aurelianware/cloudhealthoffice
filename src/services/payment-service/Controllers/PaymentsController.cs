using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using PaymentService.Models;
using PaymentService.Repositories;
using PaymentService.Services;

namespace PaymentService.Controllers;

/// <summary>
/// Payment records. Reads need payments:read (Program.cs default). Recording,
/// posting and reconciling a payment change the payment ledger and need
/// finance:write; the acting user comes from the CHO token, never the body.
/// The 835 download masks bank routing and account numbers.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class PaymentsController : ControllerBase
{
    private readonly IPaymentRepository _paymentRepository;
    private readonly IEraGeneratorService _eraGenerator;
    private readonly IConfiguration _configuration;
    private readonly ICurrentActor _actor;
    private readonly ILogger<PaymentsController> _logger;

    public PaymentsController(
        IPaymentRepository paymentRepository,
        IEraGeneratorService eraGenerator,
        IConfiguration configuration,
        ICurrentActor actor,
        ILogger<PaymentsController> logger)
    {
        _paymentRepository = paymentRepository;
        _eraGenerator = eraGenerator;
        _configuration = configuration;
        _actor = actor;
        _logger = logger;
    }

    /// <summary>
    /// Process 835 ERA payment transaction
    /// </summary>
    [HttpPost]
    [RequirePermission("finance:write")]
    [ProducesResponseType(typeof(Payment), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<Payment>> ProcessPayment([FromBody] Payment payment)
    {
        _logger.LogInformation("Processing 835 ERA payment for check {CheckNumber}", SanitizeForLog(payment.CheckNumber));

        // Validation
        if (string.IsNullOrEmpty(payment.CheckNumber))
        {
            return BadRequest("Check number is required");
        }

        // Check for duplicate
        var existing = await _paymentRepository.GetByCheckNumberAsync(payment.CheckNumber);
        if (existing != null)
        {
            return Conflict($"Payment with check number {payment.CheckNumber} already exists");
        }

        // Audit fields come from the token, never the body (the repository
        // also overwrites TenantId with the token tenant).
        var actor = _actor.UserId;
        payment.TenantId = _actor.TenantId;
        payment.PostedBy = payment.Status is PaymentStatus.Posted or PaymentStatus.Reconciled ? actor : null;
        payment.PostedAt = payment.PostedBy != null ? DateTime.UtcNow : null;
        payment.ReconciledBy = payment.Status == PaymentStatus.Reconciled ? actor : null;
        payment.ReconciledAt = payment.ReconciledBy != null ? DateTime.UtcNow : null;

        var created = await _paymentRepository.CreateAsync(payment);

        return CreatedAtAction(
            nameof(GetPaymentById),
            new { id = created.Id },
            created);
    }

    /// <summary>
    /// Get payment by ID
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(Payment), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Payment>> GetPaymentById(string id)
    {
        var payment = await _paymentRepository.GetByIdAsync(id);

        if (payment == null)
        {
            return NotFound($"Payment {id} not found");
        }

        return Ok(payment);
    }

    /// <summary>
    /// Get payment by check number
    /// </summary>
    [HttpGet("check/{checkNumber}")]
    [ProducesResponseType(typeof(Payment), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Payment>> GetPaymentByCheckNumber(string checkNumber)
    {
        var payment = await _paymentRepository.GetByCheckNumberAsync(checkNumber);

        if (payment == null)
        {
            return NotFound($"Payment with check number {checkNumber} not found");
        }

        return Ok(payment);
    }

    /// <summary>
    /// Get payments for a specific claim
    /// </summary>
    [HttpGet("claim/{claimId}")]
    [ProducesResponseType(typeof(IEnumerable<Payment>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<Payment>>> GetPaymentsByClaimId(string claimId)
    {
        var payments = await _paymentRepository.GetByClaimIdAsync(claimId);
        return Ok(payments);
    }

    /// <summary>
    /// Search payments with filters
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<Payment>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<Payment>>> SearchPayments(
        [FromQuery] DateTime? paymentDateFrom,
        [FromQuery] DateTime? paymentDateTo,
        [FromQuery] string? payerId,
        [FromQuery] PaymentStatus? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        _logger.LogInformation("Searching payments: date range {From} to {To}, payer {Payer}, status {Status}",
            paymentDateFrom, paymentDateTo, SanitizeForLog(payerId), status);

        var payments = await _paymentRepository.SearchAsync(
            paymentDateFrom, paymentDateTo, payerId, status, page, pageSize);

        return Ok(payments);
    }

    /// <summary>
    /// Post payment (mark as posted to accounts)
    /// </summary>
    [HttpPost("{id}/post")]
    [RequirePermission("finance:write")]
    [ProducesResponseType(typeof(Payment), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Payment>> PostPayment(string id, [FromBody] PostPaymentRequest request)
    {
        var payment = await _paymentRepository.GetByIdAsync(id);

        if (payment == null)
        {
            return NotFound($"Payment {id} not found");
        }

        payment.Status = PaymentStatus.Posted;
        payment.PostedAt = DateTime.UtcNow;
        payment.PostedBy = _actor.UserId; // request.PostedBy is ignored
        payment.Notes = request.Notes;

        var updated = await _paymentRepository.UpdateAsync(payment);

        _logger.LogInformation("Payment {PaymentId} posted by {User}", SanitizeForLog(id), SanitizeForLog(payment.PostedBy));

        return Ok(updated);
    }

    /// <summary>
    /// Reconcile payment (mark as reconciled with bank)
    /// </summary>
    [HttpPost("{id}/reconcile")]
    [RequirePermission("finance:write")]
    [ProducesResponseType(typeof(Payment), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Payment>> ReconcilePayment(string id, [FromBody] ReconcilePaymentRequest request)
    {
        var payment = await _paymentRepository.GetByIdAsync(id);

        if (payment == null)
        {
            return NotFound($"Payment {id} not found");
        }

        payment.Status = PaymentStatus.Reconciled;
        payment.ReconciledAt = DateTime.UtcNow;
        payment.ReconciledBy = _actor.UserId;
        payment.Notes = string.IsNullOrEmpty(payment.Notes) 
            ? request.Notes 
            : $"{payment.Notes}\n{request.Notes}";

        var updated = await _paymentRepository.UpdateAsync(payment);

        _logger.LogInformation("Payment {PaymentId} reconciled", SanitizeForLog(id));

        return Ok(updated);
    }

    /// <summary>
    /// Download the X12 835 ERA file for a payment
    /// </summary>
    [HttpGet("{id}/835")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetEra835(string id)
    {
        var payment = await _paymentRepository.GetByIdAsync(id);

        if (payment == null)
        {
            return NotFound($"Payment {id} not found");
        }

        var tp = new TradingPartnerInfo
        {
            InterchangeSenderId   = _configuration["Era:InterchangeSenderId"]   ?? "SENDER",
            InterchangeReceiverId = _configuration["Era:InterchangeReceiverId"] ?? "RECEIVER",
            ApplicationSenderId   = _configuration["Era:ApplicationSenderId"]   ?? "SENDER",
            ApplicationReceiverId = _configuration["Era:ApplicationReceiverId"] ?? "RECEIVER",
            PayerRoutingNumber    = _configuration["Era:PayerRoutingNumber"],
            PayerAccountNumber    = _configuration["Era:PayerAccountNumber"],
            OriginatingCompanyId  = _configuration["Era:OriginatingCompanyId"],
            OriginatingCompanySupplementalCode = _configuration["Era:OriginatingCompanySupplementalCode"],
            PayeeRoutingNumber    = _configuration["Era:PayeeRoutingNumber"],
            PayeeAccountNumber    = _configuration["Era:PayeeAccountNumber"],
        };

        _logger.LogInformation("Generating 835 ERA download for payment {PaymentId} check {CheckNumber}",
            SanitizeForLog(id), SanitizeForLog(payment.CheckNumber));

        // The BPR segment carries the payer's and payee's bank routing and
        // account numbers; a download never returns them in full.
        string era;
        try
        {
            era = EdiBankNumberMasking.MaskBpr(_eraGenerator.Generate835(payment, tp));
        }
        catch (InvalidOperationException ex)
        {
            // An ACH BPR that configuration cannot fill (e.g. no BPR10
            // originating company id) is refused, never emitted misaligned.
            _logger.LogError(ex, "Cannot generate 835 for payment {PaymentId}", SanitizeForLog(id));
            return Problem(title: "835 cannot be generated", detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError);
        }

        var filename = $"835_{payment.CheckNumber}.edi";
        Response.Headers["Content-Disposition"] = $"attachment; filename=\"{filename}\"";
        return Content(era, "text/plain");
    }

    /// <summary>
    /// Get payment summary statistics
    /// </summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(PaymentsSummary), StatusCodes.Status200OK)]
    public async Task<ActionResult<PaymentsSummary>> GetPaymentsSummary(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to)
    {
        var fromDate = from ?? DateTime.UtcNow.AddMonths(-1);
        var toDate = to ?? DateTime.UtcNow;

        var summary = await _paymentRepository.GetPaymentsSummaryAsync(fromDate, toDate);

        return Ok(summary);
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

public class PostPaymentRequest
{
    /// <summary>Ignored: the poster is the token subject.</summary>
    public string PostedBy { get; set; } = string.Empty;
    public string? Notes { get; set; }
}

public class ReconcilePaymentRequest
{
    public string? Notes { get; set; }
}
