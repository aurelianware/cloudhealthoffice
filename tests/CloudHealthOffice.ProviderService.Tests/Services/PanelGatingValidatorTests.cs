using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProviderService.Models;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Services;

/// <summary>
/// Tests for the soft-validation contract on
/// <see cref="PanelGatingValidator"/> (capability 5.5). The contract is
/// that producer code calling
/// <see cref="IPanelGatingValidator.Inspect(string, string, Provider)"/>
/// produces exactly one warning per participation that has all five
/// panel-gating fields at their type defaults — and zero warnings for
/// participations that have at least one field populated.
/// </summary>
public class PanelGatingValidatorTests
{
    private const string Tenant = "tenant-a";

    private static (PanelGatingValidator Validator, RecordingLogger<PanelGatingValidator> Logger)
        Build(LogLevel level = LogLevel.Warning)
    {
        var logger = new RecordingLogger<PanelGatingValidator>();
        var opts = new NetworkParticipationBackfillOptions { SoftValidationLogLevel = level };
        var monitor = new TestOptionsMonitor(opts);
        return (new PanelGatingValidator(logger, monitor), logger);
    }

    private static NetworkParticipation Legacy() =>
        new()
        {
            PlanId = "plan-1",
            NetworkId = "net-1",
            LineOfBusiness = LineOfBusiness.Commercial,
            NetworkTier = "Tier1",
            EffectiveDate = DateTime.UtcNow.AddYears(-1),
            AcceptingNewPatients = true,
        };

    private static NetworkParticipation Populated() =>
        new()
        {
            PlanId = "plan-1",
            NetworkId = "net-1",
            LineOfBusiness = LineOfBusiness.Commercial,
            NetworkTier = "Tier1",
            EffectiveDate = DateTime.UtcNow.AddYears(-1),
            AcceptingNewPatients = true,
            PanelLimit = 200,
            PanelAccepted = true,
            AcceptedLobs = new List<LineOfBusiness> { LineOfBusiness.Commercial },
            MinAcceptedAgeYears = 18,
            MaxAcceptedAgeYears = 64,
        };

    private static Provider WithParticipations(params NetworkParticipation[] ps) => new()
    {
        Id = "p1",
        ProviderId = "p1",
        TenantId = Tenant,
        NPI = "1234567890",
        NetworkParticipations = ps.ToList(),
    };

    [Fact]
    public void Inspect_emits_warning_for_participations_at_type_defaults()
    {
        var (validator, logger) = Build();
        validator.Inspect("CreateProvider", Tenant, WithParticipations(Legacy(), Populated()));

        logger.Records.Should().ContainSingle();
        logger.Records[0].Level.Should().Be(LogLevel.Warning);
        logger.Records[0].Message.Should().Contain("PanelGatingFieldsMissing");
    }

    [Fact]
    public void Inspect_emits_no_warning_when_all_participations_populated()
    {
        var (validator, logger) = Build();
        validator.Inspect("CreateProvider", Tenant, WithParticipations(Populated(), Populated()));
        logger.Records.Should().BeEmpty();
    }

    [Fact]
    public void Inspect_emits_one_warning_per_legacy_participation()
    {
        var (validator, logger) = Build();
        validator.Inspect("UpdateProvider", Tenant, WithParticipations(Legacy(), Legacy(), Populated()));
        logger.Records.Should().HaveCount(2);
    }

    [Fact]
    public void Inspect_no_warnings_for_provider_without_participations()
    {
        var (validator, logger) = Build();
        validator.Inspect("CreateProvider", Tenant, new Provider
        {
            Id = "p1",
            ProviderId = "p1",
            TenantId = Tenant,
            NPI = "1234567890",
            NetworkParticipations = new List<NetworkParticipation>(),
        });
        logger.Records.Should().BeEmpty();
    }

    [Fact]
    public void Inspect_single_participation_overload_emits_when_legacy()
    {
        var (validator, logger) = Build();
        var participation = Legacy();
        var provider = WithParticipations(participation);

        validator.Inspect("AddNetworkParticipation", Tenant, provider, participation);
        logger.Records.Should().ContainSingle();
        logger.Records[0].Message.Should().Contain("AddNetworkParticipation");
    }

    [Fact]
    public void Inspect_single_participation_overload_silent_when_populated()
    {
        var (validator, logger) = Build();
        var participation = Populated();
        var provider = WithParticipations(participation);

        validator.Inspect("AddNetworkParticipation", Tenant, provider, participation);
        logger.Records.Should().BeEmpty();
    }

    [Fact]
    public void Inspect_respects_configured_log_level()
    {
        var (validator, logger) = Build(LogLevel.Information);
        validator.Inspect("CreateProvider", Tenant, WithParticipations(Legacy()));

        logger.Records.Should().ContainSingle();
        logger.Records[0].Level.Should().Be(LogLevel.Information);
    }

    private sealed class TestOptionsMonitor : IOptionsMonitor<NetworkParticipationBackfillOptions>
    {
        private readonly NetworkParticipationBackfillOptions _value;
        public TestOptionsMonitor(NetworkParticipationBackfillOptions value) => _value = value;
        public NetworkParticipationBackfillOptions CurrentValue => _value;
        public NetworkParticipationBackfillOptions Get(string? name) => _value;
        public IDisposable? OnChange(Action<NetworkParticipationBackfillOptions, string?> listener) => null;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Records { get; } = new();

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
