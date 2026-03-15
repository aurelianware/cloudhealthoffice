using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CloudHealthOffice.Infrastructure.HealthChecks;

public class HttpDependencyHealthCheck : IHealthCheck
{
    private readonly string _url;
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    public HttpDependencyHealthCheck(string url)
    {
        _url = url;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await SharedClient.GetAsync(_url, cancellationToken);
            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy($"{_url} is reachable")
                : HealthCheckResult.Degraded($"{_url} returned {response.StatusCode}");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Degraded($"{_url} is unreachable", ex);
        }
    }
}
