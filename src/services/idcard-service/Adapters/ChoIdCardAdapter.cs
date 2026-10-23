using CloudHealthOffice.PlanConfiguration;
using IdCardService.Models;
using IdCardService.Services;
using CloudHealthOffice.Infrastructure.Logging;

namespace IdCardService.Adapters;

/// <summary>
/// Default adapter: drives the internal CHO generator pipeline. Fans out
/// member/coverage/sponsor/plan lookups in parallel, resolves a template,
/// renders PDF + PNG, uploads to member-document-service, and persists an
/// <see cref="IdCardRecord"/>.
/// </summary>
public class ChoIdCardAdapter : IIdCardAdapter
{
    public string Platform => "cho";

    private readonly IMemberClient _memberClient;
    private readonly ICoverageClient _coverageClient;
    private readonly ISponsorClient _sponsorClient;
    private readonly IBenefitPlanClient _benefitPlanClient;
    private readonly ITemplateResolver _templateResolver;
    private readonly IQrCodeService _qr;
    private readonly IIdCardGenerator _generator;
    private readonly IMemberDocumentClient _documents;
    private readonly ICarrierDirectory? _carriers;
    private readonly ILogger<ChoIdCardAdapter> _logger;

    public ChoIdCardAdapter(
        IMemberClient memberClient,
        ICoverageClient coverageClient,
        ISponsorClient sponsorClient,
        IBenefitPlanClient benefitPlanClient,
        ITemplateResolver templateResolver,
        IQrCodeService qr,
        IIdCardGenerator generator,
        IMemberDocumentClient documents,
        ILogger<ChoIdCardAdapter> logger,
        ICarrierDirectory? carriers = null)
    {
        _memberClient = memberClient;
        _coverageClient = coverageClient;
        _sponsorClient = sponsorClient;
        _benefitPlanClient = benefitPlanClient;
        _templateResolver = templateResolver;
        _qr = qr;
        _generator = generator;
        _documents = documents;
        _carriers = carriers;
        _logger = logger;
    }

    public async Task<IdCardIssueResult> IssueAsync(IdCardIssueRequest request, CancellationToken ct = default)
    {
        var tenantId = request.TenantId;
        var memberId = request.MemberId;

        // Steps 1–2 run in parallel first: we need GroupNumber/PlanId (from coverage)
        // and the member demographics concurrently. Sponsor and plan lookups require
        // values from the coverage response, so they run in the second wave.
        var memberTask = _memberClient.GetAsync(tenantId, memberId, ct);
        var coverageTask = _coverageClient.GetActiveAsync(tenantId, memberId, ct);

        await Task.WhenAll(memberTask, coverageTask);

        var member = await memberTask;
        var coverage = await coverageTask;

        if (member == null)
        {
            return Fail("MEMBER_NOT_FOUND", $"Member {memberId} not found");
        }
        if (coverage == null)
        {
            return Fail("COVERAGE_NOT_ACTIVE", $"No active coverage for member {memberId}");
        }

        // Steps 3–4 run in parallel.
        var groupNumber = coverage.GroupNumber;
        var planId = coverage.PlanId;
        var sponsorTask = string.IsNullOrEmpty(groupNumber)
            ? Task.FromResult<SponsorDto?>(null)
            : _sponsorClient.GetAsync(tenantId, groupNumber, ct);
        var planTask = string.IsNullOrEmpty(planId)
            ? Task.FromResult<BenefitPlanDto?>(null)
            : _benefitPlanClient.GetAsync(tenantId, planId, ct);
        // The issuing entity, in the same wave: it needs nothing from the
        // coverage, and an unreachable tenant-service fails the order rather
        // than issuing a card that names the wrong issuer
        // (plan-configuration.md §12.4).
        var carrierTask = _carriers is null
            ? Task.FromResult<CarrierIdentity?>(null)
            : _carriers.DefaultAsync(tenantId, ct);

        await Task.WhenAll(sponsorTask, planTask, carrierTask);

        var sponsor = await sponsorTask;
        var plan = await planTask;
        var carrier = await carrierTask;

        var languageCode = request.LanguageCode ?? member.PreferredLanguage ?? "en-US";
        var template = await _templateResolver.ResolveAsync(tenantId, coverage.GroupNumber, coverage.PlanId, languageCode, ct);
        if (template == null)
        {
            // Per Phase-1 policy, the global fallback must exist. Missing it is
            // a deployment-time misconfiguration surfaced by the startup health
            // check; at runtime we fail the order with a clear code.
            return Fail("NO_TEMPLATE_AVAILABLE",
                $"No template resolved for sponsor={coverage.GroupNumber ?? "-"}, plan={coverage.PlanId ?? "-"}, lang={languageCode}; global fallback missing");
        }

        var cardId = Guid.NewGuid().ToString("N");
        var issuedAt = DateTime.UtcNow;

        var (qrPng, qrPayloadString, keyVersion, canonical) = await _qr.GenerateAsync(
            tenantId, memberId, cardId, issuedAt, ct);

        var bindings = BuildBindings(member, coverage, sponsor, plan, carrier, cardId, issuedAt, languageCode);

        var rendered = await _generator.RenderAsync(template, bindings, qrPng, ct);

        var pdfDocId = await _documents.UploadPdfAsync(
            tenantId, memberId, rendered.Pdf,
            fileName: $"id-card-{cardId}.pdf",
            category: "IdCard",
            subcategory: template.Id,
            uploadedBy: request.RequestedBy ?? "idcard-service",
            ct);

        string? previewDocId = null;
        if (rendered.Png is { Length: > 0 })
        {
            previewDocId = await _documents.UploadPngAsync(
                tenantId, memberId, rendered.Png,
                fileName: $"id-card-{cardId}.png",
                category: "IdCard",
                subcategory: template.Id + ":preview",
                uploadedBy: request.RequestedBy ?? "idcard-service",
                ct);
        }

        var record = new IdCardRecord
        {
            TenantId = tenantId,
            MemberId = memberId,
            OrderId = request.OrderId,
            CardId = cardId,
            TemplateId = template.Id,
            SponsorId = coverage.GroupNumber,
            PlanId = coverage.PlanId,
            LanguageCode = languageCode,
            DocumentId = pdfDocId,
            PreviewDocumentId = previewDocId,
            KeyVersion = keyVersion,
            QrCanonicalPayload = canonical,
            IssuedAt = issuedAt
        };

        _logger.LogInformation(
            "Issued ID card {CardId} for member {MemberId} template {TemplateId} doc {DocId}",
            cardId, LogSanitizer.MaskId(memberId), template.Id, pdfDocId);

        return new IdCardIssueResult { Success = true, Record = record };
    }

    private static CardBindings BuildBindings(
        MemberDto member, CoverageDto coverage, SponsorDto? sponsor, BenefitPlanDto? plan,
        CarrierIdentity? carrier, string cardId, DateTime issuedAt, string languageCode)
    {
        return new CardBindings
        {
            MemberId = member.MemberId,
            MemberNumber = member.MemberNumber ?? member.MemberId,
            MemberName = string.Join(' ', new[] { member.FirstName, member.LastName }.Where(s => !string.IsNullOrEmpty(s))),
            DateOfBirth = member.DateOfBirth,
            Gender = member.Gender,

            GroupNumber = coverage.GroupNumber,
            SponsorPolicyNumber = coverage.SponsorPolicyNumber,
            SponsorName = sponsor?.EmployerName,
            SponsorSupportPhone = sponsor?.SupportPhone ?? sponsor?.ContactPhone,

            IssuerName = carrier?.LetterheadName,
            IssuerAddress = IssuerAddressLine(carrier),

            PlanId = coverage.PlanId,
            PlanName = plan?.PlanName ?? coverage.PlanName,
            NetworkName = plan?.NetworkName,
            CoverageLevel = coverage.CoverageLevel,
            EffectiveDate = coverage.EffectiveDate,
            TerminationDate = coverage.TerminationDate,

            PcpName = coverage.PcpName,
            PcpPhone = coverage.PcpPhone,
            CopaySummary = plan?.CopaySummary,

            CardId = cardId,
            IssuedAt = issuedAt,
            LanguageCode = languageCode
        };
    }

    /// <summary>
    /// The issuer's address on one line, for the back of a card where space is
    /// the constraint. Null where the carrier states no address.
    /// </summary>
    public static string? IssuerAddressLine(CarrierIdentity? carrier)
    {
        var a = carrier?.CorrespondenceAddress;
        if (a is null) return null;

        var cityLine = string.Join(" ", new[] { a.City?.Trim() is { Length: > 0 } c ? c + "," : null, a.State?.Trim(), a.ZipCode?.Trim() }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
        var line = string.Join(" • ", new[] { a.Street?.Trim(), cityLine }.Where(part => !string.IsNullOrWhiteSpace(part)));
        return line.Length == 0 ? null : line;
    }

    private static IdCardIssueResult Fail(string code, string reason) => new()
    {
        Success = false,
        FailureCode = code,
        FailureReason = reason
    };
}
