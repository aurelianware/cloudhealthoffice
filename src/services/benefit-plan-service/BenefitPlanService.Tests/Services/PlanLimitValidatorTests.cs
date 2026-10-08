using BenefitPlanService.Models;
using BenefitPlanService.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BenefitPlanService.Tests.Services;

/// <summary>
/// Capability BP 5.7 — write-time ACA §156.130 OOP cap validation.
/// Both individual and family caps are checked on every write surface;
/// missing plan-year config fails closed.
/// </summary>
public sealed class PlanLimitValidatorTests
{
    private static IPlanLimitValidator BuildValidator(IAcaLimitsProvider? limits = null)
    {
        limits ??= new StubLimits(new AcaLimits(2025, 9_200m, 18_400m));
        return new PlanLimitValidator(
            limits,
            new PlanYearResolver(),
            NullLogger<PlanLimitValidator>.Instance);
    }

    private static BenefitPlan PlanFor2025(decimal individualOop = 0m, decimal familyOop = 0m,
        FamilyAccumulatorModel model = FamilyAccumulatorModel.Embedded) => new()
    {
        TenantId = "tenant-a",
        PlanId = "plan-001",
        VersionId = "v1",
        EffectiveDate = new DateTime(2025, 1, 1),
        FamilyAccumulatorModel = model,
        CostSharing = new CostSharing
        {
            IndividualOutOfPocketMax = individualOop,
            FamilyOutOfPocketMax = familyOop,
        },
    };

    [Fact]
    public void Validate_Passes_When_Caps_Within_Limits()
    {
        var validator = BuildValidator();
        var plan = PlanFor2025(individualOop: 9_000m, familyOop: 18_000m);

        var act = () => validator.Validate(plan, PlanLimitWriteCaller.PublishAndSupersede);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_Throws_When_IndividualOop_Exceeds_AcaCap()
    {
        var validator = BuildValidator();
        var plan = PlanFor2025(individualOop: 10_000m);

        var act = () => validator.Validate(plan, PlanLimitWriteCaller.PublishAndSupersede);

        act.Should().Throw<PlanLimitValidationException>()
            .Which.Field.Should().Be("costSharing.individualOutOfPocketMax");
    }

    [Fact]
    public void Validate_Throws_When_FamilyOop_Exceeds_AcaFamilyCap()
    {
        var validator = BuildValidator();
        var plan = PlanFor2025(familyOop: 19_000m, model: FamilyAccumulatorModel.Aggregate);

        var act = () => validator.Validate(plan, PlanLimitWriteCaller.PublishAndSupersede);

        act.Should().Throw<PlanLimitValidationException>()
            .Which.Field.Should().Be("costSharing.familyOutOfPocketMax");
    }

    [Fact]
    public void Validate_Throws_When_PlanYear_Is_Not_Configured()
    {
        var validator = BuildValidator();
        var plan = PlanFor2025(individualOop: 1_000m);
        plan.EffectiveDate = new DateTime(2030, 1, 1); // unconfigured year

        var act = () => validator.Validate(plan, PlanLimitWriteCaller.PublishAndSupersede);

        act.Should().Throw<PlanLimitValidationException>()
            .Which.Field.Should().Be("planYear");
    }

    [Fact]
    public void Validate_Resolves_Year_From_PlanYearDefinition_First()
    {
        var validator = BuildValidator();
        var plan = PlanFor2025(individualOop: 9_300m); // > 2025 cap, < 2026 cap
        plan.PlanYearDefinition = new PlanYearDefinition
        {
            PlanYearStart = new DateTime(2026, 1, 1),
            PlanYearEnd = new DateTime(2026, 12, 31),
        };

        // Without year resolution, this would pass under 2026 caps
        // (individual=10,600). But the configured stub only carries 2025;
        // verify the validator looks up via PlanYearDefinition's year (2026)
        // and rejects on missing year, not against 2025 caps.
        var act = () => validator.Validate(plan, PlanLimitWriteCaller.PublishAndSupersede);
        act.Should().Throw<PlanLimitValidationException>()
            .Which.PlanYear.Should().Be(2026);
    }

    [Fact]
    public void Validate_Falls_Back_To_EffectiveDate_When_PlanYearDefinition_Missing()
    {
        var validator = BuildValidator();
        var plan = PlanFor2025(individualOop: 9_300m); // > 2025 cap of 9,200
        plan.PlanYearDefinition = null;

        var act = () => validator.Validate(plan, PlanLimitWriteCaller.PublishAndSupersede);

        act.Should().Throw<PlanLimitValidationException>()
            .Where(ex => ex.PlanYear == 2025 && ex.Cap == 9_200m);
    }

    [Fact]
    public void Validate_Throws_With_Structured_Payload_Including_Configured_Years()
    {
        var validator = BuildValidator(new StubLimits(
            new AcaLimits(2024, 9_450m, 18_900m),
            new AcaLimits(2025, 9_200m, 18_400m)));
        var plan = PlanFor2025();
        plan.EffectiveDate = new DateTime(2030, 1, 1);

        var act = () => validator.Validate(plan, PlanLimitWriteCaller.CreatePlan);

        act.Should().Throw<PlanLimitValidationException>()
            .WithMessage("*Configured years: [2024, 2025]*");
    }

    // ── Advisory warnings: Aggregate + $0 family + positive individual ──

    private static (IPlanLimitValidator Validator, RecordingLogger Logger) BuildRecordingValidator()
    {
        var logger = new RecordingLogger();
        return (new PlanLimitValidator(
            new StubLimits(new AcaLimits(2025, 9_200m, 18_400m)),
            new PlanYearResolver(),
            logger), logger);
    }

    private static BenefitPlan AggregatePlan(
        decimal individualDeductible, decimal familyDeductible,
        decimal individualOop, decimal familyOop,
        FamilyAccumulatorModel model = FamilyAccumulatorModel.Aggregate)
    {
        var plan = PlanFor2025(individualOop, familyOop, model);
        plan.CostSharing.IndividualDeductible = individualDeductible;
        plan.CostSharing.FamilyDeductible = familyDeductible;
        return plan;
    }

    private static List<string> Warnings(RecordingLogger logger)
        => logger.Records.Where(r => r.LogLevel == LogLevel.Warning).Select(r => r.Message).ToList();

    [Fact]
    public void Validate_Aggregate_ZeroFamilyDeductible_PositiveIndividual_WarnsAndAccepts()
    {
        var (validator, logger) = BuildRecordingValidator();
        var plan = AggregatePlan(individualDeductible: 1_000m, familyDeductible: 0m,
            individualOop: 5_000m, familyOop: 10_000m);

        var act = () => validator.Validate(plan, PlanLimitWriteCaller.CreatePlan);

        act.Should().NotThrow();
        var warning = Warnings(logger).Should().ContainSingle().Subject;
        warning.Should().Contain("AggregateZeroFamilyDeductible")
            .And.Contain("costSharing.familyDeductible")
            .And.Contain("treated as \"not set\"")
            .And.Contain("applied per member")
            .And.Contain("cannot currently be expressed");
    }

    [Fact]
    public void Validate_Aggregate_ZeroFamilyOop_PositiveIndividual_WarnsAndAccepts()
    {
        var (validator, logger) = BuildRecordingValidator();
        var plan = AggregatePlan(individualDeductible: 1_000m, familyDeductible: 2_000m,
            individualOop: 5_000m, familyOop: 0m);

        var act = () => validator.Validate(plan, PlanLimitWriteCaller.UpdatePlan);

        act.Should().NotThrow();
        Warnings(logger).Should().ContainSingle()
            .Which.Should().Contain("AggregateZeroFamilyOop")
            .And.Contain("costSharing.familyOutOfPocketMax");
    }

    [Fact]
    public void Validate_Aggregate_BothFamilyLimitsZero_WarnsForEach()
    {
        var (validator, logger) = BuildRecordingValidator();
        var plan = AggregatePlan(1_000m, 0m, 5_000m, 0m);

        validator.Validate(plan, PlanLimitWriteCaller.PublishAndSupersede);

        Warnings(logger).Should().HaveCount(2);
    }

    [Theory]
    [InlineData(FamilyAccumulatorModel.Embedded, 1_000, 0, 5_000, 0)]   // Embedded: unambiguous
    [InlineData(FamilyAccumulatorModel.Aggregate, 1_000, 2_000, 5_000, 10_000)] // family set
    [InlineData(FamilyAccumulatorModel.Aggregate, 0, 0, 0, 0)]          // nothing configured
    public void Validate_NoWarning_WhenUnambiguous(
        FamilyAccumulatorModel model, decimal indDed, decimal famDed, decimal indOop, decimal famOop)
    {
        var (validator, logger) = BuildRecordingValidator();
        var plan = AggregatePlan(indDed, famDed, indOop, famOop, model);

        validator.Validate(plan, PlanLimitWriteCaller.CreatePlan);

        Warnings(logger).Should().BeEmpty();
    }

    private sealed class RecordingLogger : ILogger<PlanLimitValidator>
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

    private sealed class StubLimits : IAcaLimitsProvider
    {
        private readonly Dictionary<int, AcaLimits> _byYear;
        public StubLimits(params AcaLimits[] rows)
        {
            _byYear = rows.ToDictionary(r => r.PlanYear);
        }
        public AcaLimits? GetForPlanYear(int planYear)
            => _byYear.TryGetValue(planYear, out var row) ? row : null;
        public IReadOnlyCollection<int> ConfiguredPlanYears => _byYear.Keys;
    }
}
