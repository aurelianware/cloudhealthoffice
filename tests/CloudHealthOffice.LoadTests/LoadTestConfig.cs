namespace CloudHealthOffice.LoadTests;

/// <summary>
/// Centralized configuration for load test scenarios.
/// Adjust these values based on environment capacity and SLA targets.
/// Environment variable overrides allow CI to tune without code changes.
/// </summary>
public static class LoadTestConfig
{
    // ── Service endpoints ──────────────────────────────────────────────
    public static string ClaimsServiceUrl =>
        Environment.GetEnvironmentVariable("LOAD_TEST_CLAIMS_URL") ?? "http://localhost:5001";

    public static string PaymentServiceUrl =>
        Environment.GetEnvironmentVariable("LOAD_TEST_PAYMENT_URL") ?? "http://localhost:5003";

    public static string BenefitPlanServiceUrl =>
        Environment.GetEnvironmentVariable("LOAD_TEST_BENEFIT_URL") ?? "http://localhost:5002";

    public static string EligibilityServiceUrl =>
        Environment.GetEnvironmentVariable("LOAD_TEST_ELIGIBILITY_URL") ?? "http://localhost:5007";

    public static string AuthorizationServiceUrl =>
        Environment.GetEnvironmentVariable("LOAD_TEST_AUTHORIZATION_URL") ?? "http://localhost:5005";

    public static string ProviderVerificationServiceUrl =>
        Environment.GetEnvironmentVariable("LOAD_TEST_PROVIDER_VERIFICATION_URL") ?? "http://localhost:5010";

    // ── Tenant isolation ───────────────────────────────────────────────
    public static string TenantId => "load-test-tenant";

    // ── Load profiles ──────────────────────────────────────────────────
    // Warm-up: ramp from 0 to target rate over this duration
    public static TimeSpan WarmUpDuration =>
        TimeSpan.FromSeconds(ParseInt("LOAD_TEST_WARMUP_SECS", 10));

    // Sustain: hold at target rate for this duration
    public static TimeSpan SustainDuration =>
        TimeSpan.FromSeconds(ParseInt("LOAD_TEST_SUSTAIN_SECS", 30));

    // Cool-down: ramp back down
    public static TimeSpan CoolDownDuration =>
        TimeSpan.FromSeconds(ParseInt("LOAD_TEST_COOLDOWN_SECS", 5));

    // Requests per second at peak
    public static int TargetRps =>
        ParseInt("LOAD_TEST_TARGET_RPS", 50);

    // ── SLA thresholds ─────────────────────────────────────────────────
    // p99 latency must be under this for the test to pass
    public static TimeSpan MaxP99Latency =>
        TimeSpan.FromMilliseconds(ParseInt("LOAD_TEST_MAX_P99_MS", 2000));

    // Maximum acceptable error rate (0.0 - 1.0)
    public static double MaxErrorRate
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("LOAD_TEST_MAX_ERROR_RATE");
            return double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0.01;
        }
    }

    // ── Report output ──────────────────────────────────────────────────
    public static string ReportFolder =>
        Environment.GetEnvironmentVariable("LOAD_TEST_REPORT_DIR") ?? "./LoadResults/reports";

    private static int ParseInt(string envVar, int defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(envVar);
        return int.TryParse(raw, out var v) ? v : defaultValue;
    }
}
