using EnrollmentImportService.Models;
using EnrollmentImportService.Services;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace EnrollmentImportService.Controllers;

/// <summary>
/// Manual enrollment entry. Validates with the same <see cref="IEnrollmentValidator"/> as
/// the 834 ingestion path and emits an equivalent <see cref="EnrollmentEvent"/> so manual
/// and 834 events are interchangeable downstream.
///
/// Idempotency: callers may supply <see cref="MemberEnrollment.EventId"/> in the body to
/// guarantee retry-safety. If omitted, a fresh GUID is generated per submission and only
/// exact-collision dedup applies.
/// </summary>
[ApiController]
[Route("api/v1/enrollments")]
public class ManualEnrollmentController : ControllerBase
{
    private readonly IEnrollmentImportService _importService;
    private readonly IEnrollmentValidator _validator;
    private readonly ICurrentActor _actor;
    private readonly ILogger<ManualEnrollmentController> _logger;

    public ManualEnrollmentController(
        IEnrollmentImportService importService,
        IEnrollmentValidator validator,
        ICurrentActor actor,
        ILogger<ManualEnrollmentController> logger)
    {
        _importService = importService;
        _validator = validator;
        _actor = actor;
        _logger = logger;
    }

    [HttpPost("manual")]
    [ProducesResponseType(typeof(ImportResult), 200)]
    [ProducesResponseType(typeof(ValidationProblemDetails), 400)]
    public async Task<IActionResult> CreateManual(
        [FromBody] MemberEnrollment enrollment)
    {
        // Tenant and actor come from the validated token. The X-Tenant-ID and
        // X-Actor-ID headers this action used to read are no longer consulted.
        var tenantId = _actor.TenantId;
        var actorId = _actor.UserId;

        var validation = _validator.Validate(enrollment);
        if (!validation.IsValid)
        {
            // Map structured errors to a field-keyed ValidationProblemDetails.
            // Multiple errors per field are merged into the same key.
            var grouped = validation.Errors
                .GroupBy(e => e.Field)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(e => $"[{e.Code}] {e.Message}").ToArray());

            var problem = new ValidationProblemDetails(grouped)
            {
                Title = "Manual enrollment validation failed",
                Status = StatusCodes.Status400BadRequest
            };
            return BadRequest(problem);
        }

        // Caller-supplied EventId guarantees retry-safety. We default a GUID here so the
        // downstream publisher always sees a non-null id.
        enrollment.EventId ??= Guid.NewGuid().ToString("N");

        var batchId = $"MANUAL-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}".Substring(0, 40);
        var batch = new Enrollment834
        {
            FileName = $"manual:{actorId}",
            ParsedAt = DateTime.UtcNow,
            TransactionCount = 1,
            BatchId = batchId,
            ManualSource = true,
            ActorId = actorId,
            Enrollments = new List<MemberEnrollment> { enrollment }
        };

        _logger.LogInformation(
            "Manual enrollment for tenant {TenantId} subscriber {SubscriberId} type {Type} actor {Actor} eventId {EventId}",
            Sanitize(tenantId),
            Sanitize(enrollment.SubscriberId),
            Sanitize(enrollment.MaintenanceType),
            Sanitize(actorId),
            Sanitize(enrollment.EventId));

        var result = await _importService.ImportEnrollmentAsync(batch, tenantId);
        return Ok(result);
    }

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
