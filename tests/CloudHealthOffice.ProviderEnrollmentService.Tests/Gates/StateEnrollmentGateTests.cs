using CloudHealthOffice.ProviderEnrollmentService.Abstractions;
using CloudHealthOffice.ProviderEnrollmentService.Aggregator;
using CloudHealthOffice.ProviderEnrollmentService.Gates;
using CloudHealthOffice.ProviderEnrollmentService.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CloudHealthOffice.ProviderEnrollmentService.Tests.Gates;

public class StateEnrollmentGateTests
{
    private const string Npi = "1234567890";
    private const string StateCode = "TX";
    private const string Taxonomy = "207Q00000X";

    private readonly DateOnly _serviceDate = DateOnly.FromDateTime(DateTime.Today);

    /// <summary>
    /// Build a <see cref="StateEnrollmentGate"/> backed by a
    /// <see cref="MultiStateEnrollmentAggregator"/> whose single TX source
    /// returns <paramref name="record"/> for any NPI lookup.
    /// </summary>
    private static StateEnrollmentGate BuildGate(StateEnrollmentRecord? record)
    {
        var source = Substitute.For<IStateEnrollmentSource>();
        source.StateCode.Returns("TX");
        source.GetEnrollmentAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(record);

        var aggregator = new MultiStateEnrollmentAggregator(
            new[] { source },
            Options.Create(new ProviderEnrollmentOptions()),
            Substitute.For<ILogger<MultiStateEnrollmentAggregator>>());

        return new StateEnrollmentGate(
            aggregator,
            Substitute.For<ILogger<StateEnrollmentGate>>());
    }

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

    // ═════════════════════════════════════════════════════════════════
    //  Tests 1-4, 8, 9 — Tenant-config-aware behaviour
    //
    //  The current StateEnrollmentGate is a pure enrollment-status
    //  checker.  It does NOT depend on IHttpContextAccessor,
    //  ITenantEnrollmentConfigRepository, or gate-mode (Disabled /
    //  Warn / Enforce).  Those concerns live in the orchestrating
    //  layer (PasAutoAdjudicator / benefit-plan-service controller).
    //
    //  The tests below are marked Skip so they appear as a visible
    //  TODO in test output rather than silently asserting the wrong
    //  thing.  Once the gate acquires tenant-config awareness (or a
    //  decorator is added), remove the Skip attribute and implement
    //  the body.
    // ═════════════════════════════════════════════════════════════════

    [Fact(Skip = "StateEnrollmentGate does not depend on IHttpContextAccessor — tenant context check not yet implemented in gate")]
    public void EvaluateAsync_NoTenantContext_ReturnsPass()
    {
        // IHttpContextAccessor returns null HttpContext
        // Assert GateResult.Passed == true
    }

    [Fact(Skip = "StateEnrollmentGate does not depend on ITenantEnrollmentConfigRepository — tenant config lookup not yet implemented in gate")]
    public void EvaluateAsync_NoTenantConfig_ReturnsPass()
    {
        // ITenantEnrollmentConfigRepository returns null
        // Assert GateResult.Passed == true
    }

    [Fact(Skip = "StateEnrollmentGate does not check EnrollmentGateMode — gate-mode logic not yet implemented in gate")]
    public void EvaluateAsync_GateModeDisabled_ReturnsPass()
    {
        // TenantEnrollmentConfig.DefaultGateMode = Disabled
        // Assert GateResult.Passed == true regardless of enrollment status
    }

    [Fact(Skip = "StateEnrollmentGate does not check EnrollmentGateMode — warn-only path not yet implemented in gate")]
    public void EvaluateAsync_GateModeWarn_EnrollmentFails_StillReturnsPass_AndLogs()
    {
        // GateMode = Warn, aggregator returns null (not enrolled)
        // Assert GateResult.Passed == true
        // Assert ILogger received a warning containing "warn-only"
    }

    [Fact(Skip = "StateEnrollmentGate does not resolve LobOverrides — LOB override logic not yet implemented in gate")]
    public void EvaluateAsync_GateModeEnforce_LobOverride_Marketplace_Disabled_ReturnsPass()
    {
        // LobOverride for Marketplace: GateMode = Disabled
        // Assert GateResult.Passed == true even when provider not enrolled
    }

    [Fact(Skip = "StateEnrollmentGate does not filter by EnabledStateCodes — enabled-state check not yet implemented in gate (aggregator returns null → Deny, not Pass)")]
    public void EvaluateAsync_StateNotInEnabledList_ReturnsPass()
    {
        // TenantConfig.EnabledStateCodes = ["CA"], request stateCode = "TX"
        // Assert GateResult.Passed == true
    }

    // ═════════════════════════════════════════════════════════════════
    //  Tests 5, 6, 7 — Pure enrollment-status checks (implemented)
    // ═════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EvaluateAsync_GateModeEnforce_ProviderNotFound_ReturnsDeny_PEMS001()
    {
        // Arrange — aggregator returns null (NPI unknown to TX PEMS)
        var gate = BuildGate(record: null);

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
        var gate = BuildGate(record);

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
        var gate = BuildGate(record);

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
    //  Additional gate-logic coverage (not in original spec but
    //  exercising code paths present in the current implementation)
    // ═════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EvaluateAsync_ActiveProvider_MatchingTaxonomy_ReturnsPass()
    {
        // Arrange — fully enrolled active provider
        var record = MakeActiveRecord();
        var gate = BuildGate(record);

        // Act
        var result = await gate.EvaluateAsync(
            Npi, Taxonomy, StateCode, _serviceDate, LineOfBusiness.Medicaid);

        // Assert
        result.Passed.Should().BeTrue();
        result.DenialCode.Should().BeNull();
    }

    [Fact]
    public async Task EvaluateAsync_RevalidationRequired_ReturnsDeny_PEMS004()
    {
        var record = MakeActiveRecord() with { Status = EnrollmentStatus.RevalidationRequired };
        var gate = BuildGate(record);

        var result = await gate.EvaluateAsync(
            Npi, Taxonomy, StateCode, _serviceDate, LineOfBusiness.Medicaid);

        result.Passed.Should().BeFalse();
        result.DenialCode.Should().Be("PEMS-004");
    }

    [Fact]
    public async Task EvaluateAsync_LobNotSupported_ReturnsDeny_PEMS005()
    {
        // Provider enrolled only for Medicaid, request is for CHIP
        var record = MakeActiveRecord(lobs: LineOfBusiness.Medicaid);
        var gate = BuildGate(record);

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
        var gate = BuildGate(record);

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
        var gate = BuildGate(record);

        var result = await gate.EvaluateAsync(
            Npi, Taxonomy, StateCode, _serviceDate, LineOfBusiness.Medicaid);

        result.Passed.Should().BeFalse();
        result.DenialCode.Should().Be("PEMS-001");
        result.DenialReason.Should().Contain("terminated");
    }
}
