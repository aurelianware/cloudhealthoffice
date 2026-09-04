using Cms0057Evidence;
using FluentAssertions;

namespace Cms0057Evidence.Tests;

public class ManifestLoaderTests
{
    private static string Temp(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"scn-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ValidManifest_Loads()
    {
        var path = Temp("""
        { "schemaVersion": 1, "scenarios": [
          { "id": "PAS-03", "name": "submit", "capability": "PriorAuthorization",
            "replace": { "status": "PASSABLE" },
            "augment": { "qnxt": { "status": "GAP", "rationale": "x" } } } ] }
        """);
        var doc = ManifestLoader.Load(path);
        doc.SchemaVersion.Should().Be(1);
        doc.Scenarios.Should().ContainSingle().Which.Augment.Should().ContainKey("qnxt");
    }

    [Fact]
    public void MissingFile_Throws()
    {
        var act = () => ManifestLoader.Load(Path.Combine(Path.GetTempPath(), "does-not-exist.json"));
        act.Should().Throw<ManifestException>().WithMessage("*not found*");
    }

    [Fact]
    public void MalformedJson_Throws()
    {
        var act = () => ManifestLoader.Load(Temp("{ not json "));
        act.Should().Throw<ManifestException>().WithMessage("*malformed*");
    }

    [Fact]
    public void DuplicateId_Throws()
    {
        var path = Temp("""
        { "schemaVersion": 1, "scenarios": [
          { "id": "PAS-03", "name": "a", "capability": "c", "replace": { "status": "PASSABLE" } },
          { "id": "PAS-03", "name": "b", "capability": "c", "replace": { "status": "GAP" } } ] }
        """);
        var act = () => ManifestLoader.Load(path);
        act.Should().Throw<ManifestException>().WithMessage("*duplicate scenario id*PAS-03*");
    }

    [Fact]
    public void InvalidStatus_Throws()
    {
        var path = Temp("""
        { "schemaVersion": 1, "scenarios": [
          { "id": "PAS-03", "name": "a", "capability": "c", "replace": { "status": "MAYBE" } } ] }
        """);
        var act = () => ManifestLoader.Load(path);
        act.Should().Throw<ManifestException>().WithMessage("*MAYBE*not a valid status*");
    }

    [Fact]
    public void UnknownAugmentBackendKey_Throws()
    {
        var path = Temp("""
        { "schemaVersion": 1, "scenarios": [
          { "id": "PAS-03", "name": "a", "capability": "c", "replace": { "status": "PASSABLE" },
            "augment": { "mainframe": { "status": "GAP" } } } ] }
        """);
        var act = () => ManifestLoader.Load(path);
        act.Should().Throw<ManifestException>().WithMessage("*mainframe*not a known backend*");
    }

    [Fact]
    public void NonPositiveSchemaVersion_Throws()
    {
        var path = Temp("""
        { "schemaVersion": 0, "scenarios": [
          { "id": "PAS-03", "name": "a", "capability": "c", "replace": { "status": "PASSABLE" } } ] }
        """);
        var act = () => ManifestLoader.Load(path);
        act.Should().Throw<ManifestException>().WithMessage("*schemaVersion*");
    }
}

public class TrxParserTests
{
    private const string TrxTemplate = """
    <?xml version="1.0" encoding="UTF-8"?>
    <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
      <Results>
        <UnitTestResult testName="Ns.Cls.TestA" outcome="Passed" />
        <UnitTestResult testName="Ns.Cls.TestB" outcome="Failed" />
        <UnitTestResult testName="Ns.Cls.Theory(x: 1)" outcome="Passed" />
        <UnitTestResult testName="Ns.Cls.Skipped" outcome="NotExecuted" />
      </Results>
    </TestRun>
    """;

    private static string TempTrx(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"trx-{Guid.NewGuid():N}.trx");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Parse_CountsAndNormalizesTheoryNames()
    {
        var summary = TrxParser.Parse(TempTrx(TrxTemplate));
        summary.Passed.Should().Be(2);
        summary.Failed.Should().Be(1);
        summary.Skipped.Should().Be(1);
        summary.Total.Should().Be(4);
        summary.Results.Should().Contain(r => r.TestName == "Ns.Cls.Theory"); // theory args stripped
    }

    [Fact]
    public void Parse_MissingFile_Throws()
    {
        var act = () => TrxParser.Parse(Path.Combine(Path.GetTempPath(), "nope.trx"));
        act.Should().Throw<TrxException>().WithMessage("*not found*");
    }

    [Fact]
    public void Parse_NoResults_Throws()
    {
        var empty = """
        <?xml version="1.0" encoding="UTF-8"?>
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results/></TestRun>
        """;
        var act = () => TrxParser.Parse(TempTrx(empty));
        act.Should().Throw<TrxException>().WithMessage("*no UnitTestResult*");
    }
}
