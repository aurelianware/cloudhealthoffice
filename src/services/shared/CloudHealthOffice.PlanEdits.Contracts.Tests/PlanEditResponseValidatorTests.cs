using System.Text.Json;

namespace CloudHealthOffice.PlanEdits.Contracts.Tests;

public class PlanEditResponseValidatorTests
{
    private static readonly int[] Lines = [1, 2, 3];

    private static PlanEditResponse Response(params PlanEdit[] edits) =>
        new() { ContractVersion = "1", Edits = edits };

    private static PlanEdit Review(string ruleId = "PLAN-001", params int[] lines) =>
        new() { RuleId = ruleId, Message = "Review this", LineNumbers = lines };

    [Fact]
    public void No_edits_is_a_valid_answer()
    {
        PlanEditResponseValidator.Validate(Response(), Lines).Should().BeEmpty();
    }

    [Fact]
    public void Review_and_denial_edits_are_valid()
    {
        var denial = new PlanEdit { RuleId = "PLAN.NC_7", Message = "Not covered by the plan", LineNumbers = [2], SuggestedCarc = "96", SuggestedRarc = "N130" };

        PlanEditResponseValidator.Validate(Response(Review(), Review("claim-level"), denial), Lines).Should().BeEmpty();
    }

    [Fact]
    public void A_missing_body_version_or_edits_array_is_invalid()
    {
        PlanEditResponseValidator.Validate(null, Lines).Should().ContainSingle().Which.Should().StartWith("response:");
        PlanEditResponseValidator.Validate(new PlanEditResponse { ContractVersion = "2", Edits = [] }, Lines)
            .Should().ContainSingle().Which.Should().StartWith("contractVersion:");
        PlanEditResponseValidator.Validate(new PlanEditResponse { ContractVersion = "1" }, Lines)
            .Should().ContainSingle().Which.Should().StartWith("edits:");
    }

    [Theory]
    [InlineData("")]
    [InlineData("-leading-dash")]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    [InlineData("ABCDEFGHIJABCDEFGHIJABCDEFGHIJABCDEFGHIJX")] // 41 characters
    public void A_bad_rule_id_is_invalid(string ruleId)
    {
        PlanEditResponseValidator.Validate(Response(Review(ruleId)), Lines)
            .Should().ContainSingle().Which.Should().StartWith("edits[0].ruleId:");
    }

    [Fact]
    public void A_missing_or_long_message_is_invalid()
    {
        PlanEditResponseValidator.Validate(Response(new PlanEdit { RuleId = "R1", Message = " " }), Lines)
            .Should().ContainSingle().Which.Should().StartWith("edits[0].message:");
        PlanEditResponseValidator.Validate(Response(new PlanEdit { RuleId = "R1", Message = new string('x', 501) }), Lines)
            .Should().ContainSingle().Which.Should().StartWith("edits[0].message:");
    }

    [Fact]
    public void A_line_the_claim_does_not_have_is_invalid()
    {
        PlanEditResponseValidator.Validate(Response(Review("R1", 1, 9)), Lines)
            .Should().ContainSingle().Which.Should().Be("edits[0].lineNumbers: 9 is not a line on the claim");
    }

    [Fact]
    public void A_denial_edit_must_name_lines_and_use_code_shapes()
    {
        var claimLevelDenial = new PlanEdit { RuleId = "R1", Message = "m", SuggestedCarc = "96" };
        var badCarc = new PlanEdit { RuleId = "R2", Message = "m", LineNumbers = [1], SuggestedCarc = "9601" };
        var rarcAlone = new PlanEdit { RuleId = "R3", Message = "m", LineNumbers = [1], SuggestedRarc = "N130" };
        var badRarc = new PlanEdit { RuleId = "R4", Message = "m", LineNumbers = [1], SuggestedCarc = "96", SuggestedRarc = "130" };

        var problems = PlanEditResponseValidator.Validate(Response(claimLevelDenial, badCarc, rarcAlone, badRarc), Lines);

        problems.Should().BeEquivalentTo(
            "edits[0].suggestedCarc: a denial edit must name its lineNumbers",
            "edits[1].suggestedCarc: one to three upper-case letters or digits",
            "edits[2].suggestedRarc: needs a suggestedCarc",
            "edits[3].suggestedRarc: one or two upper-case letters and up to four digits");
    }

    [Fact]
    public void More_than_the_edit_limit_is_invalid()
    {
        var edits = Enumerable.Range(0, PlanEditContract.MaxEdits + 1).Select(i => Review($"R{i}")).ToArray();

        PlanEditResponseValidator.Validate(Response(edits), Lines).Should().Contain(p => p.StartsWith("edits: at most"));
    }

    [Fact]
    public void Problems_never_repeat_the_plans_message_text()
    {
        var edit = new PlanEdit { RuleId = "bad id", Message = "Member Jane Doe DOB 1980-01-01", LineNumbers = [42] };

        var problems = PlanEditResponseValidator.Validate(Response(edit), Lines);

        problems.Should().NotBeEmpty();
        problems.Should().NotContain(p => p.Contains("Jane") || p.Contains("1980"));
    }

    [Fact]
    public void The_wire_form_is_camel_case_omits_nulls_and_ignores_unknown_fields()
    {
        var json = JsonSerializer.Serialize(new PlanEditRequest { RequestId = "r1", TenantId = "t1" }, PlanEditJson.Options);
        json.Should().Contain("\"contractVersion\":\"1\"").And.Contain("\"requestId\":\"r1\"").And.NotContain("benefitPlanId");

        var response = JsonSerializer.Deserialize<PlanEditResponse>(
            """{"contractVersion":"1","futureField":true,"edits":[{"ruleId":"R1","message":"m","severity":"x"}]}""",
            PlanEditJson.Options);
        PlanEditResponseValidator.Validate(response, Lines).Should().BeEmpty();
    }

    [Fact]
    public void A_number_sent_as_a_string_is_not_accepted()
    {
        var act = () => JsonSerializer.Deserialize<PlanEditResponse>(
            """{"contractVersion":"1","edits":[{"ruleId":"R1","message":"m","lineNumbers":["1"]}]}""",
            PlanEditJson.Options);

        act.Should().Throw<JsonException>();
    }
}
