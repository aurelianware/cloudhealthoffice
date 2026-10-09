using System.Text.RegularExpressions;
using ClaimsService.EDI.Validation;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.EDI.Snip;

/// <summary>
/// The smoke scripts post hand-written 837s to <c>import/raw837</c>, which
/// now runs SNIP validation first. Keep those payloads passing the default
/// gate (levels 1–4 reject) so the smoke runs don't start failing at intake.
/// </summary>
public class InRepo837ProducersSnipTests
{
    [Theory]
    [InlineData("scripts/smoke/834-to-837-e2e-smoke.sh")]
    [InlineData("scripts/smoke/837-pended-claim-e2e-smoke.sh")]
    public void SmokeScriptPayload_PassesSnipValidation(string script)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), script));
        var raw = Regex.Match(text, "edi_837=\"(?<edi>ISA[^\"]+)\"").Groups["edi"].Value;
        Assert.False(string.IsNullOrEmpty(raw), "edi_837 payload not found");

        var edi = raw
            .Replace("$(date -u +%y%m%d)", "260115")
            .Replace("$(date -u +%Y%m%d)", "20260115")
            .Replace("$(date -u +%H%M)", "1200")
            .Replace("${SERVICE_DATE}", "20260110")
            .Replace("${PROVIDER_NPI}", "1234567893");
        edi = Regex.Replace(edi, @"\$\{[A-Z_]+\}", "SMOKE001");

        // Fully clean: no errors and no warnings (e.g. no decimal-point ICD
        // codes, no claim-level DTP*472).
        var result = new X12837SnipValidator().Validate(edi);
        Assert.Empty(result.AllIssues.Select(i => $"{i.RuleId}: {i.Message}"));
        Assert.Equal("A", result.AcknowledgmentCode);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "cloudhealthoffice-main.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
