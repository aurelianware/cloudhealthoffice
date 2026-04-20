using IdCardService.Models;
using IdCardService.Services;
using Microsoft.AspNetCore.Mvc;

namespace IdCardService.Controllers;

[Route("api/v1/id-cards")]
public class IdCardOrdersController : TenantAwareControllerBase
{
    private readonly IIdCardOrchestrator _orchestrator;
    private readonly ILogger<IdCardOrdersController> _logger;

    public IdCardOrdersController(IIdCardOrchestrator orchestrator, ILogger<IdCardOrdersController> logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
    }

    /// <summary>Order (issue) an ID card for a member.</summary>
    [HttpPost("orders")]
    public async Task<ActionResult<IdCardOrderResponse>> Create(
        [FromBody] CreateIdCardOrderRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.MemberId))
        {
            return BadRequest(new { error = "memberId is required" });
        }

        var order = await _orchestrator.CreateOrderAsync(TenantId, request, ct);
        var response = ToResponse(order);

        // 202 for the async order semantic — Phase 1 completes synchronously
        // but callers should still treat the response as a status resource.
        return AcceptedAtAction(nameof(Get), new { orderId = order.Id }, response);
    }

    /// <summary>Get the status of an order.</summary>
    [HttpGet("{orderId}")]
    public async Task<ActionResult<IdCardOrderResponse>> Get(string orderId, CancellationToken ct)
    {
        var order = await _orchestrator.GetOrderAsync(TenantId, orderId, ct);
        if (order == null) return NotFound();
        return Ok(ToResponse(order));
    }

    /// <summary>Revoke an issued ID card.</summary>
    [HttpPost("{cardId}/revoke")]
    public async Task<IActionResult> Revoke(string cardId, [FromBody] RevokeIdCardRequest request, CancellationToken ct)
    {
        var record = await _orchestrator.RevokeAsync(TenantId, cardId, request, ct);
        if (record == null) return NotFound();
        return Ok(new
        {
            cardId = record.CardId,
            revokedAt = record.RevokedAt,
            reason = record.RevocationReason?.ToString()
        });
    }

    private static IdCardOrderResponse ToResponse(IdCardOrder order) => new()
    {
        OrderId = order.Id,
        Status = order.Status.ToString(),
        CardId = order.CardId,
        DocumentId = order.DocumentId,
        PreviewDocumentId = order.PreviewDocumentId,
        FailureReason = order.FailureReason,
        FailureCode = order.FailureCode,
        CreatedAt = order.CreatedAt,
        UpdatedAt = order.UpdatedAt,
        IssuedAt = order.IssuedAt
    };
}
