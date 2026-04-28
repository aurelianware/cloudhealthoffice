using BenefitPlanService.Models;
using BenefitPlanService.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BenefitPlanService.Tests.Services;

/// <summary>
/// Capability BP 5.7 — file-backed ACA OOP limits loader. Verifies the
/// provider validates the file at startup so a malformed deploy fails
/// fast rather than serve adjudications without enforcement.
/// </summary>
public sealed class AcaLimitsProviderTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    private IAcaLimitsProvider BuildProvider(string fileContent)
    {
        var dir = Directory.CreateTempSubdirectory("aca-limits-test").FullName;
        _tempDirs.Add(dir);
        var path = Path.Combine(dir, "limits.json");
        File.WriteAllText(path, fileContent);

        var options = Options.Create(new AcaOopLimitsOptions { LimitsFilePath = path });
        return new AcaLimitsProvider(
            options,
            new StubHost(dir),
            NullLogger<AcaLimitsProvider>.Instance);
    }

    [Fact]
    public void GetForPlanYear_Returns_Configured_Row()
    {
        var json = """
        {
          "limits": [
            { "planYear": 2025, "individualCap": 9200, "familyCap": 18400 },
            { "planYear": 2026, "individualCap": 10600, "familyCap": 21200 }
          ]
        }
        """;
        var provider = BuildProvider(json);

        var row = provider.GetForPlanYear(2025);

        row.Should().NotBeNull();
        row!.IndividualCap.Should().Be(9_200m);
        row.FamilyCap.Should().Be(18_400m);
    }

    [Fact]
    public void GetForPlanYear_Returns_Null_For_Unknown_Year()
    {
        var json = """{ "limits": [ { "planYear": 2025, "individualCap": 9200, "familyCap": 18400 } ] }""";
        var provider = BuildProvider(json);

        provider.GetForPlanYear(2099).Should().BeNull();
    }

    [Fact]
    public void ConfiguredPlanYears_Lists_All_Loaded_Years()
    {
        var json = """
        {
          "limits": [
            { "planYear": 2024, "individualCap": 9450, "familyCap": 18900 },
            { "planYear": 2025, "individualCap": 9200, "familyCap": 18400 }
          ]
        }
        """;
        var provider = BuildProvider(json);

        provider.ConfiguredPlanYears.Should().BeEquivalentTo(new[] { 2024, 2025 });
    }

    [Fact]
    public void Construction_Throws_When_File_Missing()
    {
        var options = Options.Create(new AcaOopLimitsOptions
        {
            LimitsFilePath = Path.Combine(Path.GetTempPath(), "nonexistent-aca-file.json"),
        });

        var act = () => new AcaLimitsProvider(
            options,
            new StubHost(Path.GetTempPath()),
            NullLogger<AcaLimitsProvider>.Instance);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ACA OOP limits file not found*");
    }

    [Fact]
    public void Construction_Throws_When_File_Is_Malformed_Json()
    {
        var act = () => BuildProvider("not json");
        act.Should().Throw<InvalidOperationException>().WithMessage("*malformed*");
    }

    [Fact]
    public void Construction_Throws_When_FamilyCap_Less_Than_IndividualCap()
    {
        var json = """{ "limits": [ { "planYear": 2025, "individualCap": 9200, "familyCap": 5000 } ] }""";
        var act = () => BuildProvider(json);
        act.Should().Throw<InvalidOperationException>().WithMessage("*familyCap*less than*");
    }

    private sealed class StubHost : IHostEnvironment
    {
        public StubHost(string root) { ContentRootPath = root; }
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "BenefitPlanService.Tests";
        public string ContentRootPath { get; set; }
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
