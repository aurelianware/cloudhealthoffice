using CloudHealthOffice.Consent.Contracts;
using FhirService.Models.PayerToPayer;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Infrastructure.Logging;

namespace FhirService.Services.PayerToPayer;

/// <summary>
/// Application service for inbound Payer-to-Payer respond (CMS-0057-F P2P-01).
/// Cloud Health Office, as the prior payer, matches the transitioning member and
/// produces an authorized, member-scoped FHIR data package from its own
/// authoritative data.
///
/// Order of operations (fail closed):
///   1. resolve the member (tenant-scoped, deterministic, safe)
///   2. enforce the member's consent, bound to the requesting payer (ADR 022):
///      the HRex consent that payer supplied, with this request or with its
///      earlier <c>$member-match</c>
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
    private readonly IPayerToPayerExportSource _exportSource;
    private readonly IPayerToPayerConsentAttestationStore _attestations;
    private readonly string _localPayerId;
    private readonly TimeProvider _time;
    private readonly IPayerToPayerExportBuilder _builder;
    private readonly ILogger<PayerToPayerExchangeService> _logger;

    public PayerToPayerExchangeService(
        IPayerToPayerMemberResolver resolver,
        IPayerToPayerExportSource exportSource,
        IPayerToPayerConsentAttestationStore attestations,
        Microsoft.Extensions.Options.IOptions<Outbound.PayerToPayerDirectoryOptions> directory,
        TimeProvider time,
        IPayerToPayerExportBuilder builder,
        ILogger<PayerToPayerExchangeService> logger)
    {
        _resolver = resolver;
        _exportSource = exportSource;
        _attestations = attestations;
        _localPayerId = directory.Value.LocalPayerId;
        _time = time;
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

        // Consent, bound to the requesting payer (D-13, ADR 022). Under
        // CMS-0057-F the requesting payer collects the member's opt-in; CHO
        // honours it only as an HRex consent naming that payer (the client's
        // registered payer, never a body value), supplied with this request or
        // recorded from the same payer's earlier $member-match. Another payer's
        // consent for the same member opens nothing here.
        //
        // Judged as of NOW, deliberately NOT request.ExchangeDateUtc: that field
        // anchors the lookback window and arrives on the request, so using it
        // would let a caller choose the instant its authorization is judged at.
        var now = _time.GetUtcNow().UtcDateTime;
        PayerToPayerConsentAttestation? attestation;
        if (request.Consent is not null)
        {
            var supplied = PdexConsentValidator.Validate(request.Consent, request.ReceivingPayerId, _localPayerId, now);
            if (!supplied.Valid)
                return Failed(request, PayerToPayerOutcome.NotAuthorized, member.MemberId,
                    ConsentDecision.Deny(ConsentPurposeOfUse.PayerToPayerExchange,
                        ConsentAuthorizationReason.NoConsentForPurpose, now),
                    supplied.Failure);
            attestation = new PayerToPayerConsentAttestation
            {
                TenantId = request.TenantId,
                MemberId = member.MemberId,
                ReceivingPayerId = request.ReceivingPayerId,
                Policy = supplied.Policy!,
                PeriodStartUtc = supplied.PeriodStartUtc!.Value,
                PeriodEndUtc = supplied.PeriodEndUtc,
                ConsentFingerprint = supplied.Fingerprint!,
                RecordedAtUtc = now,
                RecordedBy = request.InitiatedBy ?? "unknown",
            };
            await _attestations.RecordAsync(attestation, ct);
        }
        else
        {
            attestation = await _attestations.FindInForceAsync(
                request.TenantId, member.MemberId, request.ReceivingPayerId, now, ct);
            if (attestation is null)
                return Failed(request, PayerToPayerOutcome.NotAuthorized, member.MemberId,
                    ConsentDecision.Deny(ConsentPurposeOfUse.PayerToPayerExchange,
                        ConsentAuthorizationReason.NoConsentOnRecord, now));
        }

        var decision = new ConsentDecision
        {
            Allowed = true,
            Reason = ConsentAuthorizationReason.Granted,
            PurposeOfUse = ConsentPurposeOfUse.PayerToPayerExchange,
            ConsentId = attestation.Id,
            EvaluatedAtUtc = now,
        };

        var resources = await _exportSource.GetResourcesAsync(member, request, ct);
        var bundle = _builder.Build(member, resources, request);

        var audit = Audit(request, PayerToPayerOutcome.Exported, member.MemberId, bundle.Total, decision);
        _logger.LogInformation(
            "P2P respond: tenant={Tenant} receivingPayer={Payer} member={Member} resources={Count}",
            LogSanitizer.Sanitize(audit.TenantId), LogSanitizer.Sanitize(audit.ReceivingPayerId), LogSanitizer.MaskId(audit.MatchedMemberId), audit.ResourceCount);

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
        ConsentDecision? decision = null, PdexConsentFailure? consentFailure = null)
    {
        var audit = Audit(request, outcome, matchedMemberId, resourceCount: 0, decision, consentFailure);
        _logger.LogInformation(
            "P2P respond declined: tenant={Tenant} receivingPayer={Payer} outcome={Outcome} consent={Consent}",
            LogSanitizer.Sanitize(audit.TenantId), LogSanitizer.Sanitize(audit.ReceivingPayerId), audit.Outcome,
            audit.ConsentDecisionReason ?? "n/a");
        return new PayerToPayerExportResult { Outcome = outcome, MatchedMemberId = matchedMemberId, Audit = audit };
    }

    private static PayerToPayerAuditEntry Audit(
        PayerToPayerExchangeRequest request, PayerToPayerOutcome outcome, string? matchedMemberId,
        int resourceCount, ConsentDecision? decision, PdexConsentFailure? consentFailure = null) =>
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
            ConsentFailure = consentFailure?.ToString(),
        };
}
