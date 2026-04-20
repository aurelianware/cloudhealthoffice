using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace IdCardService.Services;

/// <summary>
/// Strips CR/LF/NUL and other control characters from values that flow
/// into log messages. All caller-supplied identifiers are routed through
/// this helper before being logged so an attacker can't inject forged log
/// entries via newline-bearing request fields.
/// </summary>
internal static class LogSafe
{
    public static string Of(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        const int maxLen = 256;
        var sb = new System.Text.StringBuilder(Math.Min(value.Length, maxLen));
        var limit = Math.Min(value.Length, maxLen);
        for (var i = 0; i < limit; i++)
        {
            var c = value[i];
            if (!char.IsControl(c)) sb.Append(c);
        }
        return sb.ToString();
    }
}

public class MemberDto
{
    public string MemberId { get; set; } = string.Empty;
    public string? MemberNumber { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public DateTime? DateOfBirth { get; set; }
    public string? Gender { get; set; }
    public string? PreferredLanguage { get; set; }
}

public class CoverageDto
{
    public string Id { get; set; } = string.Empty;
    public string? MemberId { get; set; }
    public string? GroupNumber { get; set; }
    public string? PlanId { get; set; }
    public string? PlanName { get; set; }
    public string? CoverageLevel { get; set; }
    public DateTime? EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
    public int Status { get; set; }
    public string? PcpName { get; set; }
    public string? PcpPhone { get; set; }
    public bool IsActive => Status is 1 or 5;
}

public class SponsorDto
{
    public string? GroupNumber { get; set; }
    public string? EmployerName { get; set; }
    public string? ContactPhone { get; set; }
    public string? SupportPhone { get; set; }
}

public class BenefitPlanDto
{
    public string? Id { get; set; }
    public string? PlanId { get; set; }
    public string? PlanName { get; set; }
    public string? NetworkName { get; set; }
    public string? CopaySummary { get; set; }
}

public interface IMemberClient
{
    Task<MemberDto?> GetAsync(string tenantId, string memberId, CancellationToken ct = default);
}

public interface ICoverageClient
{
    Task<CoverageDto?> GetActiveAsync(string tenantId, string memberId, CancellationToken ct = default);
}

public interface ISponsorClient
{
    Task<SponsorDto?> GetAsync(string tenantId, string groupNumber, CancellationToken ct = default);
}

public interface IBenefitPlanClient
{
    Task<BenefitPlanDto?> GetAsync(string tenantId, string planId, CancellationToken ct = default);
}

public interface IMemberDocumentClient
{
    Task<string> UploadPdfAsync(string tenantId, string memberId, byte[] pdf,
        string fileName, string category, string? subcategory, string uploadedBy, CancellationToken ct = default);
    Task<string> UploadPngAsync(string tenantId, string memberId, byte[] png,
        string fileName, string category, string? subcategory, string uploadedBy, CancellationToken ct = default);
}

public interface IEligibilityClient
{
    Task<object?> GetSnapshotAsync(string tenantId, string memberId, string? providerNpi, CancellationToken ct = default);
}

public class MemberClient : IMemberClient
{
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _cfg;
    private readonly ILogger<MemberClient> _logger;

    public MemberClient(IHttpClientFactory http, IConfiguration cfg, ILogger<MemberClient> logger)
    {
        _http = http; _cfg = cfg; _logger = logger;
    }

    public async Task<MemberDto?> GetAsync(string tenantId, string memberId, CancellationToken ct = default)
    {
        var baseUrl = _cfg["Services:MemberService"] ?? "http://member-service.cloudhealthoffice/api/v1";
        var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/members/{Uri.EscapeDataString(memberId)}");
        req.Headers.Add("X-Tenant-ID", tenantId);
        using var resp = await _http.CreateClient("IdCardDefault").SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("member-service responded {Status} for member {MemberId}", (int)resp.StatusCode, LogSafe.Of(memberId));
            resp.EnsureSuccessStatusCode();
        }
        return await resp.Content.ReadFromJsonAsync<MemberDto>(cancellationToken: ct);
    }
}

public class CoverageClient : ICoverageClient
{
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _cfg;
    private readonly ILogger<CoverageClient> _logger;

    public CoverageClient(IHttpClientFactory http, IConfiguration cfg, ILogger<CoverageClient> logger)
    {
        _http = http; _cfg = cfg; _logger = logger;
    }

    public async Task<CoverageDto?> GetActiveAsync(string tenantId, string memberId, CancellationToken ct = default)
    {
        var baseUrl = _cfg["Services:CoverageService"] ?? "http://coverage-service.cloudhealthoffice/api/v1";
        var req = new HttpRequestMessage(HttpMethod.Get,
            $"{baseUrl}/coverage/member/{Uri.EscapeDataString(memberId)}/active");
        req.Headers.Add("X-Tenant-ID", tenantId);
        using var resp = await _http.CreateClient("IdCardDefault").SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogDebug("coverage-service returned {Status} for member {MemberId}", (int)resp.StatusCode, LogSafe.Of(memberId));
            return null;
        }
        var list = await resp.Content.ReadFromJsonAsync<List<CoverageDto>>(cancellationToken: ct);
        return list?.FirstOrDefault(c => c.IsActive);
    }
}

public class SponsorClient : ISponsorClient
{
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _cfg;
    private readonly ILogger<SponsorClient> _logger;

    public SponsorClient(IHttpClientFactory http, IConfiguration cfg, ILogger<SponsorClient> logger)
    {
        _http = http; _cfg = cfg; _logger = logger;
    }

    public async Task<SponsorDto?> GetAsync(string tenantId, string groupNumber, CancellationToken ct = default)
    {
        var baseUrl = _cfg["Services:SponsorService"] ?? "http://sponsor-service.cloudhealthoffice/api/v1";
        var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/sponsors/{Uri.EscapeDataString(groupNumber)}");
        req.Headers.Add("X-Tenant-ID", tenantId);
        using var resp = await _http.CreateClient("IdCardDefault").SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogDebug("sponsor-service returned {Status} for group {Group}", (int)resp.StatusCode, LogSafe.Of(groupNumber));
            return null;
        }
        return await resp.Content.ReadFromJsonAsync<SponsorDto>(cancellationToken: ct);
    }
}

public class BenefitPlanClient : IBenefitPlanClient
{
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _cfg;
    private readonly ILogger<BenefitPlanClient> _logger;

    public BenefitPlanClient(IHttpClientFactory http, IConfiguration cfg, ILogger<BenefitPlanClient> logger)
    {
        _http = http; _cfg = cfg; _logger = logger;
    }

    public async Task<BenefitPlanDto?> GetAsync(string tenantId, string planId, CancellationToken ct = default)
    {
        var baseUrl = _cfg["Services:BenefitPlanService"] ?? "http://benefit-plan-service.cloudhealthoffice/api/v1";
        var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/plans/{Uri.EscapeDataString(planId)}");
        req.Headers.Add("X-Tenant-ID", tenantId);
        using var resp = await _http.CreateClient("IdCardDefault").SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogDebug("benefit-plan-service returned {Status} for plan {Plan}", (int)resp.StatusCode, LogSafe.Of(planId));
            return null;
        }
        return await resp.Content.ReadFromJsonAsync<BenefitPlanDto>(cancellationToken: ct);
    }
}

public class MemberDocumentClient : IMemberDocumentClient
{
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _cfg;
    private readonly ILogger<MemberDocumentClient> _logger;

    public MemberDocumentClient(IHttpClientFactory http, IConfiguration cfg, ILogger<MemberDocumentClient> logger)
    {
        _http = http; _cfg = cfg; _logger = logger;
    }

    public Task<string> UploadPdfAsync(string tenantId, string memberId, byte[] pdf,
        string fileName, string category, string? subcategory, string uploadedBy, CancellationToken ct = default) =>
        UploadAsync(tenantId, memberId, pdf, fileName, "application/pdf", category, subcategory, uploadedBy, ct);

    public Task<string> UploadPngAsync(string tenantId, string memberId, byte[] png,
        string fileName, string category, string? subcategory, string uploadedBy, CancellationToken ct = default) =>
        UploadAsync(tenantId, memberId, png, fileName, "image/png", category, subcategory, uploadedBy, ct);

    private async Task<string> UploadAsync(string tenantId, string memberId, byte[] body,
        string fileName, string contentType, string category, string? subcategory, string uploadedBy, CancellationToken ct)
    {
        var baseUrl = _cfg["Services:MemberDocumentService"] ?? "http://member-document-service.cloudhealthoffice/api/v1";
        var url = $"{baseUrl}/member-documents";

        // Strip control characters from caller-supplied strings before they go
        // into multipart form fields. These values are parsed server-side as
        // form data, not HTML — but a stray CR/LF would corrupt the multipart
        // encoding and CodeQL flags them as tainted text sinks without this
        // explicit cleansing step.
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(SanitizeFormValue(memberId)), "MemberId");
        form.Add(new StringContent(SanitizeFormValue(category)), "Category");
        if (!string.IsNullOrEmpty(subcategory))
            form.Add(new StringContent(SanitizeFormValue(subcategory)), "Subcategory");
        form.Add(new StringContent("Generated"), "Source");
        form.Add(new StringContent(SanitizeFormValue(uploadedBy)), "UploadedBy");

        var fileContent = new ByteArrayContent(body);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(fileContent, "File", SanitizeFormValue(fileName));

        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        req.Headers.Add("X-Tenant-ID", SanitizeFormValue(tenantId));

        using var resp = await _http.CreateClient("IdCardDefault").SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var doc = await resp.Content.ReadFromJsonAsync<MemberDocumentResponse>(cancellationToken: ct);
        if (doc == null || string.IsNullOrEmpty(doc.Id))
        {
            throw new InvalidOperationException("member-document-service did not return a document id");
        }
        return doc.Id;
    }

    private class MemberDocumentResponse
    {
        public string Id { get; set; } = string.Empty;
    }

    /// <summary>
    /// Strips CR/LF/NUL and other control characters and caps length so
    /// caller-supplied strings can't corrupt the multipart envelope or the
    /// HTTP headers they're also used in. Treats null as empty.
    /// </summary>
    private static string SanitizeFormValue(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        const int maxLen = 512;
        var sb = new System.Text.StringBuilder(Math.Min(value.Length, maxLen));
        var limit = Math.Min(value.Length, maxLen);
        for (var i = 0; i < limit; i++)
        {
            var c = value[i];
            if (!char.IsControl(c)) sb.Append(c);
        }
        return sb.ToString();
    }
}

public class EligibilityClient : IEligibilityClient
{
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _cfg;
    private readonly ILogger<EligibilityClient> _logger;

    public EligibilityClient(IHttpClientFactory http, IConfiguration cfg, ILogger<EligibilityClient> logger)
    {
        _http = http; _cfg = cfg; _logger = logger;
    }

    public async Task<object?> GetSnapshotAsync(string tenantId, string memberId, string? providerNpi, CancellationToken ct = default)
    {
        var baseUrl = _cfg["Services:EligibilityService"] ?? "http://eligibility-service.cloudhealthoffice/api";
        var url = $"{baseUrl}/eligibility/inquiry";

        var body = new
        {
            subscriberId = memberId,
            serviceDate = DateTime.UtcNow,
            serviceTypeCode = "30",
            providerNPI = providerNpi ?? string.Empty
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body)
        };
        req.Headers.Add("X-Tenant-ID", tenantId);

        using var resp = await _http.CreateClient("IdCardDefault").SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Eligibility snapshot fetch failed with status {Status}", (int)resp.StatusCode);
            return null;
        }
        return await resp.Content.ReadFromJsonAsync<object>(cancellationToken: ct);
    }
}
