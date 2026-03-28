using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Infrastructure.Licensing;

/// <summary>
/// Lightweight production license check for Cloud Health Office.
///
/// Behavior:
///   - Development/Test environments: no license required, no warnings
///   - Production/Staging without a key: service starts normally but logs a
///     warning on startup and periodically during requests
///   - Production/Staging with a configured key: normal operation, no warnings
///
/// This is advisory enforcement only — services always start and function
/// regardless of license status. No feature gating, no phone-home, no time-bomb.
///
/// Configuration:
///   Environment variable: CHO_LICENSE_KEY
///   appsettings.json:     "CloudHealthOffice": { "LicenseKey": "..." }
/// </summary>
public class LicenseCheckMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<LicenseCheckMiddleware> _logger;
    private readonly bool _isProduction;
    private readonly bool _hasLicenseKey;
    private readonly string _environment;
    private long _lastWarningTicks = 0;
    private static readonly long WarningIntervalTicks = TimeSpan.FromHours(1).Ticks;

    public LicenseCheckMiddleware(
        RequestDelegate next,
        ILogger<LicenseCheckMiddleware> logger,
        IHostEnvironment hostEnvironment,
        IConfiguration configuration)
    {
        _next = next;
        _logger = logger;
        _environment = hostEnvironment.EnvironmentName;
        _isProduction = hostEnvironment.IsProduction()
                     || _environment.Equals("Staging", StringComparison.OrdinalIgnoreCase);

        // Check for license key from env var or config
        var licenseKey = Environment.GetEnvironmentVariable("CHO_LICENSE_KEY")
                      ?? configuration["CloudHealthOffice:LicenseKey"];

        _hasLicenseKey = !string.IsNullOrWhiteSpace(licenseKey);

        if (_isProduction && !_hasLicenseKey)
        {
            _logger.LogWarning(
                "Cloud Health Office is running in {Environment} without a production license. " +
                "A commercial license is required for production use. " +
                "Set CHO_LICENSE_KEY or see https://cloudhealthoffice.com/docs/commercial-licensing",
                _environment);
        }
        else if (_isProduction && _hasLicenseKey)
        {
            _logger.LogInformation("Cloud Health Office production license key present");
        }
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip license check for health/swagger/static paths
        if (IsExemptPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        // In production without a key, log periodic warning (not per-request, thread-safe)
        if (_isProduction && !_hasLicenseKey)
        {
            var now = DateTime.UtcNow.Ticks;
            var last = Interlocked.Read(ref _lastWarningTicks);
            if (now - last <= WarningIntervalTicks)
            {
                // Skip — warned recently
            }
            else if (Interlocked.CompareExchange(ref _lastWarningTicks, now, last) != last)
            {
                // Another thread won the race — skip
            }
            else
            {
                _logger.LogWarning(
                    "LICENSING: Cloud Health Office requires a commercial license for production use. " +
                    "Visit https://cloudhealthoffice.com/docs/commercial-licensing or " +
                    "contact licensing@cloudhealthoffice.com");
            }
        }

        // Add license status header (informational, helps admins diagnose)
        if (_isProduction && !_hasLicenseKey)
        {
            context.Response.Headers["X-CHO-License"] = "unlicensed";
        }

        await _next(context);
    }

    private static bool IsExemptPath(PathString path)
    {
        return path.StartsWithSegments("/health")
            || path.StartsWithSegments("/swagger")
            || (path.Value?.StartsWith("/favicon", StringComparison.OrdinalIgnoreCase) == true);
    }
}

/// <summary>
/// Extension methods for registering the license check middleware.
/// </summary>
public static class LicenseCheckExtensions
{
    /// <summary>
    /// Adds the Cloud Health Office production license check middleware.
    /// In Development/Test: no-op. In Production/Staging without CHO_LICENSE_KEY: logs warnings.
    /// Services always start and function regardless of license status.
    /// </summary>
    public static IApplicationBuilder UseChoLicenseCheck(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<LicenseCheckMiddleware>();
    }
}
