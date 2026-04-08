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
    private const string TenantId = "pchp";
    private const string Taxonomy = "207Q00000X";

    private readonly DateOnly _serviceDate = DateOnly.FromDateTime(DateTime.Today);

    // ── Shared builder ───────────────────────────────────────────────

    /// <summary>
    /// Build a <see cref="StateEnrollmentGate"/> with full control over
    /// HTTP context, tenant config, and the enrollment record returned
    /// by the aggregator's TX source.
    /// </summary>
    private static StateEnrollmentGate BuildGate(
        StateEnrollmentRecord? record,
        TenantEnrollmentConfig? tenantConfig = null,
        string? tenantId = TenantId,
        bool hasHttpContext = true)
    {
        // Aggregator backed by a single TX source
        var source = Substitute.For<IStateEnrollmentSource>();
        source.StateCode.Returns("TX");
        source.GetEnrollmentAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(record);

        var aggregator = new MultiStateEnrollmentAggregator(
            new[] { source },
            Options.Create(new ProviderEnrollmentOptions()),
            Substitute.For<ILogger<MultiStateEnrollmentAggregator>>());

        // Tenant config repo
        var configRepo = Substitute.For<ITenantEnrollmentConfigRepository>();
        if (tenantId is not null)
        {
            configRepo.GetAsync(tenantId, Arg.Any<CancellationToken>())
                .Returns(tenantConfig);
        }

        // HTTP context with X-Tenant-Id header
        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        if (hasHttpContext && tenantId is not null)
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Headers["X-Tenant-Id"] = tenantId;
            httpContextAccessor.HttpContext.Returns(httpContext);
        }

        return new StateEnrollmentGate(
            aggregator,
            configRepo,
            httpContextAccessor,
            Substitute.For<ILogger<StateEnrollmentGate>>());
    }

    /// <summary>
    /// Shortcut: build a gate in Enforce mode with TX enabled.
    /// </summary>
    private static StateEnrollmentGate BuildEnforceGate(StateEnrollmentRecord? record) =>
        BuildGate(record, tenantConfig: MakeConfig(EnrollmentGateMode.Enforce));

    // ── Helpers ──────────────────────────────────────────────────────

    private static StateEnrollmentRecord MakeActiveRecord(
        string? taxonomy = Taxonomy,
        LineOfBusiness lobs = LineOfBusiness.Medicaid) => new()
    {
        Npi = Npi,
        StateCode = StateCode,
        SourceSystem = "PEMS",
        Status = EnrollmentStatus.Active,
        EffectiveDate = new DateOnly(2023, 1, 1),
        ProviderType = ProviderTypeClassification.PhysicianMD,
        SupportedLobs = lobs,
        EnrolledTaxonomies = taxonomy is null ? [] : [taxonomy]
    };

    private static TenantEnrollmentConfig MakeConfig(
        EnrollmentGateMode gateMode = EnrollmentGateMode.Enforce,
        IReadOnlyList<string>? enabledStates = null,
        IReadOnlyList<LobEnrollmentOverride>? lobOverrides = null) => new()
    {
        TenantId = TenantId,
        DefaultGateMode = gateMode,
        EnabledStateCodes = enabledStates ?? [],
        LobOverrides = lobOverrides ?? []
    };

    // ═════════════════════════════════════════════════════════════════
    //  Tests 1-4, 8, 9 — Tenant-config-aware behaviour
    // ═════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EvaluateAsync_NoTenantContext_ReturnsPass()
    {
        // Arrange — no HttpContext (batch/test path)
        var gate = BuildGate(record: null, hasHttpContext: false);

        // Act
        var result = await gate.EvaluateAsync(
            Npi, Taxonomy, StateCode, _serviceDate, LineOfBusiness.Medicaid);

        // Assert
        result.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_NoTenantConfig_ReturnsPass()
    {
        // Arrange — tenant config repo returns null
        var gate = BuildGate(record: null, tenantConfig: null);

        // Act
        var result = await gate.EvaluateAsync(
            Npi, Taxonomy, StateCode, _serviceDate, LineOfBusiness.Medicaid);

        // Assert
        result.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_GateModeDisabled_ReturnsPass()
    {
        // Arrange — gate mode Disabled, provider NOT enrolled
        var gate = BuildGate(
            record: null,
            tenantConfig: MakeConfig(EnrollmentGateMode.Disabled));

        // Act
        var result = await gate.EvaluateAsync(
            Npi, Taxonomy, StateCode, _serviceDate, LineOfBusiness.Medicaid);

        // Assert — pass regardless of enrollment status
        result.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_GateModeWarn_EnrollmentFails_StillReturnsPass_AndLogs()
    {
        // Arrange — Warn mode, aggregator returns null (not enrolled)
        var logger = Substitute.For<ILogger<StateEnrollmentGate>>();

        var source = Substitute.For<IStateEnrollmentSource>();
        source.StateCode.Returns("TX");
        source.GetEnrollmentAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((StateEnrollmentRecord?)null);

        var aggregator = new MultiStateEnrollmentAggregator(
            new[] { source },
            Options.Create(new ProviderEnrollmentOptions()),
            Substitute.For<ILogger<MultiStateEnrollmentAggregator>>());

        var configRepo = Substitute.For<ITenantEnrollmentConfigRepository>();
        configRepo.GetAsync(TenantId, Arg.Any<CancellationToken>())
            .Returns(MakeConfig(EnrollmentGateMode.Warn));

        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Tenant-Id"] = TenantId;
        httpContextAccessor.HttpContext.Returns(httpContext);

        var gate = new StateEnrollmentGate(aggregator, configRepo, httpContextAccessor, logger);

        // Act
        var result = await gate.EvaluateAsync(
            Npi, Taxonomy, StateCode, _serviceDate, LineOfBusiness.Medicaid);

        // Assert — passes despite enrollment failure
        result.Passed.Should().BeTrue();

        // Assert — logger received a warning containing "warn-only"
        logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("warn-only")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task EvaluateAsync_GateModeEnforce_LobOverride_Marketplace_Disabled_ReturnsPass()
    {
        // Arrange — Enforce by default, but Marketplace overridden to Disabled
        var config = MakeConfig(
            gateMode: EnrollmentGateMode.Enforce,
            lobOverrides:
            [
                new LobEnrollmentOverride
                {
                    Lob = LineOfBusiness.Marketplace,
                    GateMode = EnrollmentGateMode.Disabled
                }
            ]);

        var gate = BuildGate(record: null, tenantConfig: config);

        // Act — Marketplace LOB, provider not enrolled
        var result = await gate.EvaluateAsync(
            Npi, Taxonomy, StateCode, _serviceDate, LineOfBusiness.Marketplace);

        // Assert — pass because Marketplace gate is Disabled
        result.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_StateNotInEnabledList_ReturnsPass()
    {
        // Arrange — only CA is enabled, request is for TX
        var config = MakeConfig(
            gateMode: EnrollmentGateMode.Enforce,
            enabledStates: ["CA"]);

        var gate = BuildGate(record: null, tenantConfig: config);

        // Act
        var result = await gate.EvaluateAsync(
            Npi, Taxonomy, StateCode, _serviceDate, LineOfBusiness.Medicaid);

        // Assert — TX not in enabled list, gate skipped
        result.Passed.Should().BeTrue();
    }

    // ═════════════════════════════════════════════════════════════════
    //  Tests 5, 6, 7 — Pure enrollment-status checks (Enforce mode)
    // ═════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EvaluateAsync_GateModeEnforce_ProviderNotFound_ReturnsDeny_PEMS001()
    {
        // Arrange — aggregator returns null (NPI unknown to TX PEMS)
        var gate = BuildEnforceGate(record: null);

        // Act
        var result = await gate.EvaluateAsync(
            Npi, Taxonomy, StateCode, _serviceDate, LineOfBusiness.Medicaid);

        // Assert
        result.Passed.Should().BeFalse();
        result.DenialCode.Should().Be("PEMS-001");
        result.DenialReason.Should().Contain(Npi);
    }

    [Fact]
    public async Task EvaluateAsync_GateModeEnforce_StatusSuspended_ReturnsDeny_PEMS003()
    {
        // Arrange — provider is enrolled but suspended
        var record = MakeActiveRecord() with { Status = EnrollmentStatus.Suspended };
        var gate = BuildEnforceGate(record);

        // Act
        var result = await gate.EvaluateAsync(
            Npi, Taxonomy, StateCode, _serviceDate, LineOfBusiness.Medicaid);

        // Assert
        result.Passed.Should().BeFalse();
        result.DenialCode.Should().Be("PEMS-003");
        result.DenialReason.Should().Contain("suspend");
    }

    [Fact]
    public async Task EvaluateAsync_GateModeEnforce_TaxonomyNotEnrolled_ReturnsDeny_PEMS002()
    {
        // Arrange — provider active but enrolled taxonomy differs from request
        var record = MakeActiveRecord(taxonomy: "207Q00000X");
        var gate = BuildEnforceGate(record);

        // Act — request taxonomy 2084P0800X is NOT in the enrolled list
        var result = await gate.EvaluateAsync(
            Npi, "2084P0800X", StateCode, _serviceDate, LineOfBusiness.Medicaid);

        // Assert
        result.Passed.Should().BeFalse();
        result.DenialCode.Should().Be("PEMS-002");
        result.DenialReason.Should().Contain("2084P0800X");
        result.DenialReason.Should().Contain("207Q00000X");
    }

    // ═════════════════════════════════════════════════════════════════
    //  Additional gate-logic coverage
    // ═════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EvaluateAsync_ActiveProvider_MatchingTaxonomy_ReturnsPass()
    {
        var record = MakeActiveRecord();
        var gate = BuildEnforceGate(record);

        var result = await gate.EvaluateAsync(
            Npi, Taxonomy, StateCode, _serviceDate, LineOfBusiness.Medicaid);

        result.Passed.Should().BeTrue();
        result.DenialCode.Should().BeNull();
    }

    [Fact]
    public async Task EvaluateAsync_RevalidationRequired_ReturnsDeny_PEMS004()
    {
        var record = MakeActiveRecord() with { Status = EnrollmentStatus.RevalidationRequired };
        var gate = BuildEnforceGate(record);

        var result = await gate.EvaluateAsync(
            Npi, Taxonomy, StateCode, _serviceDate, LineOfBusiness.Medicaid);

        result.Passed.Should().BeFalse();
        result.DenialCode.Should().Be("PEMS-004");
    }

    [Fact]
    public async Task EvaluateAsync_LobNotSupported_ReturnsDeny_PEMS005()
    {
        var record = MakeActiveRecord(lobs: LineOfBusiness.Medicaid);
        var gate = BuildEnforceGate(record);

        var result = await gate.EvaluateAsync(
            Npi, Taxonomy, StateCode, _serviceDate, LineOfBusiness.CHIP);

        result.Passed.Should().BeFalse();
        result.DenialCode.Should().Be("PEMS-005");
    }

    [Fact]
    public async Task EvaluateAsync_EffectiveDateAfterServiceDate_ReturnsDeny_PEMS001()
    {
        var record = MakeActiveRecord() with
        {
            EffectiveDate = _serviceDate.AddDays(30)
        };
        var gate = BuildEnforceGate(record);

        var result = await gate.EvaluateAsync(
            Npi, Taxonomy, StateCode, _serviceDate, LineOfBusiness.Medicaid);

        result.Passed.Should().BeFalse();
        result.DenialCode.Should().Be("PEMS-001");
        result.DenialReason.Should().Contain("not effective");
    }

    [Fact]
    public async Task EvaluateAsync_TerminatedBeforeServiceDate_ReturnsDeny_PEMS001()
    {
        var record = MakeActiveRecord() with
        {
            TerminationDate = _serviceDate.AddDays(-1)
        };
        var gate = BuildEnforceGate(record);

        var result = await gate.EvaluateAsync(
            Npi, Taxonomy, StateCode, _serviceDate, LineOfBusiness.Medicaid);

        result.Passed.Should().BeFalse();
        result.DenialCode.Should().Be("PEMS-001");
        result.DenialReason.Should().Contain("terminated");
    }
}
