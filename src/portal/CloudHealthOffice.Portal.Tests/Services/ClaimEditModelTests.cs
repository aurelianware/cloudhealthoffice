using System.Text.Json.Nodes;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

/// <summary>
/// The examiner's claim editor model: edits land on a copy of the service's
/// payload, everything the editor doesn't show goes back untouched, and the
/// change list says what the correction does.
/// </summary>
public class ClaimEditModelTests
{
    private static JsonObject Paid() => JsonNode.Parse("""
        {
          "id": "claim-1",
          "claimNumber": "CLM-1",
          "memberId": "MEM-1",
          "billingProviderNPI": "1234567893",
          "renderingProviderNPI": null,
          "placeOfServiceCode": "11",
          "claimType": 0,
          "totalChargeAmount": 400,
          "serviceDateFrom": "2026-10-01T00:00:00Z",
          "serviceDateTo": "2026-10-01T00:00:00Z",
          "otherPayers": [ { "payerName": "Acme TPL", "paidAmount": 12.5 } ],
          "institutional": null,
          "diagnosisCodes": [
            { "code": "E119", "codeQualifier": "ABK", "pointerNumber": 1, "description": "Type 2 diabetes" },
            { "code": "I10", "codeQualifier": "ABF", "pointerNumber": 2 }
          ],
          "claimLines": [
            { "lineNumber": 1, "procedureCode": "99213", "procedureDescription": "Office visit", "modifiers": ["25"],
              "diagnosisPointers": [1], "units": 1, "chargeAmount": 100, "serviceDateFrom": "2026-10-01T00:00:00Z",
              "serviceDateTo": "2026-10-01T00:00:00Z", "nationalDrugCode": null, "adjudicationResult": null },
            { "lineNumber": 2, "procedureCode": "J1100", "modifiers": [], "diagnosisPointers": [1, 2],
              "units": 3, "chargeAmount": 300, "serviceDateFrom": "2026-10-01T00:00:00Z",
              "serviceDateTo": "2026-10-01T00:00:00Z", "nationalDrugCode": "00009004701",
              "adjudicationResult": { "allowedAmount": 90 } }
          ]
        }
        """)!.AsObject();

    private static JsonObject Line(JsonObject claim, int index) =>
        claim["claimLines"]!.AsArray()[index]!.AsObject();

    [Fact]
    public void Reads_the_professional_fields()
    {
        var model = ClaimEditModel.FromJson(Paid());

        model.ClaimNumber.Should().Be("CLM-1");
        model.MemberId.Should().Be("MEM-1");
        model.Diagnoses.Select(d => d.Code).Should().Equal("E119", "I10");
        model.Lines.Should().HaveCount(2);
        model.Lines[0].ModifiersText.Should().Be("25");
        model.Lines[1].DiagnosisPointersText.Should().Be("1, 2");
        model.Lines[1].Units.Should().Be(3);
        model.Lines[1].ServiceDateFrom.Should().Be(new DateTime(2026, 10, 1));
        model.TotalCharge.Should().Be(400);
        model.Changes().Should().BeEmpty();
        model.Validate().Should().BeEmpty();
    }

    [Fact]
    public void Unedited_claim_goes_back_with_every_field_it_came_with()
    {
        var source = Paid();
        var result = ClaimEditModel.FromJson(source).ApplyTo();

        result["otherPayers"]!.ToJsonString().Should().Be(source["otherPayers"]!.ToJsonString());
        Line(result, 1)["nationalDrugCode"]!.GetValue<string>().Should().Be("00009004701");
        Line(result, 0)["procedureDescription"]!.GetValue<string>().Should().Be("Office visit");
        result["serviceDateFrom"]!.GetValue<string>().Should().Be("2026-10-01T00:00:00Z", "an unchanged date is not rewritten");
        result["diagnosisCodes"]![0]!["description"]!.GetValue<string>().Should().Be("Type 2 diabetes");
        Line(result, 1).ContainsKey("adjudicationResult").Should().BeFalse("the corrected version is adjudicated from scratch");
    }

    [Fact]
    public void A_units_fix_shows_in_the_changes_and_the_payload()
    {
        var model = ClaimEditModel.FromJson(Paid());

        model.Lines[1].Units = 1;
        model.Lines[1].ChargeAmount = 100;

        model.Changes().Should().ContainSingle().Which.Should().StartWith("Line 2:").And.Contain("× 3").And.Contain("× 1");
        var result = model.ApplyTo();
        Line(result, 1)["units"]!.GetValue<decimal>().Should().Be(1);
        Line(result, 1)["chargeAmount"]!.GetValue<decimal>().Should().Be(100);
        result["totalChargeAmount"]!.GetValue<decimal>().Should().Be(200);
    }

    [Fact]
    public void Changing_a_procedure_drops_its_old_description()
    {
        var model = ClaimEditModel.FromJson(Paid());
        model.Lines[0].ProcedureCode = "99214";

        var line = Line(model.ApplyTo(), 0);

        line["procedureCode"]!.GetValue<string>().Should().Be("99214");
        line.ContainsKey("procedureDescription").Should().BeFalse();
    }

    [Fact]
    public void Removing_the_principal_diagnosis_renumbers_pointers_and_promotes_the_next_code()
    {
        var model = ClaimEditModel.FromJson(Paid());

        model.RemoveDiagnosis(model.Diagnoses[0]);

        model.Lines[0].DiagnosisPointers.Should().BeEmpty();
        model.Lines[1].DiagnosisPointers.Should().Equal(1);
        model.Validate().Should().Contain("Line 1: point to at least one diagnosis.");

        model.Lines[0].DiagnosisPointers = new() { 1 };
        var dx = model.ApplyTo()["diagnosisCodes"]!.AsArray();
        dx.Should().ContainSingle();
        dx[0]!["code"]!.GetValue<string>().Should().Be("I10");
        dx[0]!["codeQualifier"]!.GetValue<string>().Should().Be("ABK");
        dx[0]!["pointerNumber"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public void Added_lines_are_numbered_and_dated_like_the_last_line()
    {
        var model = ClaimEditModel.FromJson(Paid());
        var line = model.AddLine();
        line.ProcedureCode = "36415";
        line.ChargeAmount = 15;

        model.Changes().Should().ContainSingle().Which.Should().StartWith("Line 3 added: 36415");
        var added = Line(model.ApplyTo(), 2);
        added["lineNumber"]!.GetValue<int>().Should().Be(3);
        added["serviceDateFrom"]!.GetValue<string>().Should().StartWith("2026-10-01");
        added["diagnosisPointers"]!.AsArray().Select(p => p!.GetValue<int>()).Should().Equal(1);
    }

    [Fact]
    public void Removing_a_line_renumbers_the_rest_and_recomputes_the_total()
    {
        var model = ClaimEditModel.FromJson(Paid());
        model.RemoveLine(model.Lines[0]);

        var result = model.ApplyTo();

        result["claimLines"]!.AsArray().Should().ContainSingle();
        Line(result, 0)["procedureCode"]!.GetValue<string>().Should().Be("J1100");
        Line(result, 0)["lineNumber"]!.GetValue<int>().Should().Be(1);
        result["totalChargeAmount"]!.GetValue<decimal>().Should().Be(300);
        model.Changes().Should().Contain(c => c.StartsWith("Line 1:")).And.Contain(c => c.StartsWith("Line 2 removed"));
    }

    [Fact]
    public void Moving_a_service_date_moves_the_claim_span_and_keeps_the_utc_marker()
    {
        var model = ClaimEditModel.FromJson(Paid());
        model.Lines[1].ServiceDateFrom = new DateTime(2026, 10, 3);
        model.Lines[1].ServiceDateTo = new DateTime(2026, 10, 3);

        var result = model.ApplyTo();

        Line(result, 1)["serviceDateFrom"]!.GetValue<string>().Should().Be("2026-10-03T00:00:00Z");
        result["serviceDateFrom"]!.GetValue<string>().Should().Be("2026-10-01T00:00:00Z");
        result["serviceDateTo"]!.GetValue<string>().Should().Be("2026-10-03T00:00:00Z");
    }

    [Fact]
    public void Validation_names_each_problem()
    {
        var model = ClaimEditModel.FromJson(Paid());
        model.BillingProviderNpi = "12345";
        model.RenderingProviderNpi = "abc";
        model.Lines[0].ProcedureCode = " ";
        model.Lines[0].Units = 0;
        model.Lines[1].DiagnosisPointersText = "1, 7";
        model.Lines[1].ServiceDateTo = new DateTime(2026, 9, 30);

        model.Validate().Should().BeEquivalentTo(
            "Billing provider NPI must be 10 digits.",
            "Rendering provider NPI must be 10 digits.",
            "Line 1: procedure code is required.",
            "Line 1: units must be more than zero.",
            "Line 2: service end date is before the start date.",
            "Line 2: a diagnosis pointer doesn't match a diagnosis row.");
    }

    [Fact]
    public void Modifier_and_pointer_text_is_parsed_leniently()
    {
        var model = ClaimEditModel.FromJson(Paid());
        model.Lines[0].ModifiersText = "25,59 lt";
        model.Lines[0].DiagnosisPointersText = "2; 1";

        var line = Line(model.ApplyTo(), 0);

        line["modifiers"]!.AsArray().Select(m => m!.GetValue<string>()).Should().Equal("25", "59", "LT");
        line["diagnosisPointers"]!.AsArray().Select(p => p!.GetValue<int>()).Should().Equal(2, 1);
    }

    [Fact]
    public void The_source_payload_is_never_modified()
    {
        var source = Paid();
        var before = source.ToJsonString();
        var model = ClaimEditModel.FromJson(source);
        model.MemberId = "MEM-2";
        model.ApplyTo();

        source.ToJsonString().Should().Be(before);
    }
}
