using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using EncounterService.Middleware;
using EncounterService.Models;
using EncounterService.Repositories;
using EncounterService.Services;

namespace EncounterService.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class EncountersController : ControllerBase
{
    private readonly IEncounterRepository _encounterRepository;
    private readonly IEncounter837Service _edi837Service;
    private readonly IConfiguration _configuration;
    private readonly ILogger<EncountersController> _logger;

    public EncountersController(
        IEncounterRepository encounterRepository,
        IEncounter837Service edi837Service,
        IConfiguration configuration,
        ILogger<EncountersController> logger)
    {
        _encounterRepository = encounterRepository;
        _edi837Service = edi837Service;
        _configuration = configuration;
        _logger = logger;
    }

    // ── Submission Lifecycle ──────────────────────────────────────────

    /// <summary>
    /// Submit a new encounter for processing.
    /// Creates a Pending encounter record that can later be batched and dispatched.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(Encounter), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<Encounter>> SubmitEncounter([FromBody] Encounter encounter)
    {
        _logger.LogInformation(
            "Submitting encounter for member {MemberId}, provider {ProviderNPI}, payer {PayerId}",
            SanitizeForLog(encounter.MemberId),
            SanitizeForLog(encounter.BillingProviderNPI),
            SanitizeForLog(encounter.PayerId));

        if (encounter.ServiceLines == null || encounter.ServiceLines.Count == 0)
            return BadRequest("Encounter must have at least one service line");

        encounter.TotalChargeAmount = encounter.ServiceLines!.Sum(l => l.ChargeAmount * l.Units);

        encounter.Id = Guid.NewGuid().ToString();
        encounter.Status = EncounterStatus.Pending;
        encounter.CreatedDate = DateTime.UtcNow;
        encounter.LastUpdatedDate = DateTime.UtcNow;

        if (string.IsNullOrEmpty(encounter.EncounterControlNumber))
            encounter.EncounterControlNumber = GenerateControlNumber();

        var created = await _encounterRepository.CreateAsync(encounter);

        _logger.LogInformation("Encounter {ControlNumber} submitted successfully",
            SanitizeForLog(encounter.EncounterControlNumber));

        return CreatedAtAction(nameof(GetEncounterById), new { id = created.Id }, created);
    }

    /// <summary>
    /// Get encounter by ID.
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(Encounter), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Encounter>> GetEncounterById(string id)
    {
        _logger.LogInformation("Fetching encounter by ID: {Id}", SanitizeForLog(id));

        var encounter = await _encounterRepository.GetByIdAsync(id);
        if (encounter == null)
            return NotFound($"Encounter {id} not found");

        return Ok(encounter);
    }

    /// <summary>
    /// Get encounter by control number.
    /// </summary>
    [HttpGet("control-number/{controlNumber}")]
    [ProducesResponseType(typeof(Encounter), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Encounter>> GetEncounterByControlNumber(string controlNumber)
    {
        _logger.LogInformation("Fetching encounter by control number: {ControlNumber}",
            SanitizeForLog(controlNumber));

        var encounter = await _encounterRepository.GetByControlNumberAsync(controlNumber);
        if (encounter == null)
            return NotFound($"Encounter with control number {controlNumber} not found");

        return Ok(encounter);
    }

    /// <summary>
    /// Search encounters (by member, payer, batch, date range, status).
    /// </summary>
    [HttpGet("search")]
    [ProducesResponseType(typeof(IEnumerable<Encounter>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<Encounter>>> SearchEncounters(
        [FromQuery] string? memberId = null,
        [FromQuery] string? payerId = null,
        [FromQuery] string? batchId = null,
        [FromQuery] DateTime? serviceDateFrom = null,
        [FromQuery] DateTime? serviceDateTo = null,
        [FromQuery] EncounterStatus? status = null,
        [FromQuery] SubmissionType? submissionType = null,
        [FromQuery] LineOfBusiness? lineOfBusiness = null,
        [FromQuery, Range(1, int.MaxValue)] int page = 1,
        [FromQuery, Range(1, 200)] int pageSize = 50)
    {
        _logger.LogInformation(
            "Searching encounters: member={Member}, payer={Payer}, batch={Batch}, status={Status}",
            SanitizeForLog(memberId), SanitizeForLog(payerId), SanitizeForLog(batchId), status);

        var encounters = await _encounterRepository.SearchAsync(
            memberId, payerId, batchId, serviceDateFrom, serviceDateTo,
            status, submissionType, lineOfBusiness, page, pageSize);

        return Ok(encounters);
    }

    /// <summary>
    /// Update encounter status (e.g., from 999/277CA acknowledgment).
    /// </summary>
    [HttpPut("{id}/status")]
    [ProducesResponseType(typeof(Encounter), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Encounter>> UpdateEncounterStatus(
        string id,
        [FromBody] EncounterStatusUpdate statusUpdate)
    {
        _logger.LogInformation("Updating encounter {Id} status to {Status}",
            SanitizeForLog(id), statusUpdate.Status);

        var encounter = await _encounterRepository.GetByIdAsync(id);
        if (encounter == null)
            return NotFound($"Encounter {id} not found");

        encounter.Status = statusUpdate.Status;
        encounter.LastUpdatedDate = DateTime.UtcNow;

        if (!string.IsNullOrEmpty(statusUpdate.Edi999Status))
            encounter.Edi999Status = statusUpdate.Edi999Status;

        if (statusUpdate.RejectionReasons != null && statusUpdate.RejectionReasons.Count > 0)
            encounter.RejectionReasons = statusUpdate.RejectionReasons;

        switch (statusUpdate.Status)
        {
            case EncounterStatus.Acknowledged:
                encounter.AcknowledgedDate = DateTime.UtcNow;
                break;
            case EncounterStatus.Accepted:
                encounter.AcceptedDate = DateTime.UtcNow;
                break;
            case EncounterStatus.Rejected:
                encounter.RejectedDate = DateTime.UtcNow;
                break;
        }

        if (!string.IsNullOrEmpty(statusUpdate.Notes))
        {
            encounter.Notes = string.IsNullOrEmpty(encounter.Notes)
                ? statusUpdate.Notes
                : $"{encounter.Notes}\n{DateTime.UtcNow:yyyy-MM-dd HH:mm}: {statusUpdate.Notes}";
        }

        var updated = await _encounterRepository.UpdateAsync(encounter);
        return Ok(updated);
    }

    /// <summary>
    /// Get encounter summary statistics.
    /// </summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(EncounterSummary), StatusCodes.Status200OK)]
    public async Task<ActionResult<EncounterSummary>> GetEncounterSummary(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string? payerId = null)
    {
        var fromDate = from ?? DateTime.UtcNow.AddMonths(-1);
        var toDate = to ?? DateTime.UtcNow;

        _logger.LogInformation("Fetching encounter summary from {From} to {To}, payer={PayerId}",
            fromDate, toDate, SanitizeForLog(payerId));

        var summary = await _encounterRepository.GetSummaryAsync(fromDate, toDate, payerId);
        return Ok(summary);
    }

    // ── Batch Dispatch ────────────────────────────────────────────────

    /// <summary>
    /// Create a batch of pending encounters and mark them as Queued for dispatch.
    /// Gathers all Pending encounters for the specified payer (optionally filtered by
    /// LOB and encounter type) up to the max batch size.
    /// </summary>
    [HttpPost("batch/dispatch")]
    [ProducesResponseType(typeof(BatchDispatchResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<BatchDispatchResult>> DispatchBatch(
        [FromBody] BatchDispatchRequest request)
    {
        _logger.LogInformation("Dispatching batch for payer {PayerId}, maxSize={MaxBatchSize}",
            SanitizeForLog(request.PayerId), request.MaxBatchSize);

        var pending = await _encounterRepository.GetPendingByPayerAsync(
            request.PayerId, request.LineOfBusiness, request.EncounterType, request.MaxBatchSize);

        var encounterList = pending.ToList();
        if (encounterList.Count == 0)
            return BadRequest($"No pending encounters found for payer {request.PayerId}");

        var batchId = $"BATCH-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString()[..8]}";
        var encounterIds = new List<string>();

        foreach (var encounter in encounterList)
        {
            encounter.BatchId = batchId;
            encounter.Status = EncounterStatus.Queued;
            encounter.LastUpdatedDate = DateTime.UtcNow;
            await _encounterRepository.UpdateAsync(encounter);
            encounterIds.Add(encounter.Id);
        }

        _logger.LogInformation("Batch {BatchId} created with {Count} encounters for payer {PayerId}",
            batchId, encounterList.Count, SanitizeForLog(request.PayerId));

        var result = new BatchDispatchResult
        {
            BatchId = batchId,
            PayerId = request.PayerId,
            EncounterCount = encounterList.Count,
            DispatchedDate = DateTime.UtcNow,
            EncounterIds = encounterIds
        };

        return Ok(result);
    }

    /// <summary>
    /// Mark all encounters in a batch as Submitted (called after 837 file is transmitted).
    /// </summary>
    [HttpPost("batch/{batchId}/submit")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> MarkBatchSubmitted(string batchId)
    {
        _logger.LogInformation("Marking batch {BatchId} as submitted", SanitizeForLog(batchId));

        var encounters = await _encounterRepository.SearchAsync(
            null, null, batchId, null, null, EncounterStatus.Queued, null, null, 1, 5000);

        var encounterList = encounters.ToList();
        if (encounterList.Count == 0)
            return NotFound($"No queued encounters found for batch {batchId}");

        foreach (var encounter in encounterList)
        {
            encounter.Status = EncounterStatus.Submitted;
            encounter.SubmittedDate = DateTime.UtcNow;
            encounter.LastUpdatedDate = DateTime.UtcNow;
            await _encounterRepository.UpdateAsync(encounter);
        }

        return Ok(new { batchId, submittedCount = encounterList.Count, submittedDate = DateTime.UtcNow });
    }

    // ── 837 Download ──────────────────────────────────────────────────

    /// <summary>
    /// Download the X12 837 transaction for a specific encounter.
    /// Returns the raw EDI text as a downloadable file.
    /// </summary>
    [HttpGet("{id}/837")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download837(string id)
    {
        var encounter = await _encounterRepository.GetByIdAsync(id);
        if (encounter == null)
            return NotFound($"Encounter {id} not found");

        var cfg = new Encounter837Config
        {
            InterchangeSenderId   = _configuration["Edi837:InterchangeSenderId"]   ?? "CHO",
            InterchangeReceiverId = _configuration["Edi837:InterchangeReceiverId"] ?? "RECEIVER",
            ApplicationSenderId   = _configuration["Edi837:ApplicationSenderId"]   ?? "CHO",
            ApplicationReceiverId = _configuration["Edi837:ApplicationReceiverId"] ?? "RECEIVER",
            SubmitterName         = _configuration["Edi837:SubmitterName"]         ?? "Cloud Health Office",
            SubmitterContactName  = _configuration["Edi837:SubmitterContactName"]  ?? "EDI Department",
            SubmitterContactPhone = _configuration["Edi837:SubmitterContactPhone"] ?? "5555555555",
        };

        _logger.LogInformation(
            "Generating 837 for encounter {Id} ({ControlNumber}), type={Type}",
            SanitizeForLog(id), SanitizeForLog(encounter.EncounterControlNumber), encounter.EncounterType);

        var edi = _edi837Service.Generate837(encounter, cfg);

        var typeCode = encounter.EncounterType switch
        {
            EncounterType.Professional => "837P",
            EncounterType.Institutional => "837I",
            EncounterType.Dental => "837D",
            _ => "837"
        };
        // Sanitize the control number — allow only safe filename characters to prevent header injection;
        // fall back to encounter ID if control number is missing
        var controlNumberSource = string.IsNullOrEmpty(encounter.EncounterControlNumber)
            ? encounter.Id
            : encounter.EncounterControlNumber;
        var safeControlNumber = string.Concat(
            controlNumberSource.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-'));
        var filename = $"{typeCode}_{safeControlNumber}.edi";
        Response.Headers["Content-Disposition"] = $"attachment; filename=\"{filename}\"";
        return Content(edi, "text/plain");
    }

    // ── Correction / Resubmission Flow ────────────────────────────────

    /// <summary>
    /// Submit a correction for an existing encounter.
    /// Creates a Void encounter (frequency code 8) for the original and a
    /// Replacement encounter (frequency code 7) with corrected data.
    /// Both are set to Pending status for the next batch dispatch.
    /// </summary>
    [HttpPost("{id}/correction")]
    [ProducesResponseType(typeof(CorrectionResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CorrectionResult>> SubmitCorrection(
        string id,
        [FromBody] CorrectionRequest request)
    {
        _logger.LogInformation("Submitting correction for encounter {Id}", SanitizeForLog(id));

        var original = await _encounterRepository.GetByIdAsync(id);
        if (original == null)
            return NotFound($"Encounter {id} not found");

        if (original.Status != EncounterStatus.Accepted &&
            original.Status != EncounterStatus.Rejected)
        {
            return BadRequest(
                $"Cannot correct encounter in status {original.Status}. " +
                "Only Accepted or Rejected encounters can be corrected.");
        }

        // 1. Create Void encounter (frequency code 8)
        var voidEncounter = new Encounter
        {
            Id = Guid.NewGuid().ToString(),
            EncounterControlNumber = GenerateControlNumber(),
            ClaimId = original.ClaimId,
            ClaimNumber = original.ClaimNumber,
            MemberId = original.MemberId,
            SubscriberId = original.SubscriberId,
            SubscriberFirstName = original.SubscriberFirstName,
            SubscriberLastName = original.SubscriberLastName,
            PatientFirstName = original.PatientFirstName,
            PatientLastName = original.PatientLastName,
            BillingProviderNPI = original.BillingProviderNPI,
            BillingProviderName = original.BillingProviderName,
            RenderingProviderNPI = original.RenderingProviderNPI,
            LineOfBusiness = original.LineOfBusiness,
            EncounterType = original.EncounterType,
            SubmissionType = SubmissionType.Void,
            ClaimFrequencyCode = "8",
            OriginalEncounterId = original.Id,
            OriginalEncounterControlNumber = original.EncounterControlNumber,
            PayerId = original.PayerId,
            PayerName = original.PayerName,
            PlaceOfServiceCode = original.PlaceOfServiceCode,
            TotalChargeAmount = original.TotalChargeAmount,
            ServiceDateFrom = original.ServiceDateFrom,
            ServiceDateTo = original.ServiceDateTo,
            DiagnosisCodes = original.DiagnosisCodes,
            ServiceLines = original.ServiceLines,
            Status = EncounterStatus.Pending,
            CreatedDate = DateTime.UtcNow,
            LastUpdatedDate = DateTime.UtcNow,
            Notes = $"Void for correction. Original: {original.EncounterControlNumber}. Reason: {request.CorrectionReason}"
        };

        // 2. Create Replacement encounter (frequency code 7)
        var replacement = request.CorrectedEncounter;
        replacement.Id = Guid.NewGuid().ToString();
        replacement.EncounterControlNumber = GenerateControlNumber();
        replacement.SubmissionType = SubmissionType.Correction;
        replacement.ClaimFrequencyCode = "7";
        replacement.OriginalEncounterId = original.Id;
        replacement.OriginalEncounterControlNumber = original.EncounterControlNumber;
        replacement.Status = EncounterStatus.Pending;
        replacement.CreatedDate = DateTime.UtcNow;
        replacement.LastUpdatedDate = DateTime.UtcNow;
        replacement.TotalChargeAmount = replacement.ServiceLines.Sum(l => l.ChargeAmount * l.Units);
        replacement.Notes = $"Correction for: {original.EncounterControlNumber}. Reason: {request.CorrectionReason}";

        // 3. Mark original as CorrectionSubmitted
        original.Status = EncounterStatus.CorrectionSubmitted;
        original.LastUpdatedDate = DateTime.UtcNow;
        original.Notes = string.IsNullOrEmpty(original.Notes)
            ? $"Correction submitted: {request.CorrectionReason}"
            : $"{original.Notes}\n{DateTime.UtcNow:yyyy-MM-dd HH:mm}: Correction submitted: {request.CorrectionReason}";

        await _encounterRepository.UpdateAsync(original);
        var createdVoid = await _encounterRepository.CreateAsync(voidEncounter);
        var createdReplacement = await _encounterRepository.CreateAsync(replacement);

        _logger.LogInformation(
            "Correction submitted for encounter {OriginalId}: void={VoidId}, replacement={ReplacementId}",
            SanitizeForLog(id), SanitizeForLog(createdVoid.Id), SanitizeForLog(createdReplacement.Id));

        return Ok(new CorrectionResult
        {
            VoidEncounter = createdVoid,
            ReplacementEncounter = createdReplacement
        });
    }

    /// <summary>
    /// Resubmit a rejected encounter (same data, new control number).
    /// </summary>
    [HttpPost("{id}/resubmit")]
    [ProducesResponseType(typeof(Encounter), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<Encounter>> ResubmitEncounter(string id)
    {
        _logger.LogInformation("Resubmitting encounter {Id}", SanitizeForLog(id));

        var original = await _encounterRepository.GetByIdAsync(id);
        if (original == null)
            return NotFound($"Encounter {id} not found");

        if (original.Status != EncounterStatus.Rejected)
            return BadRequest($"Cannot resubmit encounter in status {original.Status}. Only Rejected encounters can be resubmitted.");

        var resubmission = new Encounter
        {
            Id = Guid.NewGuid().ToString(),
            EncounterControlNumber = GenerateControlNumber(),
            ClaimId = original.ClaimId,
            ClaimNumber = original.ClaimNumber,
            MemberId = original.MemberId,
            SubscriberId = original.SubscriberId,
            SubscriberFirstName = original.SubscriberFirstName,
            SubscriberLastName = original.SubscriberLastName,
            PatientFirstName = original.PatientFirstName,
            PatientLastName = original.PatientLastName,
            BillingProviderNPI = original.BillingProviderNPI,
            BillingProviderName = original.BillingProviderName,
            RenderingProviderNPI = original.RenderingProviderNPI,
            LineOfBusiness = original.LineOfBusiness,
            EncounterType = original.EncounterType,
            SubmissionType = SubmissionType.Resubmission,
            ClaimFrequencyCode = "1",
            OriginalEncounterId = original.Id,
            OriginalEncounterControlNumber = original.EncounterControlNumber,
            PayerId = original.PayerId,
            PayerName = original.PayerName,
            PlaceOfServiceCode = original.PlaceOfServiceCode,
            TotalChargeAmount = original.TotalChargeAmount,
            ServiceDateFrom = original.ServiceDateFrom,
            ServiceDateTo = original.ServiceDateTo,
            DiagnosisCodes = original.DiagnosisCodes,
            ServiceLines = original.ServiceLines,
            Status = EncounterStatus.Pending,
            CreatedDate = DateTime.UtcNow,
            LastUpdatedDate = DateTime.UtcNow,
            Notes = $"Resubmission of rejected encounter: {original.EncounterControlNumber}"
        };

        var created = await _encounterRepository.CreateAsync(resubmission);

        _logger.LogInformation("Encounter {OriginalId} resubmitted as {NewId}",
            SanitizeForLog(id), SanitizeForLog(created.Id));

        return Ok(created);
    }

    /// <summary>
    /// Void an encounter (soft delete — sets status to Voided).
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> VoidEncounter(string id)
    {
        _logger.LogInformation("Voiding encounter: {Id}", SanitizeForLog(id));

        var encounter = await _encounterRepository.GetByIdAsync(id);
        if (encounter == null)
            return NotFound($"Encounter {id} not found");

        encounter.Status = EncounterStatus.Voided;
        encounter.LastUpdatedDate = DateTime.UtcNow;
        await _encounterRepository.UpdateAsync(encounter);

        return NoContent();
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static string GenerateControlNumber()
        => $"ENC-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString()[..8].ToUpperInvariant()}";

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
