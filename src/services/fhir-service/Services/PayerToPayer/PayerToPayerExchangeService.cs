using CloudHealthOffice.Consent.Contracts;
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
    private readonly IPayerToPayerConsentGate _consentGate;
    private readonly IPayerToPayerExportBuilder _builder;
    private readonly ILogger<PayerToPayerExchangeService> _logger;

    public PayerToPayerExchangeService(
        IPayerToPayerMemberResolver resolver,
        IPayerToPayerMemberSource source,
        IPayerToPayerConsentGate consentGate,
        IPayerToPayerExportBuilder builder,
        ILogger<PayerToPayerExchangeService> logger)
    {
        _resolver = resolver;
        _source = source;
        _consentGate = consentGate;
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

        // Consent / authorization gate — the member must have an active consent
        // whose PURPOSE authorizes Payer-to-Payer exchange. Decided server-side
        // from the plan's own registry (never a value supplied on the request),
        // so a receiving payer cannot self-attest consent, and a consent granted
        // for some other purpose (Provider Access, say) does not open this door.
        var decision = await _consentGate.EvaluateAsync(
            request.TenantId, member.MemberId, request.ExchangeDateUtc, ct);
        if (!decision.Allowed)
            return Failed(request, PayerToPayerOutcome.NotAuthorized, member.MemberId, decision);

        var payments = await _source.GetPaymentsAsync(request.TenantId, member.MemberId, ct);
        var bundle = _builder.Build(member, payments, request);

        var audit = Audit(request, PayerToPayerOutcome.Exported, member.MemberId, bundle.Total, decision);
        _logger.LogInformation(
            "P2P respond: tenant={Tenant} receivingPayer={Payer} member={Member} resources={Count}",
            Clean(audit.TenantId), Clean(audit.ReceivingPayerId), Clean(audit.MatchedMemberId), audit.ResourceCount);

        return new PayerToPayerExportResult
        {
            Outcome = PayerToPayerOutcome.Exported,
            MatchedMemberId = member.MemberId,
            Bundle = bundle,
            Audit = audit,
        };
    }

    private PayerToPayerExportResult Failed(
        PayerToPayerExchangeRequest request, PayerToPayerOutcome outcome, string? matchedMemberId,
        ConsentDecision? decision = null)
    {
        var audit = Audit(request, outcome, matchedMemberId, resourceCount: 0, decision);
        _logger.LogInformation(
            "P2P respond declined: tenant={Tenant} receivingPayer={Payer} outcome={Outcome} consent={Consent}",
            Clean(audit.TenantId), Clean(audit.ReceivingPayerId), audit.Outcome,
            audit.ConsentDecisionReason ?? "n/a");
        return new PayerToPayerExportResult { Outcome = outcome, MatchedMemberId = matchedMemberId, Audit = audit };
    }

    /// <summary>
    /// Strips CR/LF from caller-supplied values before they reach a log entry,
    /// preventing log-forging / injection (CWE-117) from the exchange request.
    /// </summary>
    private static string Clean(string? value)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("\r", string.Empty, StringComparison.Ordinal)
                   .Replace("\n", string.Empty, StringComparison.Ordinal);

    private static PayerToPayerAuditEntry Audit(
        PayerToPayerExchangeRequest request, PayerToPayerOutcome outcome, string? matchedMemberId,
        int resourceCount, ConsentDecision? decision) =>
        new()
        {
            TenantId = request.TenantId,
            ReceivingPayerId = request.ReceivingPayerId,
            InitiatedBy = request.InitiatedBy,
            MatchedMemberId = matchedMemberId,
            Outcome = outcome.ToString(),
            ResourceCount = resourceCount,
            // Which authorization answered, and how. Opaque id + reason code: it
            // makes "why was this disclosure allowed?" answerable without putting
            // any consent content in the audit trail.
            AuthorizingConsentId = decision?.ConsentId,
            ConsentDecisionReason = decision?.Reason.ToString(),
        };
}
