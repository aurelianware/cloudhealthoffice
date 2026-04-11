using ClaimsExaminerService.Models;
using ClaimsExaminerService.Services.Examiner;
using CloudHealthOffice.Events;
using Xunit;

namespace CloudHealthOffice.ClaimsExaminerService.Tests;

public class PromptBuilderTests
{
    private readonly ExaminerPromptBuilder _builder = new();

    [Fact]
    public void System_Prompt_Constrains_Disposition_Vocabulary()
    {
        var system = _builder.BuildSystemPrompt();

        // The four allowed dispositions must all be referenced so the model
        // is anchored to the same vocabulary the schema enforces. If a future
        // edit drops one of these, the schema will reject the model's call but
        // the model won't know why.
        Assert.Contains("Approve", system);
        Assert.Contains("Deny", system);
        Assert.Contains("RequestInfo", system);
        Assert.Contains("EscalateToHuman", system);
    }

    [Fact]
    public void User_Message_Surfaces_Edit_And_Line_Detail()
    {
        var claim = new ClaimSnapshot
        {
            Id = "abc",
            ClaimNumber = "CLM-1",
            MemberId = "MBR-1",
            BillingProviderNPI = "1234567890",
            PlaceOfServiceCode = "22",
            ServiceDateFrom = new DateTime(2026, 4, 1),
            ServiceDateTo = new DateTime(2026, 4, 1),
            TotalChargeAmount = 800m,
            DiagnosisCodes = new()
            {
                new() { Code = "S83.511A", PointerNumber = 1, Description = "ACL tear" }
            },
            ClaimLines = new()
            {
                new()
                {
                    LineNumber = 1, ProcedureCode = "29888",
                    DiagnosisPointers = new() { 1 }, Units = 1, ChargeAmount = 700m
                },
                new()
                {
                    LineNumber = 2, ProcedureCode = "29870", Modifiers = new() { "59" },
                    DiagnosisPointers = new() { 1 }, Units = 1, ChargeAmount = 100m
                }
            }
        };

        var edit = new NcciEditFailureSnapshot
        {
            EditType = "NcciPair", RuleId = "NE001",
            Column1Code = "29888", Column2Code = "29870",
            AffectedLineNumbers = new() { 2 },
            ModifierOverridePresent = true,
            Message = "29870 bundled into 29888"
        };

        var msg = _builder.BuildUserMessage(claim, edit);

        // Edit metadata
        Assert.Contains("29888", msg);
        Assert.Contains("29870", msg);
        Assert.Contains("NE001", msg);
        Assert.Contains("True", msg); // ModifierOverridePresent rendered
        // Line evidence the model needs to reason about
        Assert.Contains("Line 1", msg);
        Assert.Contains("Line 2", msg);
        Assert.Contains("59", msg); // modifier on line 2
        Assert.Contains("S83.511A", msg);
        // Tool-name reminder
        Assert.Contains("recommend_disposition", msg);
    }

    [Fact]
    public void Tool_Schema_Enforces_Required_Fields_And_Disposition_Enum()
    {
        var tool = _builder.BuildRecommendationTool();
        var json = tool.InputSchema.ToJsonString();

        Assert.Equal("recommend_disposition", tool.Name);
        Assert.Contains("recommended_disposition", json);
        Assert.Contains("confidence_score", json);
        Assert.Contains("rationale", json);
        Assert.Contains("policy_citations", json);
        // The disposition vocabulary must be enforced as an enum at the schema level.
        Assert.Contains("Approve", json);
        Assert.Contains("Deny", json);
        Assert.Contains("RequestInfo", json);
        Assert.Contains("EscalateToHuman", json);
    }

    [Fact]
    public void Prompt_Version_Is_Stable()
    {
        // Prompt version is part of the persisted recommendation. If you bump it,
        // bump it intentionally and update this test — that's the point.
        Assert.Equal("ncci-pend-v1", _builder.PromptVersion);
    }

    [Fact]
    public void Rfai_History_Section_Is_Omitted_When_Null()
    {
        var msg = _builder.BuildUserMessage(MinimalClaim(), MinimalEdit(), rfaiHistory: null);
        Assert.DoesNotContain("Provider RFAI History", msg);
    }

    [Fact]
    public void Rfai_History_Section_Is_Omitted_When_Zero_Rfais_Sent()
    {
        // A history record with TotalRfaisSent=0 carries no signal — render nothing
        // rather than feeding the model an empty section that looks like data.
        var history = new ProviderRfaiHistory
        {
            EditCode = "NE001",
            TotalRfaisSent = 0
        };
        var msg = _builder.BuildUserMessage(MinimalClaim(), MinimalEdit(), history);
        Assert.DoesNotContain("Provider RFAI History", msg);
    }

    [Fact]
    public void Rfai_History_Section_Renders_When_Populated()
    {
        var history = new ProviderRfaiHistory
        {
            EditCode = "NE001",
            TotalRfaisSent = 12,
            TotalResponded = 3,
            ResponseRatePct = 25,
            AvgResponseDays = 18,
            LastRfaiSentAt = new DateTime(2026, 2, 14)
        };

        var msg = _builder.BuildUserMessage(MinimalClaim(), MinimalEdit(), history);

        Assert.Contains("Provider RFAI History", msg);
        Assert.Contains("12", msg);   // total RFAIs
        Assert.Contains("25", msg);   // response rate
        Assert.Contains("18", msg);   // avg response days
        Assert.Contains("2026-02-14", msg);
        // Critical: the section must explicitly tell the model the history is a soft
        // signal only — never sufficient grounds for Approve/Deny on its own.
        Assert.Contains("soft signal", msg);
    }

    private static ClaimSnapshot MinimalClaim() => new()
    {
        Id = "x", ClaimNumber = "x", MemberId = "x", BillingProviderNPI = "1234567890",
        ServiceDateFrom = new DateTime(2026, 4, 1), ServiceDateTo = new DateTime(2026, 4, 1),
        ClaimLines = new()
        {
            new() { LineNumber = 1, ProcedureCode = "27447", DiagnosisPointers = new() { 1 }, Units = 1, ChargeAmount = 100 },
            new() { LineNumber = 2, ProcedureCode = "27486", DiagnosisPointers = new() { 1 }, Units = 1, ChargeAmount = 50 }
        },
        DiagnosisCodes = new() { new() { Code = "M17.11", PointerNumber = 1 } }
    };

    private static NcciEditFailureSnapshot MinimalEdit() => new()
    {
        EditType = "NcciPair", RuleId = "NE001",
        Column1Code = "27447", Column2Code = "27486",
        AffectedLineNumbers = new() { 2 }
    };
}
