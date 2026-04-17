namespace MemberService.Services;

/// <summary>
/// Bound from configuration section <c>Downstream</c>. All base URLs are optional; when
/// unset, the corresponding endpoints return 503 Service Unavailable with a RFC 7807
/// problem-detail.
/// </summary>
public class DownstreamOptions
{
    public DownstreamService? CoverageService { get; set; }
    public DownstreamService? EnrollmentImportService { get; set; }
    public DownstreamService? AccumulatorService { get; set; }
}

public class DownstreamService
{
    public string? BaseUrl { get; set; }
}
