using IdCardService.Models;
using IdCardService.Services;

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
        ILogger<ChoIdCardAdapter> logger)
    {
        _memberClient = memberClient;
        _coverageClient = coverageClient;
        _sponsorClient = sponsorClient;
        _benefitPlanClient = benefitPlanClient;
        _templateResolver = templateResolver;
        _qr = qr;
        _generator = generator;
        _documents = documents;
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

        var member = memberTask.Result;
        var coverage = coverageTask.Result;

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

        await Task.WhenAll(sponsorTask, planTask);

        var sponsor = sponsorTask.Result;
        var plan = planTask.Result;

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

        var bindings = BuildBindings(member, coverage, sponsor, plan, cardId, issuedAt, languageCode);

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
            cardId, Sanitize(memberId), template.Id, pdfDocId);

        return new IdCardIssueResult { Success = true, Record = record };
    }

    private static CardBindings BuildBindings(
        MemberDto member, CoverageDto coverage, SponsorDto? sponsor, BenefitPlanDto? plan,
        string cardId, DateTime issuedAt, string languageCode)
    {
        return new CardBindings
        {
            MemberId = member.MemberId,
            MemberNumber = member.MemberNumber ?? member.MemberId,
            MemberName = string.Join(' ', new[] { member.FirstName, member.LastName }.Where(s => !string.IsNullOrEmpty(s))),
            DateOfBirth = member.DateOfBirth,
            Gender = member.Gender,

            GroupNumber = coverage.GroupNumber,
            SponsorName = sponsor?.EmployerName,
            SponsorSupportPhone = sponsor?.SupportPhone ?? sponsor?.ContactPhone,

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

    private static IdCardIssueResult Fail(string code, string reason) => new()
    {
        Success = false,
        FailureCode = code,
        FailureReason = reason
    };

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");
}
