using ClaimsService.EDI.Inbound;
using ClaimsService.EDI.Validation;
using Microsoft.Extensions.Configuration;
using Xunit;
using static CloudHealthOffice.ClaimsService.Tests.EDI.Snip.Snip837Samples;

namespace CloudHealthOffice.ClaimsService.Tests.EDI.Snip;

/// <summary>
/// <see cref="SnipIntegrityRules"/> reject whatever the level setting or
/// partner override says; the service-date rule closes the 837I gap where a
/// file with no DTP*434 and no line DTP*472 used to pass; options with
/// undefined or misspelt actions fail validation.
/// </summary>
public class SnipIntegrityRulesTests
{
    private static SnipValidationResult Validate(string edi, Snip837ValidationOptions? options = null)
        => new X12837SnipValidator(options).Validate(edi);

    private static List<string> RemoveAll(List<string> body, string prefix)
    {
        body.RemoveAll(s => s.StartsWith(prefix, StringComparison.Ordinal));
        return body;
    }

    /// <summary>837I with no statement period and no line service dates.</summary>
    private static string InstitutionalWithoutDates() =>
        Wrap(InstitutionalVersion, RemoveAll(RemoveAll(InstitutionalBody(), "DTP*434"), "DTP*472"));

    public static TheoryData<string, string> PinnedCases() => new()
    {
        { "L2-HL02", Professional(b => b.Replace("HL*2", "HL*2*7*22*0")) },
        { "L2-HL03", Professional(b => b.Replace("HL*2", "HL*2*1*99*0")) },
        { "L2-CLM05-1", Professional(b => b.Replace("CLM*", "CLM*PCN0001*150.00***:B:1*Y*A*Y*Y")) },
        { "L2-SV203", Institutional(b => b.Replace("SV2*0300", "SV2*0300*HC:80053**UN*1")) },
        { "L2-HI-PRINCIPAL", Institutional(b => b.Replace("HI*ABK:R0789", "HI*ABJ:R0789")) },
        { "L4-DTP472", Professional(b => b.RemoveSegment("DTP*472")) },
        { "L4-SERVICE-DATE", InstitutionalWithoutDates() },
    };

    private static readonly string[] Lenient = ["level2-warn", "levels-off", "partner-warn"];

    private static Snip837ValidationOptions LenientOptions(string name) => name switch
    {
        "level2-warn" => new Snip837ValidationOptions { Level2 = SnipAction.Warn, Level4 = SnipAction.Warn },
        "levels-off" => new Snip837ValidationOptions { Level2 = SnipAction.Off, Level4 = SnipAction.Off },
        "partner-warn" => new Snip837ValidationOptions
        {
            Level2 = SnipAction.Reject,
            PartnerOverrides = { ["SUB001"] = new SnipLevelOverrides { Level2 = SnipAction.Warn, Level4 = SnipAction.Warn } },
        },
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(PinnedCases))]
    public void PinnedRule_RejectsWithDefaults(string ruleId, string edi)
    {
        var result = Validate(edi);
        Assert.Contains(result.AllIssues, i => i.RuleId == ruleId && i.Severity == SnipSeverity.Error);
        Assert.Equal("R", result.AcknowledgmentCode);
        Assert.Empty(result.AcceptedTransactionSets);
    }

    [Theory]
    [MemberData(nameof(PinnedCases))]
    public void PinnedRule_RejectsUnderWarnOffAndPartnerOverride(string ruleId, string edi)
    {
        foreach (var lenient in Lenient)
        {
            var result = Validate(edi, LenientOptions(lenient));
            Assert.True(result.AllIssues.Any(i => i.RuleId == ruleId && i.Severity == SnipSeverity.Error),
                $"{ruleId} did not reject under {lenient}");
            Assert.Equal("R", result.AcknowledgmentCode);
        }
    }

    [Fact]
    public void PinnedRules_AreDocumentedWithAReason()
    {
        Assert.All(SnipIntegrityRules.Rules, r => Assert.False(string.IsNullOrWhiteSpace(r.Reason)));
        Assert.Equal(PinnedCases().Count(), SnipIntegrityRules.Rules.Count);
    }

    // ── Unpinned rules still follow Level 2 ──────────────────────────

    [Fact]
    public void ProfessionalPrincipalDiagnosis_IsNotPinned()
    {
        var result = Validate(Professional(b => b.Replace("HI*", "HI*ABF:J069*ABF:R05")));
        Assert.Equal(SnipSeverity.Warning, Assert.Single(result.AllIssues, i => i.RuleId == "L2-HI-PRINCIPAL").Severity);
    }

    [Fact]
    public void Hl04Mismatch_StaysWarn()
    {
        var result = Validate(Professional(b => b.Replace("HL*2", "HL*2*1*22*1")));
        Assert.All(result.AllIssues.Where(i => i.RuleId == "L2-HL04"), i => Assert.Equal(SnipSeverity.Warning, i.Severity));
        Assert.Equal("E", result.AcknowledgmentCode);
    }

    [Fact]
    public void ClaimFrequencyMissing_IsLevel2CLM05_NotThePinnedFacilityRule()
    {
        var result = Validate(Professional(b => b.Replace("CLM*", "CLM*PCN0001*150.00***11:B*Y*A*Y*Y")));
        Assert.Equal(SnipSeverity.Warning, Assert.Single(result.AllIssues, i => i.RuleId == "L2-CLM05").Severity);
        Assert.DoesNotContain(result.AllIssues, i => i.RuleId == "L2-CLM05-1");
    }

    // ── Service dates ────────────────────────────────────────────────

    [Fact]
    public void Institutional_NoStatementPeriodAndNoLineDates_IsRejectedPerLine()
    {
        var result = Validate(InstitutionalWithoutDates());
        var issues = result.AllIssues.Where(i => i.RuleId == "L4-SERVICE-DATE").ToList();
        Assert.Equal(2, issues.Count);
        Assert.All(issues, i => Assert.Equal(("DTP", "2400", "I6"), (i.SegmentId, i.Loop, i.SegmentErrorCode)));
        Assert.Contains("IK5*R", X12999AcknowledgmentBuilder.Build(result, new X12999AcknowledgmentBuilder.Options { ControlNumber = 1 })!);
    }

    [Fact]
    public void Institutional_LineDatesWithoutStatementPeriod_IsNotAServiceDateReject()
    {
        var result = Validate(Institutional(b => b.RemoveSegment("DTP*434")));
        Assert.DoesNotContain(result.AllIssues, i => i.RuleId == "L4-SERVICE-DATE");
        Assert.Equal(SnipSeverity.Warning, Assert.Single(result.AllIssues, i => i.RuleId == "L2-DTP434").Severity);
    }

    [Fact]
    public void Institutional_StatementPeriodWithoutLineDates_FallsBackToStatement()
    {
        // Single-day statement: line dates fall back to DTP*434, so no reject.
        var result = Validate(Wrap(InstitutionalVersion, RemoveAll(
            InstitutionalBody().Select(s => s.StartsWith("DTP*434", StringComparison.Ordinal) ? "DTP*434*RD8*20260105-20260105" : s).ToList(),
            "DTP*472")));
        Assert.DoesNotContain(result.AllIssues, i => i.RuleId is "L4-SERVICE-DATE" or "L4-DTP472");
        Assert.True(result.TransactionSets.Single().Accepted);
    }

    // ── Options validation ───────────────────────────────────────────

    private static Microsoft.Extensions.Options.ValidateOptionsResult ValidateConfig(Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var options = new Snip837ValidationOptions();
        try
        {
            config.GetSection(Snip837ValidationOptions.SectionName).Bind(options);
        }
        catch (InvalidOperationException ex)
        {
            return Microsoft.Extensions.Options.ValidateOptionsResult.Fail(ex.Message);
        }
        return new Snip837ValidationOptionsValidator(config).Validate(null, options);
    }

    [Fact]
    public void OptionsValidation_AcceptsNamedActions()
    {
        var result = ValidateConfig(new()
        {
            ["ClaimsImport:Snip:Level2"] = "warn",
            ["ClaimsImport:Snip:PartnerOverrides:SUB001:Level2"] = "Reject",
            ["ClaimsImport:Snip:PartnerOverrides:SUB001:Level5"] = "Off",
        });
        Assert.True(result.Succeeded, result.FailureMessage);
    }

    [Theory]
    [InlineData("ClaimsImport:Snip:PartnerOverrides:SUB001:Level2", "Rejct")]
    [InlineData("ClaimsImport:Snip:PartnerOverrides:SUB001:Level2", "7")]
    [InlineData("ClaimsImport:Snip:PartnerOverrides:SUB001:Level2", "1")]
    [InlineData("ClaimsImport:Snip:PartnerOverrides:SUB001:Level9", "Reject")]
    [InlineData("ClaimsImport:Snip:PartnerOverrides:SUB001", "")]
    [InlineData("ClaimsImport:Snip:Level2", "5")]
    [InlineData("ClaimsImport:Snip:Level3", "Rejct")]
    public void OptionsValidation_FailsFast(string key, string value)
    {
        var result = ValidateConfig(new() { [key] = value });
        Assert.True(result.Failed, $"{key} = '{value}' was accepted");
    }

    [Fact]
    public void OptionsValidation_RejectsUndefinedBoundValuesAndNullEntries()
    {
        var options = new Snip837ValidationOptions
        {
            Level4 = (SnipAction)5,
            PartnerOverrides =
            {
                ["SUB001"] = new SnipLevelOverrides { Level2 = (SnipAction)7 },
                ["SUB002"] = null!,
            },
        };
        var result = new Snip837ValidationOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains("Level4", result.FailureMessage);
        Assert.Contains("SUB001", result.FailureMessage);
        Assert.Contains("SUB002", result.FailureMessage);
    }
}
