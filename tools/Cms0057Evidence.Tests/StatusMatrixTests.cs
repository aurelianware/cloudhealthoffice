using Cms0057Evidence;
using FluentAssertions;

namespace Cms0057Evidence.Tests;

public class StatusMatrixParserTests
{
    private const string Header = "| Scenario | Status | Matrix refs | Justification |";
    private const string Separator = "|---|---|---|---|";

    private static string Doc(params string[] tableLines) =>
        string.Join("\n", new[]
        {
            "# Matrix", "", "Some prose with | pipes | outside the markers.", "",
            "## 11. Evidence scenario status (generator input)", "",
            StatusMatrixParser.BeginMarker,
        }.Concat(tableLines).Concat(new[] { StatusMatrixParser.EndMarker, "", "Trailing prose." }));

    private static string ValidDoc(params string[] rows) =>
        Doc(new[] { Header, Separator }.Concat(rows).ToArray());

    private static StatusMatrix Parse(string md) => StatusMatrixParser.Parse(md, "matrix.md");

    private static void ShouldFail(string md, string pattern)
    {
        var act = () => Parse(md);
        act.Should().Throw<StatusMatrixException>().WithMessage(pattern);
    }

    [Fact]
    public void ValidTable_ParsesEveryRow()
    {
        var m = Parse(ValidDoc(
            "| PAS-01 | PARTIAL | §4.2, D-21 | CRD cards only. |",
            "| PAS-08 | PASSABLE | §4.4 | Drug exclusion is I+T. |",
            "| METRICS-01 | GAP | G-20 | No metric set. |",
            "| X-01 | N/A | — | Not product code. |"));

        m.Rows.Should().HaveCount(4);
        m.Rows["PAS-01"].Should().Be(new StatusMatrixRow("PAS-01", "PARTIAL", "§4.2, D-21", "CRD cards only.", 10));
        m.Rows["PAS-08"].Status.Should().Be(Status.Passable);
        m.Rows["METRICS-01"].Status.Should().Be(Status.Gap);
        m.Rows["X-01"].Status.Should().Be(Status.NotApplicable);
    }

    [Fact]
    public void CrLfLineEndingsAndBlankLinesInsideMarkers_AreAccepted()
    {
        var md = ValidDoc("", "| PAS-01 | GAP | D-01 | Not persisted. |", "").Replace("\n", "\r\n");
        Parse(md).Rows.Should().ContainKey("PAS-01");
    }

    [Fact]
    public void EscapedPipe_IsALiteralPipeInsideACell()
    {
        var m = Parse(ValidDoc(@"| PAS-01 | PARTIAL | D-21 | Uses a \| b semantics. |"));
        m.Rows["PAS-01"].Justification.Should().Be("Uses a | b semantics.");
    }

    [Fact]
    public void HeaderNames_AreCaseInsensitive()
    {
        var md = Doc("| scenario | STATUS | matrix REFS | justification |", Separator, "| PAS-01 | GAP | D-01 | x |");
        Parse(md).Rows.Should().ContainKey("PAS-01");
    }

    [Fact]
    public void MissingFile_Throws()
    {
        var act = () => StatusMatrixParser.Load(Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.md"));
        act.Should().Throw<StatusMatrixException>().WithMessage("*not found*");
    }

    [Fact]
    public void Load_ReadsFromDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"matrix-{Guid.NewGuid():N}.md");
        File.WriteAllText(path, ValidDoc("| PAS-01 | GAP | D-01 | x |"));
        StatusMatrixParser.Load(path).Rows.Should().ContainKey("PAS-01");
    }

    [Fact]
    public void MissingBeginMarker_Throws() =>
        ShouldFail("# Matrix\n" + Header + "\n" + Separator + "\n" + StatusMatrixParser.EndMarker, "*begin marker*not found*");

    [Fact]
    public void MissingEndMarker_Throws() =>
        ShouldFail(StatusMatrixParser.BeginMarker + "\n" + Header + "\n" + Separator, "*end marker*not found*");

    [Fact]
    public void DuplicateBeginMarker_Throws() =>
        ShouldFail(ValidDoc("| PAS-01 | GAP | D-01 | x |") + "\n" + StatusMatrixParser.BeginMarker, "*begin marker appears 2 times*");

    [Fact]
    public void DuplicateEndMarker_Throws() =>
        ShouldFail(ValidDoc("| PAS-01 | GAP | D-01 | x |") + "\n" + StatusMatrixParser.EndMarker, "*end marker appears 2 times*");

    [Fact]
    public void EndMarkerBeforeBegin_Throws() =>
        ShouldFail(StatusMatrixParser.EndMarker + "\n" + StatusMatrixParser.BeginMarker, "*end marker*before the begin marker*");

    [Fact]
    public void WrongHeader_Throws() =>
        ShouldFail(Doc("| Scenario | Status | Justification | Matrix refs |", Separator, "| PAS-01 | GAP | x | D-01 |"),
            "*header must be*");

    [Fact]
    public void MissingSeparator_Throws() =>
        ShouldFail(Doc(Header, "| PAS-01 | GAP | D-01 | x |"), "*expected a table separator row*");

    [Fact]
    public void HeaderOnly_Throws() =>
        ShouldFail(Doc(Header), "*no header and separator rows*");

    [Fact]
    public void NoRows_Throws() =>
        ShouldFail(ValidDoc(), "*no scenario rows*");

    [Fact]
    public void NonTableLineInsideMarkers_Throws() =>
        ShouldFail(ValidDoc("| PAS-01 | GAP | D-01 | x |", "A stray note."), "*matrix.md:*unexpected non-table line*A stray note.*");

    [Theory]
    [InlineData("\t| PAS-01 | GAP | D-01 | x |")]
    [InlineData("    | PAS-01 | GAP | D-01 | x |")]
    [InlineData("      | PAS-01 | GAP | D-01 | x |")]
    public void CodeBlockIndentedLine_Throws(string row) =>
        ShouldFail(ValidDoc(row), "*matrix.md:10: status table line is indented by a tab or 4+ spaces*");

    [Fact]
    public void ShallowIndentation_IsAccepted() =>
        Parse(ValidDoc("   | PAS-01 | GAP | D-01 | x |")).Rows.Should().ContainKey("PAS-01");

    [Fact]
    public void WrongCellCount_Throws() =>
        ShouldFail(ValidDoc("| PAS-01 | GAP | D-01 | has | an unescaped pipe |"), "*expected 4 cells, found 5*");

    [Fact]
    public void RowNotClosedWithPipe_Throws() =>
        ShouldFail(ValidDoc("| PAS-01 | GAP | D-01 | x"), "*must start and end with '|'*");

    [Theory]
    [InlineData("MAYBE")]
    [InlineData("passable")]   // case matters: the vocabulary is exact
    [InlineData("**PARTIAL**")] // no Markdown emphasis in the status cell
    [InlineData("")]
    public void UnknownStatus_Throws(string status) =>
        ShouldFail(ValidDoc($"| PAS-01 | {status} | D-01 | x |"), $"*PAS-01 status '{status}' is not a valid status*");

    [Theory]
    [InlineData("")]
    [InlineData("pas-01")]
    [InlineData("`PAS-01`")]
    [InlineData("PAS 01")]
    public void InvalidScenarioId_Throws(string id) =>
        ShouldFail(ValidDoc($"| {id} | GAP | D-01 | x |"), "*is not a valid scenario id*");

    [Fact]
    public void EmptyRefs_Throws() =>
        ShouldFail(ValidDoc("| PAS-01 | GAP |  | x |"), "*PAS-01 has no matrix refs*");

    [Fact]
    public void EmptyJustification_Throws() =>
        ShouldFail(ValidDoc("| PAS-01 | GAP | D-01 |  |"), "*PAS-01 has no justification*");

    [Fact]
    public void DuplicateRow_Throws_EvenWhenTheStatusesAgree() =>
        ShouldFail(ValidDoc("| PAS-01 | GAP | D-01 | x |", "| PAS-01 | GAP | D-01 | y |"),
            "*matrix.md:11: duplicate row for scenario PAS-01 (first at line 10)*");
}

public class StatusMatrixResolverTests
{
    private static StatusMatrix Matrix(params (string Id, string Status)[] rows) =>
        new("matrix.md", rows.ToDictionary(
            r => r.Id,
            r => new StatusMatrixRow(r.Id, r.Status, $"refs-{r.Id}", $"why-{r.Id}", 1),
            StringComparer.Ordinal));

    private static ManifestScenario Unresolved(string id, string? qnxt = null) => new()
    {
        Id = id,
        Name = $"{id} name",
        Capability = "PriorAuthorization",
        Augment = qnxt is null
            ? new Dictionary<string, ManifestBackend>()
            : new Dictionary<string, ManifestBackend> { ["qnxt"] = new() { Status = qnxt, Rationale = "engagement work" } },
    };

    [Fact]
    public void Apply_TakesReplaceStatusAndRationaleFromTheMatrix_AndKeepsTheRest()
    {
        var resolved = StatusMatrixResolver.Apply(
            TestData.Manifest(Unresolved("PAS-01", qnxt: "GAP"), Unresolved("PAS-08")),
            Matrix(("PAS-01", Status.Partial), ("PAS-08", Status.Passable)));

        var pas01 = resolved.Scenarios.Single(s => s.Id == "PAS-01");
        pas01.Replace!.Status.Should().Be(Status.Partial);
        pas01.Replace.Rationale.Should().Be("why-PAS-01 (matrix refs: refs-PAS-01)");
        pas01.Name.Should().Be("PAS-01 name");
        pas01.Capability.Should().Be("PriorAuthorization");
        pas01.Augment["qnxt"].Status.Should().Be(Status.Gap, "Augment status is still declared in the manifest");
        resolved.Scenarios.Single(s => s.Id == "PAS-08").Replace!.Status.Should().Be(Status.Passable);
        resolved.SchemaVersion.Should().Be(1);
    }

    [Fact]
    public void Apply_MatrixWins_OverAnyReplaceValueAlreadyOnTheModel()
    {
        // ManifestLoader rejects a manifest that declares 'replace'; this pins
        // the resolver too, so a programmatic value can never compete.
        var resolved = StatusMatrixResolver.Apply(
            TestData.Manifest(TestData.Scenario("PAS-06", "PASSABLE")),
            Matrix(("PAS-06", Status.Gap)));
        resolved.Scenarios.Single().Replace!.Status.Should().Be(Status.Gap);
    }

    [Fact]
    public void Apply_ManifestScenarioWithoutARow_Throws()
    {
        var act = () => StatusMatrixResolver.Apply(
            TestData.Manifest(Unresolved("PAS-01"), Unresolved("PAS-02"), Unresolved("PAS-03")),
            Matrix(("PAS-02", Status.Gap)));
        act.Should().Throw<StatusMatrixException>().WithMessage("*no status row for manifest scenario(s): PAS-01, PAS-03*");
    }

    [Fact]
    public void Apply_RowForUnknownScenario_Throws()
    {
        var act = () => StatusMatrixResolver.Apply(
            TestData.Manifest(Unresolved("PAS-01")),
            Matrix(("PAS-01", Status.Gap), ("PAS-99", Status.Passable)));
        act.Should().Throw<StatusMatrixException>().WithMessage("*not in the manifest: PAS-99*");
    }
}

/// <summary>Precedence and reconciliation once the matrix is applied.</summary>
public class StatusMatrixReconciliationTests
{
    private static ManifestDocument Resolve(string id, string status) =>
        StatusMatrixResolver.Apply(
            TestData.Manifest(new ManifestScenario { Id = id, Name = id, Capability = "c" }),
            new StatusMatrix("matrix.md", new Dictionary<string, StatusMatrixRow>
            {
                [id] = new(id, status, "D-01", "why", 1),
            }));

    [Fact]
    public void MatrixPassable_WithoutAPassingTaggedTest_FailsReconciliation()
    {
        var traits = new[] { new ScenarioTrait("T.A", "PAS-03", ScenarioTrait.ReplaceBackend, IsGap: false) };
        var act = () => EvidenceBuilder.Build(Resolve("PAS-03", Status.Passable), traits,
            TestData.Trx(("T.A", TestOutcome.Failed)), TestData.Identity());
        act.Should().Throw<EvidenceReconciliationException>().WithMessage("*PAS-03*PASSABLE*no passing non-GAP test*");
    }

    [Fact]
    public void MatrixPassable_BackedOnlyByAGapTest_FailsReconciliation()
    {
        var traits = new[] { new ScenarioTrait("T.A", "PAS-03", ScenarioTrait.ReplaceBackend, IsGap: true) };
        var act = () => EvidenceBuilder.Build(Resolve("PAS-03", Status.Passable), traits,
            TestData.Trx(("T.A", TestOutcome.Passed)), TestData.Identity());
        act.Should().Throw<EvidenceReconciliationException>();
    }

    [Fact]
    public void MatrixPassable_WithAPassingTaggedTest_IsPassable()
    {
        var traits = new[] { new ScenarioTrait("T.A", "PAS-03", ScenarioTrait.ReplaceBackend, IsGap: false) };
        var report = EvidenceBuilder.Build(Resolve("PAS-03", Status.Passable), traits,
            TestData.Trx(("T.A", TestOutcome.Passed)), TestData.Identity());
        report.Scenarios.Single().Backends.Single(b => b.Backend == BackendIds.Replace)
            .DeclaredStatus.Should().Be(Status.Passable);
    }

    [Fact]
    public void MatrixGap_IsNotUpgradedByAPassingTest()
    {
        var traits = new[] { new ScenarioTrait("T.A", "PAS-06", ScenarioTrait.ReplaceBackend, IsGap: false) };
        var report = EvidenceBuilder.Build(Resolve("PAS-06", Status.Gap), traits,
            TestData.Trx(("T.A", TestOutcome.Passed)), TestData.Identity());

        var replace = report.Scenarios.Single().Backends.Single(b => b.Backend == BackendIds.Replace);
        replace.DeclaredStatus.Should().Be(Status.Gap);
        replace.TestExecutionStatus.Should().Be(ExecutionStatus.Passed);
        replace.Rationale.Should().Be("why (matrix refs: D-01)");
        report.KnownGaps.Should().ContainSingle(g => g.ScenarioId == "PAS-06" && g.Backend == BackendIds.Replace);
    }

    [Fact]
    public void UnresolvedManifest_IsRefusedByTheBuilder()
    {
        var unresolved = TestData.Manifest(new ManifestScenario { Id = "PAS-03", Name = "n", Capability = "c" });
        var traits = new[] { new ScenarioTrait("T.A", "PAS-03", ScenarioTrait.ReplaceBackend, IsGap: false) };
        var act = () => EvidenceBuilder.Build(unresolved, traits, TestData.Trx(("T.A", TestOutcome.Passed)), TestData.Identity());
        act.Should().Throw<EvidenceReconciliationException>().WithMessage("*PAS-03 has no Replace status*");
    }
}

/// <summary>
/// Drift guard over the committed files: docs/epa-gap-matrix.md §11 and
/// tests/Cms0057Acceptance.Tests/scenarios.json must resolve cleanly.
/// </summary>
public class CommittedStatusMatrixTests
{
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, StatusMatrixParser.DefaultPath))
                && Directory.Exists(Path.Combine(dir.FullName, "tests", "Cms0057Acceptance.Tests")))
                return dir.FullName;
        throw new InvalidOperationException("Repository root (containing docs/epa-gap-matrix.md) not found.");
    }

    [Fact]
    public void CommittedMatrix_CoversExactlyTheCommittedManifest()
    {
        var root = RepoRoot();
        var manifest = ManifestLoader.Load(Path.Combine(root, "tests", "Cms0057Acceptance.Tests", "scenarios.json"));
        var matrix = StatusMatrixParser.Load(Path.Combine(root, StatusMatrixParser.DefaultPath));

        var resolved = StatusMatrixResolver.Apply(manifest, matrix);

        resolved.Scenarios.Should().OnlyContain(s => s.Replace != null && Status.All.Contains(s.Replace.Status));
        matrix.Rows.Keys.Should().BeEquivalentTo(manifest.Scenarios.Select(s => s.Id));
    }

    [Fact]
    public void CommittedMatrix_TotalsLine_MatchesTheTable()
    {
        var path = Path.Combine(RepoRoot(), StatusMatrixParser.DefaultPath);
        var matrix = StatusMatrixParser.Load(path);
        var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(path),
            @"\*\*Totals:\*\* (\d+) PASSABLE, (\d+) PARTIAL, (\d+) GAP, (\d+) N/A \((\d+) scenarios\)");
        m.Success.Should().BeTrue("§11 carries a totals line under the status table");

        int Count(string status) => matrix.Rows.Values.Count(r => r.Status == status);
        int.Parse(m.Groups[1].Value).Should().Be(Count(Status.Passable));
        int.Parse(m.Groups[2].Value).Should().Be(Count(Status.Partial));
        int.Parse(m.Groups[3].Value).Should().Be(Count(Status.Gap));
        int.Parse(m.Groups[4].Value).Should().Be(Count(Status.NotApplicable));
        int.Parse(m.Groups[5].Value).Should().Be(matrix.Rows.Count);
    }
}
