using System.Text.Json;
using ClaimsService.Import.Cpcds.Drop;
using ClaimsService.Models;
using FluentAssertions;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Import.Cpcds.Drop;

/// <summary>
/// The drop API names its states (claims-service's MVC JSON has no string-enum
/// converter of its own), so the portal console and scripts read "Failed", not 3.
/// </summary>
public class CpcdsDropWireFormatTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void A_run_names_its_status_and_each_files_kind()
    {
        var run = new CpcdsDropRun
        {
            Id = "run-1",
            Status = CpcdsDropRunStatus.CompletedWithErrors,
            Files = [new CpcdsDropRunFile { Name = "auths.csv", Kind = CpcdsDropFileKind.Authorizations }],
            Steps = [new CpcdsDropStep { Name = "claims", Status = CpcdsDropStepStatus.Failed }],
        };

        var json = JsonSerializer.Serialize(run, Web);

        json.Should().Contain("\"status\":\"CompletedWithErrors\"")
            .And.Contain("\"kind\":\"Authorizations\"")
            .And.Contain("\"status\":\"Failed\"");
    }

    [Fact]
    public void The_report_and_settings_view_name_their_states()
    {
        JsonSerializer.Serialize(new CpcdsFreshnessReport { Status = CpcdsFreshness.Stale }, Web)
            .Should().Contain("\"status\":\"Stale\"");
        JsonSerializer.Serialize(new CpcdsDropSettingsView
            {
                Source = CpcdsDropSettingsSource.Stored,
                Settings = new CpcdsDropSettingsRequest { LineOfBusiness = LineOfBusiness.Medicaid },
            }, Web)
            .Should().Contain("\"source\":\"Stored\"").And.Contain("\"lineOfBusiness\":\"Medicaid\"");
    }

    [Theory]
    [InlineData("\"Medicaid\"")]
    [InlineData("3")]
    public void Settings_take_the_line_of_business_by_name_or_number(string value)
    {
        var request = JsonSerializer.Deserialize<CpcdsDropSettingsRequest>(
            $$"""{ "enabled": true, "lineOfBusiness": {{value}} }""", Web)!;

        request.LineOfBusiness.Should().Be(LineOfBusiness.Medicaid);
    }
}
