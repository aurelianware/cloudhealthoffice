using EnrollmentImportService.Controllers;
using EnrollmentImportService.Services.Settings;
using EnrollmentImportService.Tests.Support;
using Microsoft.AspNetCore.Mvc;

namespace EnrollmentImportService.Tests.Controllers;

public class SettingsControllerTests
{
    private readonly InMemoryEnrollmentSettingsStore _store = new();

    private SettingsController Controller(string tenant = "t1") =>
        new(_store, new EnrollmentSettingsResolver(_store), new TestActor("admin", tenant), TimeProvider.System);

    private static Dictionary<string, int?> Grace(int? days) => new() { [EnrollmentSettingKeys.OrphanGraceDays] = days };

    [Fact]
    public async Task SettingALevel_ChangesWhatResolves_AndNullFallsThrough()
    {
        var ctl = Controller();

        (await ctl.Put("sponsor", "GRP1", Grace(3), default)).Should().BeOfType<OkObjectResult>();
        (await ctl.Put("lineOfBusiness", "commercial", Grace(10), default)).Should().BeOfType<OkObjectResult>();

        var resolved = (ResolvedEnrollmentSetting)((OkObjectResult)await ctl.Resolve(EnrollmentSettingKeys.OrphanGraceDays, "1", null, "GRP1", default)).Value!;
        resolved.Should().Be(new ResolvedEnrollmentSetting(EnrollmentSettingKeys.OrphanGraceDays, 3, "Sponsor", "GRP1"));

        (await ctl.Put("sponsor", "GRP1", Grace(null), default)).Should().BeOfType<NoContentResult>("the level has nothing left");
        resolved = (ResolvedEnrollmentSetting)((OkObjectResult)await ctl.Resolve(EnrollmentSettingKeys.OrphanGraceDays, "Commercial", null, "GRP1", default)).Value!;
        resolved.Value.Should().Be(10);
        resolved.Source.Should().Be("LineOfBusiness");

        (await Controller("t2").Resolve(EnrollmentSettingKeys.OrphanGraceDays, "Commercial", null, "GRP1", default))
            .Should().BeOfType<OkObjectResult>().Which.Value.Should().Be(
                new ResolvedEnrollmentSetting(EnrollmentSettingKeys.OrphanGraceDays, 7, "Default", "Commercial"), "another tenant sees only defaults");
    }

    [Theory]
    [InlineData("region", "X", 3)]                 // unknown level
    [InlineData("lineOfBusiness", "Dental", 3)]    // unknown line of business
    [InlineData("sponsor", "GRP1", 91)]            // out of range
    [InlineData("sponsor", "GRP1", -1)]
    public async Task BadInput_Is422(string level, string key, int days) =>
        (await Controller().Put(level, key, Grace(days), default)).Should().BeOfType<UnprocessableEntityObjectResult>();

    [Fact]
    public async Task UnknownSettings_Are422() =>
        (await Controller().Put("sponsor", "GRP1", new Dictionary<string, int?> { ["retroDays"] = 3 }, default))
            .Should().BeOfType<UnprocessableEntityObjectResult>();
}
