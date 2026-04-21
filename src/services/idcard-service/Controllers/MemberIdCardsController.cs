using IdCardService.Models;
using IdCardService.Services;
using Microsoft.AspNetCore.Mvc;

namespace IdCardService.Controllers;

[Route("api/v1/members/{memberId}/id-cards")]
public class MemberIdCardsController : TenantAwareControllerBase
{
    private readonly IIdCardOrchestrator _orchestrator;

    public MemberIdCardsController(IIdCardOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    [HttpGet]
    public async Task<ActionResult<List<IdCardHistoryEntry>>> List(string memberId, CancellationToken ct)
    {
        var records = await _orchestrator.ListForMemberAsync(TenantId, memberId, ct);
        var history = records.Select(r => new IdCardHistoryEntry
        {
            CardId = r.CardId,
            OrderId = r.OrderId,
            DocumentId = r.DocumentId,
            PreviewDocumentId = r.PreviewDocumentId,
            PlanId = r.PlanId,
            SponsorId = r.SponsorId,
            LanguageCode = r.LanguageCode,
            IssuedAt = r.IssuedAt,
            RevokedAt = r.RevokedAt,
            RevocationReason = r.RevocationReason?.ToString(),
            ScanCount = r.ScanCount
        }).ToList();
        return Ok(history);
    }
}
