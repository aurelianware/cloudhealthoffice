using System.Text.Json;
using EligibilityService.Models;

namespace EligibilityService.Services;

public interface ITemporalEligibilityService
{
    Task<TemporalEligibilityResult> GetAsOfAsync(
        string tenantId,
        string memberId,
        DateTime serviceDate,
        CancellationToken ct = default);
}

/// <summary>
/// Read projection over coverage-service (+ accumulator stub). Returns every
/// coverage that was active on <paramref name="serviceDate"/> together with
/// its COB order, plan version, and accumulator snapshot.
///
/// Note: this is a query-side service and deliberately does not go through
/// IEligibilityAdapter — that path is for real-time 270/271 verification.
/// </summary>
public class TemporalEligibilityService : ITemporalEligibilityService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAccumulatorClient _accumulators;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TemporalEligibilityService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public TemporalEligibilityService(
        IHttpClientFactory httpClientFactory,
        IAccumulatorClient accumulators,
        IConfiguration configuration,
        ILogger<TemporalEligibilityService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _accumulators = accumulators;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<TemporalEligibilityResult> GetAsOfAsync(
        string tenantId,
        string memberId,
        DateTime serviceDate,
        CancellationToken ct = default)
    {
        var result = new TemporalEligibilityResult
        {
            MemberId = memberId,
            ServiceDate = serviceDate.Date
        };

        var coverages = await FetchActiveCoveragesAsync(tenantId, memberId, serviceDate, ct);
        if (coverages.Count == 0)
            return result;

        var ordered = OrderForCob(coverages);

        for (var i = 0; i < ordered.Count; i++)
        {
            var dto = ordered[i];
            var snapshot = await _accumulators.GetSnapshotAsync(
                tenantId, memberId, dto.PlanId ?? string.Empty, serviceDate, ct);

            result.Coverages.Add(new TemporalCoverage
            {
                CoverageId = dto.Id ?? string.Empty,
                GroupNumber = dto.GroupNumber ?? string.Empty,
                PlanId = dto.PlanId ?? string.Empty,
                PlanVersion = dto.PlanVersion,
                CoverageLevel = dto.CoverageLevel,
                InsuranceLineCode = dto.InsuranceLineCode,
                EffectiveDate = dto.EffectiveDate,
                TerminationDate = dto.TerminationDate,
                LineOfBusiness = MapLineOfBusiness(dto.LineOfBusiness),
                CobOrder = i + 1,
                CoverageSequence = SequenceFromOrder(i),
                IsCOBRA = dto.Status == 5 || dto.IsCOBRA,
                IsRetroactive = dto.EffectiveDate.Date < DateTime.UtcNow.Date
                                && dto.EffectiveDate.Date <= serviceDate.Date,
                Accumulators = snapshot
            });
        }

        return result;
    }

    private async Task<List<CoverageDto>> FetchActiveCoveragesAsync(
        string tenantId, string memberId, DateTime serviceDate, CancellationToken ct)
    {
        var baseUrl = _configuration["Services:CoverageService"]
            ?? "http://coverage-service.cloudhealthoffice/api/v1";
        var client = _httpClientFactory.CreateClient("EligibilityDefault");

        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{baseUrl}/coverage/member/{memberId}/active?serviceDate={serviceDate:yyyy-MM-dd}");
        request.Headers.Add("X-Tenant-ID", tenantId);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Coverage-service unreachable for tenant {Tenant} member {Member}",
                SanitizeForLog(tenantId), SanitizeForLog(memberId));
            return new List<CoverageDto>();
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug(
                "Coverage-service returned {Status} for member {Member}",
                response.StatusCode, SanitizeForLog(memberId));
            return new List<CoverageDto>();
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(json))
            return new List<CoverageDto>();

        return JsonSerializer.Deserialize<List<CoverageDto>>(json, JsonOpts)
               ?? new List<CoverageDto>();
    }

    /// <summary>
    /// Orders coverages for Coordination of Benefits.
    /// Primary → Secondary → Tertiary based on <c>OtherInsurance.IsPrimaryPayer</c>
    /// and <c>MedicareCoverage.IsPrimaryPayer</c>. When no COB data is present
    /// the earliest <see cref="CoverageDto.EffectiveDate"/> wins primary.
    /// </summary>
    internal static List<CoverageDto> OrderForCob(List<CoverageDto> coverages)
    {
        return coverages
            .OrderBy(c => CobRank(c))
            .ThenBy(c => c.EffectiveDate)
            .ToList();
    }

    private static int CobRank(CoverageDto c)
    {
        if (c.MedicareCoverage?.IsPrimaryPayer == true) return 0;
        if (c.OtherInsurance?.IsPrimaryPayer == true) return 0;
        if (c.OtherInsurance != null) return 2; // explicit non-primary other insurance
        return 1;
    }

    private static string SequenceFromOrder(int zeroBased) => zeroBased switch
    {
        0 => "P",
        1 => "S",
        2 => "T",
        _ => "O"
    };

    private static LineOfBusiness MapLineOfBusiness(int raw)
    {
        // coverage-service stores 1..N; eligibility LineOfBusiness is 0..N
        var index = Math.Max(0, raw - 1);
        return Enum.IsDefined(typeof(LineOfBusiness), index)
            ? (LineOfBusiness)index
            : LineOfBusiness.Commercial;
    }

    private static string SanitizeForLog(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");

    internal class CoverageDto
    {
        public string? Id { get; set; }
        public string? MemberId { get; set; }
        public string? GroupNumber { get; set; }
        public string? PlanId { get; set; }
        public string? PlanVersion { get; set; }
        public string? CoverageLevel { get; set; }
        public string? InsuranceLineCode { get; set; }
        public DateTime EffectiveDate { get; set; }
        public DateTime? TerminationDate { get; set; }
        public int Status { get; set; }
        public int LineOfBusiness { get; set; } = 1;
        public bool IsCOBRA { get; set; }
        public CoverageMedicareDto? MedicareCoverage { get; set; }
        public CoverageOtherInsuranceDto? OtherInsurance { get; set; }
    }

    internal class CoverageMedicareDto
    {
        public bool IsPrimaryPayer { get; set; }
    }

    internal class CoverageOtherInsuranceDto
    {
        public bool IsPrimaryPayer { get; set; }
        public string? PayerName { get; set; }
    }
}
