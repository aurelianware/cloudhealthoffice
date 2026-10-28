using System.Text.Json.Nodes;
using FhirService.Services.BulkExport;
using FhirService.Services.Identity;
using FhirService.Services.PayerToPayer.BulkMemberMatch;
using FhirService.Services.ProviderAccess;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FhirService.Controllers;

/// <summary>
/// PDex 2.1 bulk Payer-to-Payer member match (G-35):
/// <c>POST Group/$bulk-member-match</c>, the Groups it answers with
/// (<c>GET Group/{id}</c>), and the answer again by its status URL. The
/// matched Group is then exported with <c>Group/{id}/$davinci-data-export</c>
/// (<see cref="BulkExportController"/>).
///
/// Served with bulk export (<c>BulkExport:Enabled</c>), since its only use is
/// the export that follows; otherwise answered 501. Backend payer clients
/// only, holding the explicit <c>system/PayerToPayer.read</c> grant and bound
/// to the payer they are registered to, exactly as <c>$member-match</c>. A
/// Group is answered only to the client that asked for it, while still
/// registered to that payer; to anyone else it does not exist.
/// </summary>
[Route("fhir/r4")]
[FhirAccess(smart: true, cho: null)]
public sealed class PayerToPayerBulkMemberMatchController : FhirControllerBase
{
    private readonly ILogger<PayerToPayerBulkMemberMatchController> _logger;

    public PayerToPayerBulkMemberMatchController(ILogger<PayerToPayerBulkMemberMatchController> logger)
        => _logger = logger;

    /// <summary>
    /// POST /fhir/r4/Group/$bulk-member-match. With <c>Prefer: respond-async</c>
    /// (the form PDex requires) the answer is 202 with a status URL that returns
    /// it; without, the Parameters are answered at once.
    /// </summary>
    [HttpPost("Group/$bulk-member-match")]
    public async Task<IActionResult> BulkMemberMatch([FromBody] JsonObject? body, CancellationToken ct)
    {
        if (!TryCaller(out var caller, out var refusal))
            return refusal;
        if (Service is not { } service || Options is not { } options)
            return NotServed();

        if (!BulkMemberMatchRequestReader.TryRead(body, options.MaxMembersPerMatch, out var members, out var error))
            return FhirUnprocessable(error!);

        var answer = await service.MatchAsync(caller, members, ct);
        _logger.LogInformation(
            "P2P bulk member-match {RequestId}: {Members} members for the requesting payer.",
            answer.RequestId, members.Count);

        if (!RespondAsync)
            return Fhir(answer.Parameters);

        Response.Headers["Content-Location"] = $"{FhirBaseUrl}/Group/$bulk-member-match/{answer.RequestId}";
        return StatusCode(StatusCodes.Status202Accepted);
    }

    /// <summary>GET /fhir/r4/Group/$bulk-member-match/{requestId} — the answer of an earlier request.</summary>
    [HttpGet("Group/$bulk-member-match/{requestId}")]
    public async Task<IActionResult> BulkMemberMatchStatus(string requestId, CancellationToken ct)
    {
        if (!TryCaller(out var caller, out var refusal))
            return refusal;
        if (Service is not { } service)
            return NotServed();

        // The match ran when it was asked for, so a request that exists is complete.
        return await service.GetAnswerAsync(caller, requestId, ct) is { } answer
            ? Fhir(answer)
            : FhirNotFound("$bulk-member-match request", requestId);
    }

    /// <summary>GET /fhir/r4/Group/{id} — one of the caller's match Groups.</summary>
    [HttpGet("Group/{groupId}")]
    public async Task<IActionResult> ReadGroup(string groupId, CancellationToken ct)
    {
        if (!TryCaller(out var caller, out var refusal))
            return refusal;
        if (Service is not { } service)
            return NotServed();

        return await service.GetGroupAsync(caller, groupId, ct) is { } group
            ? Fhir(JsonNode.Parse(group.Resource)!.AsObject())
            : FhirNotFound("Group", groupId);
    }

    private IPayerToPayerBulkMemberMatchService? Service
        => HttpContext.RequestServices.GetService<IPayerToPayerBulkMemberMatchService>();

    private BulkExportOptions? Options
        => HttpContext.RequestServices.GetService<IOptions<BulkExportOptions>>()?.Value;

    /// <summary>
    /// A backend client registered to a payer, which is the payer the match is
    /// for (PDex carries no requesting-payer parameter). Refused before the
    /// body is read, so the answer does not depend on the members named.
    /// </summary>
    private bool TryCaller(out BulkMemberMatchCaller caller, out IActionResult refusal)
    {
        caller = null!;
        refusal = null!;
        if (!IsBackendSystemCaller)
        {
            refusal = FhirForbidden(PayerToPayerController.BackendOnlyMessage);
            return false;
        }
        var client = ProviderAccessAuthorizationFilter.ResolveCallerId(HttpContext);
        if (!TryBindReceivingPayer(null, _logger, "bulk member-match", out var receivingPayerId)
            || string.IsNullOrEmpty(client))
        {
            refusal = FhirForbidden(PayerToPayerController.PayerBindingMessage);
            return false;
        }
        caller = new BulkMemberMatchCaller(TenantId, receivingPayerId, client);
        return true;
    }

    private bool RespondAsync
        => Request.Headers.TryGetValue("Prefer", out var prefer)
           && prefer.Any(v => v is not null && v.Contains("respond-async", StringComparison.OrdinalIgnoreCase));

    private static ContentResult Fhir(JsonObject resource) => new()
    {
        Content = resource.ToJsonString(),
        ContentType = "application/fhir+json",
        StatusCode = StatusCodes.Status200OK,
    };

    private static IActionResult NotServed()
    {
        var result = new ObjectResult(new Hl7.Fhir.Model.OperationOutcome
        {
            Issue =
            [
                new()
                {
                    Severity = Hl7.Fhir.Model.OperationOutcome.IssueSeverity.Error,
                    Code = Hl7.Fhir.Model.OperationOutcome.IssueType.NotSupported,
                    Diagnostics = "Bulk member match is served with bulk export, which is not enabled.",
                },
            ],
        }) { StatusCode = StatusCodes.Status501NotImplemented };
        result.ContentTypes.Add("application/fhir+json");
        return result;
    }
}
