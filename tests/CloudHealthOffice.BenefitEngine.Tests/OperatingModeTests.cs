using Xunit;
using CloudHealthOffice.OperatingMode;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.BenefitEngine.Tests;

/// <summary>
/// Tests for the augment/replace operating mode core types:
/// OperatingModeConfiguration, AugmentResult, and OperatingModeInfo.
/// </summary>
public class OperatingModeTests
{
    // ═══════════════════════════════════════════════════════════════════
    // OperatingModeConfiguration — GetEngineMode
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void GetEngineMode_ConfiguredAsAugment_ReturnsAugment()
    {
        var config = new OperatingModeConfiguration
        {
            Engines = new(StringComparer.OrdinalIgnoreCase)
            {
                { "benefitCalculation", "augment" }
            }
        };

        var mode = config.GetEngineMode("benefitCalculation");

        Assert.Equal(EngineOperatingMode.Augment, mode.Mode);
        Assert.False(mode.IsAuthoritative);
    }

    [Fact]
    public void GetEngineMode_ConfiguredAsReplace_ReturnsReplace()
    {
        var config = new OperatingModeConfiguration
        {
            Engines = new(StringComparer.OrdinalIgnoreCase)
            {
                { "ncciEdits", "replace" }
            }
        };

        var mode = config.GetEngineMode("ncciEdits");

        Assert.Equal(EngineOperatingMode.Replace, mode.Mode);
        Assert.True(mode.IsAuthoritative);
    }

    [Fact]
    public void GetEngineMode_UnknownEngine_DefaultsToReplace()
    {
        var config = new OperatingModeConfiguration();

        var mode = config.GetEngineMode("unknownEngine");

        Assert.Equal(EngineOperatingMode.Replace, mode.Mode);
        Assert.True(mode.IsAuthoritative);
    }

    [Fact]
    public void GetEngineMode_InvalidModeString_DefaultsToReplace()
    {
        var config = new OperatingModeConfiguration
        {
            Engines = new(StringComparer.OrdinalIgnoreCase)
            {
                { "benefitCalculation", "invalid_mode" }
            }
        };

        var mode = config.GetEngineMode("benefitCalculation");

        Assert.Equal(EngineOperatingMode.Replace, mode.Mode);
    }

    [Fact]
    public void GetEngineMode_CaseInsensitive()
    {
        var config = new OperatingModeConfiguration
        {
            Engines = new(StringComparer.OrdinalIgnoreCase)
            {
                { "BenefitCalculation", "AUGMENT" }
            }
        };

        var mode = config.GetEngineMode("benefitcalculation");

        Assert.Equal(EngineOperatingMode.Augment, mode.Mode);
    }

    // ═══════════════════════════════════════════════════════════════════
    // OperatingModeConfiguration — SetEngineMode
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void SetEngineMode_SetsValueAndUpdatesTimestamp()
    {
        var config = new OperatingModeConfiguration();

        config.SetEngineMode("benefitCalculation", EngineOperatingMode.Augment);

        Assert.Equal("augment", config.Engines["benefitCalculation"]);
        Assert.NotNull(config.UpdatedAt);
    }

    [Fact]
    public void SetEngineMode_TrimsWhitespace()
    {
        var config = new OperatingModeConfiguration();

        config.SetEngineMode("  rateResolution  ", EngineOperatingMode.Replace);

        Assert.True(config.Engines.ContainsKey("rateResolution"));
    }

    [Fact]
    public void SetEngineMode_OverwritesExisting()
    {
        var config = new OperatingModeConfiguration
        {
            Engines = new(StringComparer.OrdinalIgnoreCase)
            {
                { "ncciEdits", "augment" }
            }
        };

        config.SetEngineMode("ncciEdits", EngineOperatingMode.Replace);

        Assert.Equal("replace", config.Engines["ncciEdits"]);
    }

    // ═══════════════════════════════════════════════════════════════════
    // OperatingModeConfiguration — EngineNames constants
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void EngineNames_AllDefined()
    {
        Assert.Equal("benefitCalculation", OperatingModeConfiguration.EngineNames.BenefitCalculation);
        Assert.Equal("rateResolution", OperatingModeConfiguration.EngineNames.RateResolution);
        Assert.Equal("ncciEdits", OperatingModeConfiguration.EngineNames.NcciEdits);
        Assert.Equal("claimsScrubbing", OperatingModeConfiguration.EngineNames.ClaimsScrubbing);
        Assert.Equal("cobCalculation", OperatingModeConfiguration.EngineNames.CobCalculation);
        Assert.Equal("riskAdjustment", OperatingModeConfiguration.EngineNames.RiskAdjustment);
    }

    // ═══════════════════════════════════════════════════════════════════
    // OperatingModeInfo — IsAuthoritative
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void OperatingModeInfo_Replace_IsAuthoritative()
    {
        var info = new OperatingModeInfo { Mode = EngineOperatingMode.Replace };
        Assert.True(info.IsAuthoritative);
    }

    [Fact]
    public void OperatingModeInfo_Augment_IsNotAuthoritative()
    {
        var info = new OperatingModeInfo { Mode = EngineOperatingMode.Augment };
        Assert.False(info.IsAuthoritative);
    }

    // ═══════════════════════════════════════════════════════════════════
    // AugmentResult — ForReplace
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ForReplace_ChoResultIsAuthoritative()
    {
        var result = AugmentResult.ForReplace("hello");

        Assert.Equal("hello", result.ChoResult);
        Assert.True(result.Authoritative);
        Assert.Equal(EngineOperatingMode.Replace, result.Mode);
        Assert.Null(result.LegacyResult);
        Assert.Empty(result.Discrepancies);
        Assert.Null(result.ComparedAt);
    }

    // ═══════════════════════════════════════════════════════════════════
    // AugmentResult — ForAugment
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ForAugment_WithLegacyResult_SetsComparedAt()
    {
        var result = AugmentResult.ForAugment(
            choResult: 100,
            legacyResult: 95,
            discrepancies: new[] { "Amount differs: CHO=100, Legacy=95" });

        Assert.Equal(100, result.ChoResult);
        Assert.Equal(95, result.LegacyResult);
        Assert.False(result.Authoritative);
        Assert.Equal(EngineOperatingMode.Augment, result.Mode);
        Assert.Single(result.Discrepancies);
        Assert.NotNull(result.ComparedAt);
    }

    [Fact]
    public void ForAugment_WithoutLegacyResult_ComparedAtIsNull()
    {
        var result = AugmentResult.ForAugment<string?>(
            choResult: "cho-only",
            legacyResult: null,
            discrepancies: Array.Empty<string>());

        Assert.Equal("cho-only", result.ChoResult);
        Assert.Null(result.ComparedAt);
        Assert.Empty(result.Discrepancies);
    }

    [Fact]
    public void ForAugment_NoDiscrepancies_EmptyArray()
    {
        var result = AugmentResult.ForAugment(
            choResult: "match",
            legacyResult: "match",
            discrepancies: Array.Empty<string>());

        Assert.Empty(result.Discrepancies);
        Assert.NotNull(result.ComparedAt); // legacy provided, so compared
    }

    [Fact]
    public void ForAugment_MultipleDiscrepancies_AllCaptured()
    {
        var discrepancies = new[]
        {
            "Plan paid differs: CHO=$145.00, Legacy=$140.00",
            "Copay differs: CHO=$30.00, Legacy=$25.00",
            "Deductible applied differs: CHO=$0.00, Legacy=$50.00"
        };

        var result = AugmentResult.ForAugment(
            choResult: "cho", legacyResult: "legacy", discrepancies: discrepancies);

        Assert.Equal(3, result.Discrepancies.Length);
        Assert.Contains("Plan paid", result.Discrepancies[0]);
        Assert.Contains("Deductible", result.Discrepancies[2]);
    }

    // ═══════════════════════════════════════════════════════════════════
    // AugmentResult — ForAugment with logging
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ForAugment_WithLogger_DiscrepanciesLoggedAsWarning()
    {
        var logger = new TestLogger();

        AugmentResult.ForAugment(
            choResult: 100, legacyResult: 95,
            discrepancies: new[] { "Amount differs" },
            logger, "benefitCalculation", "tenant-1");

        Assert.Contains(logger.Messages, m => m.Level == LogLevel.Warning);
        Assert.Contains(logger.Messages, m => m.Message.Contains("benefitCalculation"));
        Assert.Contains(logger.Messages, m => m.Message.Contains("tenant-1"));
    }

    [Fact]
    public void ForAugment_WithLogger_NoDiscrepancies_LoggedAsInfo()
    {
        var logger = new TestLogger();

        AugmentResult.ForAugment(
            choResult: 100, legacyResult: 100,
            discrepancies: Array.Empty<string>(),
            logger, "ncciEdits", "tenant-2");

        Assert.Contains(logger.Messages, m => m.Level == LogLevel.Information);
        Assert.Contains(logger.Messages, m => m.Message.Contains("match"));
    }

    [Fact]
    public void ForAugment_WithLogger_NullLegacy_NoLogEntry()
    {
        var logger = new TestLogger();

        AugmentResult.ForAugment<string?>(
            choResult: "cho-only", legacyResult: null,
            discrepancies: Array.Empty<string>(),
            logger, "rateResolution", "tenant-3");

        Assert.Empty(logger.Messages);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Multi-engine configuration scenario
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void GradualMigration_MixedModes_PerEngine()
    {
        // Simulates a health plan in the middle of migration:
        // NCCI and rate resolution fully cut over (replace)
        // Benefit calculation still validating (augment)
        var config = new OperatingModeConfiguration
        {
            TenantId = "acme-health",
            Engines = new(StringComparer.OrdinalIgnoreCase)
            {
                { "benefitCalculation", "augment" },
                { "rateResolution", "replace" },
                { "ncciEdits", "replace" },
                { "claimsScrubbing", "augment" },
            }
        };

        Assert.False(config.GetEngineMode("benefitCalculation").IsAuthoritative);
        Assert.True(config.GetEngineMode("rateResolution").IsAuthoritative);
        Assert.True(config.GetEngineMode("ncciEdits").IsAuthoritative);
        Assert.False(config.GetEngineMode("claimsScrubbing").IsAuthoritative);
        // Unconfigured engine defaults to replace
        Assert.True(config.GetEngineMode("cobCalculation").IsAuthoritative);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test helper
    // ═══════════════════════════════════════════════════════════════════

    private class TestLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add((logLevel, formatter(state, exception)));
        }
    }
}
