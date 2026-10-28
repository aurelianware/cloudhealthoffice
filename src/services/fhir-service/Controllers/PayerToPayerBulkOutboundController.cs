using FhirService.Models.PayerToPayer;
using FhirService.Services.Identity;
using FhirService.Services.PayerToPayer.Outbound.Bulk;
using FhirService.Services.ProviderAccess;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FhirService.Controllers;

/// <summary>
/// Outbound bulk Payer-to-Payer (G-35): CHO, as the new payer, asks one prior
/// payer for many transitioning members' data at once over the PDex bulk
/// operations. <c>POST PayerToPayer/$bulk-initiate</c> records the job and
/// answers 202 with its status URL; the worker does the rest
/// (<see cref="IPayerToPayerBulkOutboundService"/>).
///
/// The same rules as <c>PayerToPayer/$initiate</c>: the payer is named by id
/// from the trusted directory (never a URL), the tenant is the caller's, and
/// each member's opt-in is decided server-side. Staff or system callers only:
/// a patient-scoped token speaks for one member, and a bulk request is for
/// many. Served when <c>Cms0057:PayerToPayerOutbound:Bulk:Enabled</c>;
/// otherwise 501.
/// </summary>
[Route("fhir/r4")]
[FhirAccess(smart: true, cho: "payer-to-payer:initiate")]
public sealed class PayerToPayerBulkOutboundController : FhirControllerBase
{
    [HttpPost("PayerToPayer/$bulk-initiate")]
    public async Task<IActionResult> Initiate([FromBody] PayerToPayerBulkInitiateRequestDto? body, CancellationToken ct)
    {
        if (Service is not { } service) return NotServed();
        if (SmartPatientId is not null)
            return Forbidden("A patient-scoped token may not initiate a bulk Payer-to-Payer exchange.");
        if (body is null)
            return FhirBadRequest("A bulk Payer-to-Payer initiation request body is required.");
        if (string.IsNullOrWhiteSpace(body.TargetPayerId))
            return FhirBadRequest(
                "targetPayerId is required. It names a payer in the configured Payer-to-Payer directory; "
                + "endpoint locations are never accepted from a request.");

        var result = await service.StartAsync(new BulkOutboundStartRequest(
            TenantId, body.TargetPayerId, body.MemberIds ?? [], body.AsOfDate,
            InitiatedBy: ProviderAccessAuthorizationFilter.ResolveCallerId(HttpContext) is { Length: > 0 } client
                ? $"client:{client}"
                : "system"), ct);

        return result.Refusal switch
        {
            BulkOutboundRefusal.None => AcceptedJob(result.Job!),
            BulkOutboundRefusal.NoMembers => FhirBadRequest("memberIds must name at least one member."),
            BulkOutboundRefusal.TooManyMembers => FhirBadRequest(
                "memberIds names more members than one bulk request may carry; split the request."),
            BulkOutboundRefusal.TargetPayerNotConfigured => FhirUnprocessable(
                "No Payer-to-Payer endpoint is configured for the requested payer in this tenant."),
            _ => FhirNotFound("Tenant", "unknown"),
        };
    }

    [HttpGet("PayerToPayer/$bulk-initiate/{jobId}")]
    public async Task<IActionResult> Status(string jobId, CancellationToken ct)
    {
        if (Service is not { } service) return NotServed();
        if (SmartPatientId is not null)
            return Forbidden("A patient-scoped token may not read a bulk Payer-to-Payer exchange.");

        return await service.GetAsync(TenantId, jobId, ct) is { } job
            ? Ok(Receipt(job))
            : FhirNotFound("bulk Payer-to-Payer exchange", jobId);
    }

    private IPayerToPayerBulkOutboundService? Service
        => HttpContext.RequestServices.GetService<IOptions<PayerToPayerBulkOutboundOptions>>()?.Value.Enabled == true
            ? HttpContext.RequestServices.GetService<IPayerToPayerBulkOutboundService>()
            : null;

    private IActionResult AcceptedJob(PayerToPayerBulkOutboundJob job)
    {
        Response.Headers["Content-Location"] = $"{FhirBaseUrl}/PayerToPayer/$bulk-initiate/{job.JobId}";
        return StatusCode(StatusCodes.Status202Accepted, Receipt(job));
    }

    /// <summary>
    /// The job as it stands: its status, and each member's outcome by CHO
    /// member id. The payer's URLs and the members' data are not shown.
    /// </summary>
    private static Parameters Receipt(PayerToPayerBulkOutboundJob job)
    {
        var parameters = new Parameters();
        parameters.Add("jobId", new FhirString(job.JobId));
        parameters.Add("status", new FhirString(job.Status.ToString()));
        if (job.Failure != PayerToPayerBulkOutboundFailure.None)
            parameters.Add("failure", new FhirString(job.Failure.ToString()));
        parameters.Add("targetPayerId", new FhirString(job.TargetPayerId));
        parameters.Add("createdAt", new FhirDateTime(job.CreatedAt));
        if (job.CompletedAt is { } completed) parameters.Add("completedAt", new FhirDateTime(completed));

        foreach (var member in job.Members)
        {
            var entry = new Parameters.ParameterComponent { Name = "member" };
            entry.Part.Add(new() { Name = "memberId", Value = new FhirString(member.MemberId) });
            entry.Part.Add(new() { Name = "outcome", Value = new FhirString(member.Outcome.ToString()) });
            if (member.Outcome == PayerToPayerBulkMemberOutcome.Completed)
            {
                entry.Part.Add(new() { Name = "resourceCount", Value = new Integer(member.ReceivedResourceCount) });
                entry.Part.Add(new() { Name = "persistedCount", Value = new Integer(member.PersistedResourceCount) });
            }
            parameters.Parameter.Add(entry);
        }
        return parameters;
    }

    private IActionResult Forbidden(string diagnostics) => StatusCode(403, Outcome(OperationOutcome.IssueType.Forbidden, diagnostics));

    private static IActionResult NotServed()
        => new ObjectResult(Outcome(OperationOutcome.IssueType.NotSupported,
            "Outbound bulk Payer-to-Payer is not enabled on this server.")) { StatusCode = StatusCodes.Status501NotImplemented };

    private static OperationOutcome Outcome(OperationOutcome.IssueType code, string diagnostics) => new()
    {
        Issue =
        [
            new OperationOutcome.IssueComponent
            {
                Severity = OperationOutcome.IssueSeverity.Error,
                Code = code,
                Diagnostics = diagnostics,
            },
        ],
    };
}

/// <summary>
/// Request body for <c>PayerToPayer/$bulk-initiate</c>: which configured payer,
/// which members. No URL, no consent, no tenant.
/// </summary>
public sealed class PayerToPayerBulkInitiateRequestDto
{
    public string? TargetPayerId { get; set; }

    /// <summary>The CHO members to ask the payer for.</summary>
    public List<string>? MemberIds { get; set; }

    /// <summary>Date the members' coverage with the payer is matched as of (yyyy-MM-dd).</summary>
    public string? AsOfDate { get; set; }
}
