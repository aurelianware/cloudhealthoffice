using FhirService.Models.PayerToPayer;
using Microsoft.Extensions.Logging;

namespace FhirService.Services.PayerToPayer;

/// <summary>
/// Application service for inbound Payer-to-Payer respond (CMS-0057-F P2P-01).
/// Cloud Health Office, as the prior payer, matches the transitioning member and
/// produces an authorized, member-scoped FHIR data package from its own
/// authoritative data.
///
/// Order of operations (fail closed):
///   1. resolve the member (tenant-scoped, deterministic, safe)
///   2. enforce the member's opt-in authorization
///   3. assemble the export from CHO-owned data
///   4. record an audit entry
///
/// This is the domain/application layer — the FHIR controller is a thin routing
/// surface over it, so core P2P logic never lives in the controller.
/// </summary>
public interface IPayerToPayerExchangeService
{
    Task<PayerToPayerExportResult> RespondAsync(
        PayerToPayerExchangeRequest request, CancellationToken ct = default);
}

public sealed class PayerToPayerExchangeService : IPayerToPayerExchangeService
{
    private readonly IPayerToPayerMemberResolver _resolver;
    private readonly IPayerToPayerMemberSource _source;
    private readonly IPayerToPayerExportBuilder _builder;
    private readonly ILogger<PayerToPayerExchangeService> _logger;

    public PayerToPayerExchangeService(
        IPayerToPayerMemberResolver resolver,
        IPayerToPayerMemberSource source,
        IPayerToPayerExportBuilder builder,
        ILogger<PayerToPayerExchangeService> logger)
    {
        _resolver = resolver;
        _source = source;
        _builder = builder;
        _logger = logger;
    }

    public async Task<PayerToPayerExportResult> RespondAsync(
        PayerToPayerExchangeRequest request, CancellationToken ct = default)
    {
        var resolution = await _resolver.ResolveAsync(request, ct);
        if (resolution.Outcome != PayerToPayerOutcome.Exported || resolution.Member is null)
            return Failed(request, resolution.Outcome, matchedMemberId: null);

        var member = resolution.Member;

        // Consent / authorization gate — the member must have opted in. Enforced,
        // never bypassed. This uses the generic active opt-in signal; it does not
        // introduce a dedicated Payer-to-Payer ConsentType (P2P-03 stays PARTIAL).
        if (!request.MemberOptedIn)
            return Failed(request, PayerToPayerOutcome.NotAuthorized, member.MemberId);

        var payments = await _source.GetPaymentsAsync(request.TenantId, member.MemberId, ct);
        var bundle = _builder.Build(member, payments, request);

        var audit = Audit(request, PayerToPayerOutcome.Exported, member.MemberId, bundle.Total);
        _logger.LogInformation(
            "P2P respond: tenant={Tenant} receivingPayer={Payer} member={Member} resources={Count}",
            audit.TenantId, audit.ReceivingPayerId, audit.MatchedMemberId, audit.ResourceCount);

        return new PayerToPayerExportResult
        {
            Outcome = PayerToPayerOutcome.Exported,
            MatchedMemberId = member.MemberId,
            Bundle = bundle,
            Audit = audit,
        };
    }

    private PayerToPayerExportResult Failed(
        PayerToPayerExchangeRequest request, PayerToPayerOutcome outcome, string? matchedMemberId)
    {
        var audit = Audit(request, outcome, matchedMemberId, resourceCount: 0);
        _logger.LogInformation(
            "P2P respond declined: tenant={Tenant} receivingPayer={Payer} outcome={Outcome}",
            audit.TenantId, audit.ReceivingPayerId, audit.Outcome);
        return new PayerToPayerExportResult { Outcome = outcome, MatchedMemberId = matchedMemberId, Audit = audit };
    }

    private static PayerToPayerAuditEntry Audit(
        PayerToPayerExchangeRequest request, PayerToPayerOutcome outcome, string? matchedMemberId, int resourceCount) =>
        new()
        {
            TenantId = request.TenantId,
            ReceivingPayerId = request.ReceivingPayerId,
            InitiatedBy = request.InitiatedBy,
            MatchedMemberId = matchedMemberId,
            Outcome = outcome.ToString(),
            ResourceCount = resourceCount,
        };
}
