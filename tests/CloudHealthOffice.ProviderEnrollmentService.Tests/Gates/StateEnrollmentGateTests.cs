using CloudHealthOffice.ProviderEnrollmentService.Abstractions;
using CloudHealthOffice.ProviderEnrollmentService.Aggregator;
using CloudHealthOffice.ProviderEnrollmentService.Cache;
using CloudHealthOffice.ProviderEnrollmentService.Gates;
using CloudHealthOffice.ProviderEnrollmentService.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CloudHealthOffice.ProviderEnrollmentService.Tests.Gates;

public class StateEnrollmentGateTests
{
    private const string Npi = "1234567890";
    private const string StateCode = "TX";
    private const string TenantId = "txmco01";
    private const string Taxonomy = "207Q00000X";

    private readonly DateOnly _today = DateOnly.FromDateTime(DateTime.Today);

    // Mocks stored by BuildGate so individual tests can assert on them
    private IStateEnrollmentSource _source = null!;
    private ITenantEnrollmentConfigRepository _configRepo = null!;
    private ILogger<StateEnrollmentGate> _logger = null!;

    // ── Helpers ──────────────────────────────────────────────────────

    private StateEnrollmentGate BuildGate(
        IHttpContextAccessor? httpContext = null,
        TenantEnrollmentConfig? config = null,
        StateEnrollmentRecord? record = null)
    {
        _source = Substitute.For<IStateEnrollmentSource>();
        _source.StateCode.Returns("TX");
        _source.GetEnrollmentAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(record);

        var aggregator = new MultiStateEnrollmentAggregator(
            new[] { _source },
            Options.Create(new ProviderEnrollmentOptions()),
            Substitute.For<ILogger<MultiStateEnrollmentAggregator>>());

        _configRepo = Substitute.For<ITenantEnrollmentConfigRepository>();
        _configRepo.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(config);

        _logger = Substitute.For<ILogger<StateEnrollmentGate>>();

        return new StateEnrollmentGate(
            aggregator,
            _configRepo,
            httpContext ?? Substitute.For<IHttpContextAccessor>(),
            _logger);
    }

    private static StateEnrollmentRecord BuildActiveRecord() => new()
    {
        Npi = Npi,
        StateCode = StateCode,
        SourceSystem = "PEMS",
        Status = EnrollmentStatus.Active,
        EffectiveDate = DateOnly.FromDateTime(DateTime.Today).AddYears(-1),
        ProviderType = ProviderTypeClassification.PhysicianMD,
        SupportedLobs = LineOfBusiness.Medicaid,
        EnrolledTaxonomies = [Taxonomy]
    };

    private static TenantEnrollmentConfig BuildConfig(
        EnrollmentGateMode mode,
        IReadOnlyList<string>? enabledStates = null,
        IReadOnlyList<LobEnrollmentOverride>? lobOverrides = null) => new()
    {
        TenantId = TenantId,
        DefaultGateMode = mode,
        EnabledStateCodes = enabledStates ?? ["TX"],
        LobOverrides = lobOverrides ?? []
    };

    private static IHttpContextAccessor BuildHttpContext(string? tenantId)
    {
        var accessor = Substitute.For<IHttpContextAccessor>();

        if (tenantId is not null)
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Headers["X-Tenant-Id"] = tenantId;
            accessor.HttpContext.Returns(ctx);
        }

        return accessor;
    }

    // ═════════════════════════════════════════════════════════════════
    //  1-2: No tenant context → Pass
    // ═════════════════════════════════════════════════════════════════

    [Fact]
    public async Task NoHttpContext_ReturnsPass()
    {
        // HttpContext is null — batch/test path
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns((HttpContext?)null);

        var gate = BuildGate(httpContext: accessor);

        var result = await gate.EvaluateAsync(Npi, Taxonomy, StateCode, _today, LineOfBusiness.Medicaid);

        result.Passed.Should().BeTrue();
        await _configRepo.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TenantIdMissingFromItems_ReturnsPass()
    {
        // HttpContext exists but X-Tenant-Id header is absent
        var accessor = BuildHttpContext(null);
        var ctx = new DefaultHttpContext(); // no tenant header
        accessor.HttpContext.Returns(ctx);

        var gate = BuildGate(httpContext: accessor);

        var result = await gate.EvaluateAsync(Npi, Taxonomy, StateCode, _today, LineOfBusiness.Medicaid);

        result.Passed.Should().BeTrue();
        await _configRepo.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ═════════════════════════════════════════════════════════════════
    //  3: No tenant config → Pass
    // ═════════════════════════════════════════════════════════════════

    [Fact]
    public async Task NoTenantConfig_ReturnsPass()
    {
        var gate = BuildGate(
            httpContext: BuildHttpContext(TenantId),
            config: null);

        var result = await gate.EvaluateAsync(Npi, Taxonomy, StateCode, _today, LineOfBusiness.Medicaid);

        result.Passed.Should().BeTrue();
        await _source.DidNotReceive()
            .GetEnrollmentAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    // ═════════════════════════════════════════════════════════════════
    //  4-6: Gate mode / LOB override / state filter → Pass
    // ═════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GateModeDisabled_ReturnsPass()
    {
        var gate = BuildGate(
            httpContext: BuildHttpContext(TenantId),
            config: BuildConfig(EnrollmentGateMode.Disabled));

        var result = await gate.EvaluateAsync(Npi, Taxonomy, StateCode, _today, LineOfBusiness.Medicaid);

        result.Passed.Should().BeTrue();
        await _source.DidNotReceive()
            .GetEnrollmentAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GateModeDisabled_ViaLobOverride_Marketplace_ReturnsPass()
    {
        var config = BuildConfig(EnrollmentGateMode.Enforce, lobOverrides:
        [
            new LobEnrollmentOverride
            {
                Lob = LineOfBusiness.Marketplace,
                GateMode = EnrollmentGateMode.Disabled
            }
        ]);

        var gate = BuildGate(
            httpContext: BuildHttpContext(TenantId),
            config: config);

        var result = await gate.EvaluateAsync(Npi, Taxonomy, StateCode, _today, LineOfBusiness.Marketplace);

        result.Passed.Should().BeTrue();
        await _source.DidNotReceive()
            .GetEnrollmentAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StateNotInEnabledList_ReturnsPass()
    {
        // Only CA is enabled; request is for TX
        var config = BuildConfig(EnrollmentGateMode.Enforce, enabledStates: ["CA"]);

        var gate = BuildGate(
            httpContext: BuildHttpContext(TenantId),
            config: config);

        var result = await gate.EvaluateAsync(Npi, Taxonomy, StateCode, _today, LineOfBusiness.Medicaid);

        result.Passed.Should().BeTrue();
        await _source.DidNotReceive()
            .GetEnrollmentAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    // ═════════════════════════════════════════════════════════════════
    //  7-14: Enforce mode denial codes
    // ═════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Enforce_ProviderNotFound_ReturnsDeny_PEMS001()
    {
        var gate = BuildGate(
            httpContext: BuildHttpContext(TenantId),
            config: BuildConfig(EnrollmentGateMode.Enforce),
            record: null);

        var result = await gate.EvaluateAsync(Npi, Taxonomy, StateCode, _today, LineOfBusiness.Medicaid);

        result.Passed.Should().BeFalse();
        result.DenialCode.Should().Be("PEMS-001");
    }

    [Fact]
    public async Task Enforce_StatusSuspended_ReturnsDeny_PEMS003()
    {
        var gate = BuildGate(
            httpContext: BuildHttpContext(TenantId),
            config: BuildConfig(EnrollmentGateMode.Enforce),
            record: BuildActiveRecord() with { Status = EnrollmentStatus.Suspended });

        var result = await gate.EvaluateAsync(Npi, Taxonomy, StateCode, _today, LineOfBusiness.Medicaid);

        result.Passed.Should().BeFalse();
        result.DenialCode.Should().Be("PEMS-003");
    }

    [Fact]
    public async Task Enforce_StatusRevalidationRequired_ReturnsDeny_PEMS004()
    {
        var gate = BuildGate(
            httpContext: BuildHttpContext(TenantId),
            config: BuildConfig(EnrollmentGateMode.Enforce),
            record: BuildActiveRecord() with { Status = EnrollmentStatus.RevalidationRequired });

        var result = await gate.EvaluateAsync(Npi, Taxonomy, StateCode, _today, LineOfBusiness.Medicaid);

        result.Passed.Should().BeFalse();
        result.DenialCode.Should().Be("PEMS-004");
    }

    [Fact]
    public async Task Enforce_StatusPending_ReturnsDeny_PEMS001()
    {
        var gate = BuildGate(
            httpContext: BuildHttpContext(TenantId),
            config: BuildConfig(EnrollmentGateMode.Enforce),
            record: BuildActiveRecord() with { Status = EnrollmentStatus.Pending });

        var result = await gate.EvaluateAsync(Npi, Taxonomy, StateCode, _today, LineOfBusiness.Medicaid);

        result.Passed.Should().BeFalse();
        result.DenialCode.Should().Be("PEMS-001");
    }

    [Fact]
    public async Task Enforce_EffectiveDateInFuture_ReturnsDeny_PEMS001()
    {
        var gate = BuildGate(
            httpContext: BuildHttpContext(TenantId),
            config: BuildConfig(EnrollmentGateMode.Enforce),
            record: BuildActiveRecord() with { EffectiveDate = _today.AddDays(30) });

        var result = await gate.EvaluateAsync(Npi, Taxonomy, StateCode, _today, LineOfBusiness.Medicaid);

        result.Passed.Should().BeFalse();
        result.DenialCode.Should().Be("PEMS-001");
    }

    [Fact]
    public async Task Enforce_TerminatedBeforeServiceDate_ReturnsDeny_PEMS001()
    {
        var gate = BuildGate(
            httpContext: BuildHttpContext(TenantId),
            config: BuildConfig(EnrollmentGateMode.Enforce),
            record: BuildActiveRecord() with { TerminationDate = _today.AddDays(-1) });

        var result = await gate.EvaluateAsync(Npi, Taxonomy, StateCode, _today, LineOfBusiness.Medicaid);

        result.Passed.Should().BeFalse();
        result.DenialCode.Should().Be("PEMS-001");
    }

    [Fact]
    public async Task Enforce_TaxonomyNotEnrolled_ReturnsDeny_PEMS002()
    {
        var gate = BuildGate(
            httpContext: BuildHttpContext(TenantId),
            config: BuildConfig(EnrollmentGateMode.Enforce),
            record: BuildActiveRecord()); // enrolled: 207Q00000X

        var result = await gate.EvaluateAsync(Npi, "2084P0800X", StateCode, _today, LineOfBusiness.Medicaid);

        result.Passed.Should().BeFalse();
        result.DenialCode.Should().Be("PEMS-002");
    }

    [Fact]
    public async Task Enforce_LobNotSupported_ReturnsDeny_PEMS005()
    {
        var gate = BuildGate(
            httpContext: BuildHttpContext(TenantId),
            config: BuildConfig(EnrollmentGateMode.Enforce),
            record: BuildActiveRecord()); // SupportedLobs = Medicaid only

        var result = await gate.EvaluateAsync(Npi, Taxonomy, StateCode, _today, LineOfBusiness.CHIP);

        result.Passed.Should().BeFalse();
        result.DenialCode.Should().Be("PEMS-005");
    }

    // ═════════════════════════════════════════════════════════════════
    //  15: Enforce happy path
    // ═════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Enforce_AllChecksPass_ReturnsPass()
    {
        var gate = BuildGate(
            httpContext: BuildHttpContext(TenantId),
            config: BuildConfig(EnrollmentGateMode.Enforce),
            record: BuildActiveRecord());

        var result = await gate.EvaluateAsync(Npi, Taxonomy, StateCode, _today, LineOfBusiness.Medicaid);

        result.Passed.Should().BeTrue();
        result.DenialCode.Should().BeNull();
    }

    // ═════════════════════════════════════════════════════════════════
    //  16-17: Warn mode
    // ═════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Warn_ProviderNotFound_ReturnsPass_AndLogsWarning()
    {
        var gate = BuildGate(
            httpContext: BuildHttpContext(TenantId),
            config: BuildConfig(EnrollmentGateMode.Warn),
            record: null);

        var result = await gate.EvaluateAsync(Npi, Taxonomy, StateCode, _today, LineOfBusiness.Medicaid);

        result.Passed.Should().BeTrue();

        _logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("warn-only")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task Warn_AllChecksPass_ReturnsPass()
    {
        var gate = BuildGate(
            httpContext: BuildHttpContext(TenantId),
            config: BuildConfig(EnrollmentGateMode.Warn),
            record: BuildActiveRecord());

        var result = await gate.EvaluateAsync(Npi, Taxonomy, StateCode, _today, LineOfBusiness.Medicaid);

        result.Passed.Should().BeTrue();
    }

    // ═════════════════════════════════════════════════════════════════
    //  18: LOB override inherits Enforce with extra fields
    // ═════════════════════════════════════════════════════════════════

    [Fact]
    public async Task LobOverride_STARPlus_InheritedEnforce_ValidRecord_ReturnsPass()
    {
        // STARPlus override sets RevalidationWarningDays only — GateMode inherits Enforce
        var config = BuildConfig(EnrollmentGateMode.Enforce, lobOverrides:
        [
            new LobEnrollmentOverride
            {
                Lob = LineOfBusiness.STARPlus,
                RevalidationWarningDays = 45
            }
        ]);

        var record = BuildActiveRecord() with
        {
            SupportedLobs = LineOfBusiness.STARPlus | LineOfBusiness.Medicaid
        };

        var gate = BuildGate(
            httpContext: BuildHttpContext(TenantId),
            config: config,
            record: record);

        var result = await gate.EvaluateAsync(Npi, Taxonomy, StateCode, _today, LineOfBusiness.STARPlus);

        result.Passed.Should().BeTrue();
    }
}
