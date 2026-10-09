using ClaimsService.EDI.Validation;
using Xunit;

namespace CloudHealthOffice.GoldenPath.Tests;

/// <summary>
/// The golden-path 837 fixtures go through the same WEDI SNIP 1–5 gate as
/// <c>POST /api/v1/claims/import/raw837</c>, with the default configuration
/// (levels 1–4 reject, level 5 warns). A golden input that the intake would
/// reject is a broken golden input. Needs no database, so it runs everywhere.
/// </summary>
public class GoldenPathSnipTests
{
    [Theory]
    [InlineData("01-office-visit")]
    [InlineData("02-multi-line-mppr")]
    [InlineData("03-percent-of-medicare")]
    [InlineData("04-denied-line")]
    [InlineData("05-inpatient-drg")]
    [InlineData("06-oop-max")]
    [InlineData("07-tertiary-cob")]
    public void GoldenFixture_PassesSnipValidation(string name)
    {
        var result = new X12837SnipValidator().Validate(GoldenInputs.Edi837(name));

        var errors = result.AllIssues.Where(i => i.Severity == SnipSeverity.Error)
            .Select(i => $"{i.RuleId} @ {i.SegmentId}{i.SegmentPosition}: {i.Message}")
            .ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
        Assert.NotEqual("R", result.AcknowledgmentCode);
        Assert.NotNull(X12999AcknowledgmentBuilder.Build(result));
    }
}
