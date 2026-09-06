namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// Opt-in switches and bounded timeouts for the interop harness.
///
/// External interop tests are opt-in locally and explicitly enabled in CI: no
/// ordinary unit-test run may download or start third-party code. Nothing here
/// weakens CHO's own trust model — these values decide whether the harness runs
/// and how long it waits, never how CHO authenticates a caller.
/// </summary>
public static class InteropSettings
{
    /// <summary>Set CHO_INTEROP_ENABLED=1 to allow scenarios that start external containers.</summary>
    public const string EnabledVariable = "CHO_INTEROP_ENABLED";

    /// <summary>
    /// Set CHO_INTEROP_KEEP_STACK=1 to leave containers running after a run, for
    /// interactive debugging. Off by default: the harness always cleans up.
    /// </summary>
    public const string KeepStackVariable = "CHO_INTEROP_KEEP_STACK";

    /// <summary>Set CHO_INTEROP_ENVIRONMENT to label evidence (e.g. "CI").</summary>
    public const string EnvironmentVariable = "CHO_INTEROP_ENVIRONMENT";

    public static bool IsEnabled => IsTruthy(Environment.GetEnvironmentVariable(EnabledVariable));

    public static bool KeepStack => IsTruthy(Environment.GetEnvironmentVariable(KeepStackVariable));

    public static string EnvironmentLabel =>
        Environment.GetEnvironmentVariable(EnvironmentVariable)
        ?? (Environment.GetEnvironmentVariable("CI") is not null ? "CI" : "local");

    /// <summary>The CHO commit under test, recorded in evidence.</summary>
    public static string? ChoCommit =>
        Environment.GetEnvironmentVariable("GITHUB_SHA")
        ?? Environment.GetEnvironmentVariable("CHO_COMMIT_SHA");

    /// <summary>
    /// Bounded timeouts. A dead external implementation must produce a diagnostic
    /// within a known time, never hang CI.
    /// </summary>
    public static class Timeouts
    {
        /// <summary>Pulling the pinned images (cold cache, several hundred MB).</summary>
        public static TimeSpan ImagePull => FromEnv("CHO_INTEROP_PULL_TIMEOUT_SECONDS", TimeSpan.FromMinutes(15));

        /// <summary>`docker compose up --detach` returning.</summary>
        public static TimeSpan ContainerStart => FromEnv("CHO_INTEROP_START_TIMEOUT_SECONDS", TimeSpan.FromMinutes(5));

        /// <summary>Waiting for an external implementation to become ready.</summary>
        public static TimeSpan Readiness => FromEnv("CHO_INTEROP_READY_TIMEOUT_SECONDS", TimeSpan.FromMinutes(8));

        /// <summary>A single protocol call.</summary>
        public static TimeSpan ProtocolCall => FromEnv("CHO_INTEROP_CALL_TIMEOUT_SECONDS", TimeSpan.FromSeconds(90));

        /// <summary>One scenario end to end, excluding stack startup.</summary>
        public static TimeSpan Scenario => FromEnv("CHO_INTEROP_SCENARIO_TIMEOUT_SECONDS", TimeSpan.FromMinutes(5));

        /// <summary>Tearing the stack down.</summary>
        public static TimeSpan Teardown => FromEnv("CHO_INTEROP_TEARDOWN_TIMEOUT_SECONDS", TimeSpan.FromMinutes(3));

        /// <summary>Interval between readiness polls.</summary>
        public static TimeSpan ReadinessPoll => FromEnv("CHO_INTEROP_POLL_SECONDS", TimeSpan.FromSeconds(5));

        private static TimeSpan FromEnv(string variable, TimeSpan fallback) =>
            int.TryParse(Environment.GetEnvironmentVariable(variable), out var seconds) && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : fallback;
    }

    private static bool IsTruthy(string? value) =>
        value is not null
        && (value.Equals("1", StringComparison.Ordinal)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase));
}
