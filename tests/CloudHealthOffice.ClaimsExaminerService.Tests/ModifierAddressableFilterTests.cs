using ClaimsExaminerService.Models;
using ClaimsExaminerService.Services.Examiner;
using CloudHealthOffice.Events;
using Xunit;

namespace CloudHealthOffice.ClaimsExaminerService.Tests;

/// <summary>
/// V1 of the AI Claims Examiner only acts on edits where a -59/X{EPSU} modifier
/// is a legal override path. These tests pin that scope so a future contributor
/// cannot quietly widen it without thinking through the safety implications.
/// </summary>
public class ModifierAddressableFilterTests
{
    [Fact]
    public void NcciPair_With_NE001_Is_Addressable()
    {
        var edit = new NcciEditFailureSnapshot
        {
            EditType = "NcciPair",
            RuleId = "NE001",
            Column1Code = "27447",
            Column2Code = "27486"
        };
        Assert.True(edit.IsModifierAddressable());
    }

    [Theory]
    [InlineData("Mue", "NE002")]   // MUE has no modifier override path
    [InlineData("NcciPair", "NE002")] // wrong rule for the type
    [InlineData("Mue", "NE001")]   // wrong type for the rule
    [InlineData("", "NE001")]
    [InlineData("NcciPair", "")]
    public void Other_Edits_Are_Not_Addressable(string editType, string ruleId)
    {
        var edit = new NcciEditFailureSnapshot { EditType = editType, RuleId = ruleId };
        Assert.False(edit.IsModifierAddressable());
    }

    [Fact]
    public void EditType_Match_Is_Case_Insensitive()
    {
        var edit = new NcciEditFailureSnapshot { EditType = "ncCipAir", RuleId = "ne001" };
        Assert.True(edit.IsModifierAddressable());
    }

    [Fact]
    public void SelectAddressableEdit_Returns_First_NcciPair_Skipping_Mue()
    {
        var details = new PendDetails
        {
            EditFailures = new List<NcciEditFailureSnapshot>
            {
                new() { EditType = "Mue", RuleId = "NE002" },
                new() { EditType = "NcciPair", RuleId = "NE001", Column1Code = "27447", Column2Code = "27486" },
                new() { EditType = "NcciPair", RuleId = "NE001", Column1Code = "11042", Column2Code = "97597" }
            }
        };

        var picked = ExaminerOrchestrator.SelectAddressableEdit(details);

        Assert.NotNull(picked);
        Assert.Equal("27447", picked!.Column1Code);
    }

    [Fact]
    public void SelectAddressableEdit_Returns_Null_When_No_Addressable_Edits()
    {
        var details = new PendDetails
        {
            EditFailures = new List<NcciEditFailureSnapshot>
            {
                new() { EditType = "Mue", RuleId = "NE002" }
            }
        };

        Assert.Null(ExaminerOrchestrator.SelectAddressableEdit(details));
    }

    [Fact]
    public void SelectAddressableEdit_Returns_Null_When_PendDetails_Null()
    {
        Assert.Null(ExaminerOrchestrator.SelectAddressableEdit(null));
    }

    [Fact]
    public void SelectAddressableEdit_Returns_Null_When_EditFailures_Empty()
    {
        var details = new PendDetails { EditFailures = new() };
        Assert.Null(ExaminerOrchestrator.SelectAddressableEdit(details));
    }
}
