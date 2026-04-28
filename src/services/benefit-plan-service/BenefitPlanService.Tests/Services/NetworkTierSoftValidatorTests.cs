using BenefitPlanService.Models;
using BenefitPlanService.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BenefitPlanService.Tests.Services;

/// <summary>
/// Capability 5.5 — soft-validation telemetry on
/// <c>BenefitPlan.NetworkTiers</c> writes. Verifies the warning emits
/// once per offending tier with the expected caller dimension and
/// passes through cleanly when every tier carries a NetworkId.
/// </summary>
public sealed class NetworkTierSoftValidatorTests
{
    [Fact]
    public void Inspect_Emits_One_Warning_Per_Tier_Without_NetworkId()
    {
        var logger = new RecordingLogger();
        var validator = BuildValidator(logger);
        var plan = new BenefitPlan
        {
            TenantId = "tenant-a",
            PlanId = "plan-001",
            VersionId = "v1",
            NetworkTiers = new()
            {
                new NetworkTier { TierName = "In-Network",     TierLevel = 1, NetworkId = null },
                new NetworkTier { TierName = "Out-of-Network", TierLevel = 2, NetworkId = "net-2" },
                new NetworkTier { TierName = "OON-Tier-3",     TierLevel = 3, NetworkId = "" },
            },
        };

        validator.Inspect(plan, NetworkTierWriteCaller.UpdatePlan);

        // Two offending tiers (null + empty), one populated tier — two warnings.
        logger.Records.Should().HaveCount(2);
        logger.Records.Should().AllSatisfy(r => r.LogLevel.Should().Be(LogLevel.Warning));
        logger.Records.All(r => r.Message.Contains("UpdatePlan")).Should().BeTrue();
    }

    [Fact]
    public void Inspect_Is_A_NoOp_When_Plan_Has_No_Tiers()
    {
        var logger = new RecordingLogger();
        var validator = BuildValidator(logger);
        var plan = new BenefitPlan { TenantId = "tenant-a", PlanId = "plan-001" };

        validator.Inspect(plan, NetworkTierWriteCaller.CreatePlan);

        logger.Records.Should().BeEmpty();
    }

    [Fact]
    public void Inspect_Honors_The_Configured_LogLevel()
    {
        var logger = new RecordingLogger();
        var options = new NetworkTierBackfillOptions { SoftValidationLogLevel = LogLevel.Information };
        var validator = new NetworkTierSoftValidator(
            new SingleValueOptionsMonitor<NetworkTierBackfillOptions>(options),
            logger);
        var plan = new BenefitPlan
        {
            TenantId = "tenant-a",
            PlanId = "plan-001",
            NetworkTiers = new() { new NetworkTier { TierName = "In-Network", NetworkId = null } },
        };

        validator.Inspect(plan, NetworkTierWriteCaller.PublishAndSupersede);

        logger.Records.Single().LogLevel.Should().Be(LogLevel.Information);
    }

    private static NetworkTierSoftValidator BuildValidator(ILogger<NetworkTierSoftValidator> logger)
    {
        var options = new NetworkTierBackfillOptions();
        return new NetworkTierSoftValidator(
            new SingleValueOptionsMonitor<NetworkTierBackfillOptions>(options),
            logger);
    }

    private sealed class SingleValueOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public SingleValueOptionsMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class RecordingLogger : ILogger<NetworkTierSoftValidator>
    {
        public List<(LogLevel LogLevel, string Message)> Records { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Records.Add((logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
