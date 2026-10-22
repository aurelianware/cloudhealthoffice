using System.Text.Json;
using CloudHealthOffice.GrouperEngine.Data;
using CloudHealthOffice.GrouperEngine.Domain;
using CloudHealthOffice.GrouperEngine.Mask;
using Xunit;

namespace CloudHealthOffice.GrouperEngine.Tests;

/// <summary>
/// The importer that reads a CMS release's code tables.
///
/// <para>
/// The fixtures here are invented, not extracted. They carry CMS's file
/// <i>shape</i> — which is the thing worth pinning, since the shape has three
/// traps in it — and a handful of made-up codes, so that none of CMS's table
/// data is committed. Reading a real release is the gated test at the bottom.
/// </para>
/// </summary>
public class CmsJsonTableImporterTests : IDisposable
{
    private const string Version = "V440";

    private readonly string directory = Directory.CreateTempSubdirectory(
        "cho-cms-tables-").FullName;

    public void Dispose()
    {
        Directory.Delete(directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private void Write(string file, string json) =>
        File.WriteAllText(Path.Combine(directory, file), json);

    /// <summary>
    /// A minimal but complete release: two diagnoses, one procedure, and the
    /// pattern and sex tables they point at.
    /// </summary>
    private void WriteCompleteRelease()
    {
        Write("dxPatterns.json", """
            {"dxPatterns": {
              "1": {"operands": ["stcc", "strauma"]},
              "2": {"operands": ["dxc1402"]},
              "3": {"operands": ["HAC14"]}
            }}
            """);

        // Both pattern files wrap their contents in a member called
        // "dxPatterns". That is CMS's spelling in prPatterns.json too.
        Write("prPatterns.json", """
            {"dxPatterns": {
              "7": {"operands": ["d477", "lapint"]}
            }}
            """);

        Write("diagnoses.json", """
            {"dxMap": {
              "AA000": {"list": [
                {"mdc": 4, "severity": "MCC", "operandsPatternNum": 1,
                 "hacOperandsPatternNum": 3, "versions": ["V440"]}
              ]},
              "AA001": {"list": [
                {"mdc": 11, "severity": "CC", "dxCatListPatternNum": 2,
                 "versions": ["V430", "V440"]}
              ]},
              "AA002": {"list": [
                {"mdc": 0, "severity": "NCC", "versions": ["V430"]}
              ]}
            }}
            """);

        Write("procedures.json", """
            {"codes": {
              "0AA000A": {"list": [
                {"operandsPatternNum": 7, "versions": ["V440"]}
              ]}
            }}
            """);

        Write("codeGenderMdcs.json", """
            {"CodeGenderMdcs": {
              "AA001": {"list": [
                {"genderMdcMap": {"1": 12, "2": 13}, "versions": ["V440"]}
              ]}
            }}
            """);
    }

    [Fact]
    public void A_release_imports_its_codes_for_the_version_asked_for()
    {
        WriteCompleteRelease();

        var tables = CmsJsonTableImporter.Import(directory, Version);

        Assert.Equal(Version, tables.Version);

        Assert.True(tables.TryGetDiagnosis("AA000", out var mcc));
        Assert.Equal(4, mcc.Mdc);
        Assert.Equal(ComplicationLevel.Mcc, mcc.Severity);
        Assert.Equal(["stcc", "strauma"], mcc.Operands);
        Assert.Equal(["HAC14"], mcc.HacOperands);
        Assert.Empty(mcc.DiagnosisCategoryOperands);
        Assert.Null(mcc.SexMdcs);

        Assert.True(tables.TryGetProcedure("0AA000A", out var procedure));
        Assert.Equal(["d477", "lapint"], procedure.Operands);
    }

    /// <summary>
    /// A code with no row for this version is not in this version's tables. New
    /// codes and retired ones both look like this, so it is a miss rather than
    /// an error — and the caller already has to handle a missing code, because
    /// an unknown code on a claim has to make the grouper refuse.
    /// </summary>
    [Fact]
    public void A_code_from_another_version_is_simply_absent()
    {
        WriteCompleteRelease();

        var tables = CmsJsonTableImporter.Import(directory, Version);

        Assert.False(tables.TryGetDiagnosis("AA002", out _));
        Assert.True(CmsJsonTableImporter
            .Import(directory, "V430")
            .TryGetDiagnosis("AA002", out _));
    }

    [Fact]
    public void Codes_are_matched_regardless_of_case()
    {
        WriteCompleteRelease();

        var tables = CmsJsonTableImporter.Import(directory, Version);

        Assert.True(tables.TryGetDiagnosis("aa000", out _));
        Assert.True(tables.TryGetProcedure("0aa000a", out _));
    }

    [Fact]
    public void A_sex_specific_code_carries_its_mdc_per_sex()
    {
        WriteCompleteRelease();

        var tables = CmsJsonTableImporter.Import(directory, Version);

        Assert.True(tables.TryGetDiagnosis("AA001", out var code));
        Assert.NotNull(code.SexMdcs);
        Assert.Equal(12, code.SexMdcs[1]);
        Assert.Equal(13, code.SexMdcs[2]);
    }

    /// <summary>
    /// Two rows for one code and one version is the release contradicting
    /// itself. Taking the first would be a wrong answer the grouper could never
    /// explain, so the import refuses.
    /// </summary>
    [Fact]
    public void A_code_with_two_rows_for_one_version_is_refused()
    {
        WriteCompleteRelease();
        Write("diagnoses.json", """
            {"dxMap": {
              "AA000": {"list": [
                {"mdc": 4, "severity": "MCC", "versions": ["V440"]},
                {"mdc": 5, "severity": "CC", "versions": ["V440"]}
              ]}
            }}
            """);

        var error = Assert.Throws<MsDrgTableImportException>(
            () => CmsJsonTableImporter.Import(directory, Version));

        Assert.Contains("more than one row", error.Message);
        Assert.Contains("AA000", error.Message);
    }

    /// <summary>
    /// A version the release does not carry imports as nothing, and nothing is
    /// the one outcome that must not be allowed to look like success: every
    /// code would be unknown and every stay ungroupable, for a reason nobody
    /// would think to look for in the table files.
    /// </summary>
    [Fact]
    public void A_version_the_release_does_not_carry_is_refused()
    {
        WriteCompleteRelease();

        var error = Assert.Throws<MsDrgTableImportException>(
            () => CmsJsonTableImporter.Import(directory, "V999"));

        Assert.Contains("no diagnosis rows", error.Message);
        Assert.Contains("V999", error.Message);
    }

    [Fact]
    public void A_dangling_pattern_number_is_refused()
    {
        WriteCompleteRelease();
        Write("diagnoses.json", """
            {"dxMap": {
              "AA000": {"list": [
                {"mdc": 4, "severity": "MCC", "operandsPatternNum": 4242,
                 "versions": ["V440"]}
              ]}
            }}
            """);

        var error = Assert.Throws<MsDrgTableImportException>(
            () => CmsJsonTableImporter.Import(directory, Version));

        Assert.Contains("4242", error.Message);
        Assert.Contains("different releases", error.Message);
    }

    [Fact]
    public void A_severity_that_is_not_one_of_cms_three_is_refused()
    {
        WriteCompleteRelease();
        Write("diagnoses.json", """
            {"dxMap": {
              "AA000": {"list": [
                {"mdc": 4, "severity": "SEVERE", "versions": ["V440"]}
              ]}
            }}
            """);

        var error = Assert.Throws<MsDrgTableImportException>(
            () => CmsJsonTableImporter.Import(directory, Version));

        Assert.Contains("SEVERE", error.Message);
    }

    [Fact]
    public void A_missing_file_names_itself()
    {
        WriteCompleteRelease();
        File.Delete(Path.Combine(directory, "procedures.json"));

        var error = Assert.Throws<MsDrgTableImportException>(
            () => CmsJsonTableImporter.Import(directory, Version));

        Assert.Contains("procedures.json", error.Message);
    }

    [Fact]
    public void A_file_of_the_wrong_shape_says_so()
    {
        WriteCompleteRelease();
        Write("diagnoses.json", """{"somethingElse": {}}""");

        var error = Assert.Throws<MsDrgTableImportException>(
            () => CmsJsonTableImporter.Import(directory, Version));

        Assert.Contains("'dxMap'", error.Message);
    }

    [Fact]
    public void A_directory_that_is_not_there_says_so()
    {
        var error = Assert.Throws<MsDrgTableImportException>(
            () => CmsJsonTableImporter.Import(
                Path.Combine(directory, "nope"), Version));

        Assert.Contains("No CMS table directory", error.Message);
    }

    [Fact]
    public void Unreadable_json_says_which_file()
    {
        WriteCompleteRelease();
        Write("dxPatterns.json", "{ this is not json");

        var error = Assert.Throws<MsDrgTableImportException>(
            () => CmsJsonTableImporter.Import(directory, Version));

        Assert.Contains("dxPatterns.json", error.Message);
        Assert.IsAssignableFrom<JsonException>(error.InnerException);
    }

    /// <summary>
    /// Reads a real CMS release, when the operator has pointed at one.
    ///
    /// <para>
    /// Skipped unless <c>CMS_MSDRG_TABLES</c> names a directory, which is the
    /// same gate the parity oracle uses: CI has no CMS release and must not
    /// need one. What it checks is only what no fixture can — that the shapes
    /// this importer reads are the shapes a real release actually has, at real
    /// scale.
    /// </para>
    /// </summary>
    [SkippableFact]
    public void A_real_cms_release_imports()
    {
        var tables = Environment.GetEnvironmentVariable("CMS_MSDRG_TABLES");
        Skip.If(
            string.IsNullOrWhiteSpace(tables),
            "Set CMS_MSDRG_TABLES to a CMS MS-DRG release's table directory to run this.");

        var version = Environment.GetEnvironmentVariable("CMS_MSDRG_VERSION") ?? "V440";
        var imported = CmsJsonTableImporter.Import(tables!, version);

        // A real release is tens of thousands of codes. A handful would mean the
        // version filter matched almost nothing, which is the failure this is
        // here to catch.
        Assert.True(
            imported.DiagnosisCount > 50_000,
            $"Only {imported.DiagnosisCount} diagnosis codes imported for {version}.");
        Assert.True(
            imported.ProcedureCount > 50_000,
            $"Only {imported.ProcedureCount} procedure codes imported for {version}.");
    }

    /// <summary>
    /// A real release's DRG logic table imports, every formula in it parses,
    /// and the one tie the ranking contains is harmless.
    ///
    /// <para>
    /// Skipped unless <c>CMS_MSDRG_TABLES</c> names a directory. Parsing is
    /// the point: 824 formulas is a large enough sample that a lexer or
    /// parser gap shows up here rather than on a claim, and the import refuses
    /// a formula it cannot parse rather than skipping the row.
    /// </para>
    /// </summary>
    [SkippableFact]
    public void A_real_releases_drg_logic_table_imports_and_every_formula_parses()
    {
        var tables = Environment.GetEnvironmentVariable("CMS_MSDRG_TABLES");
        Skip.If(
            string.IsNullOrWhiteSpace(tables),
            "Set CMS_MSDRG_TABLES to a CMS MS-DRG release's table directory to run this.");

        var version = Environment.GetEnvironmentVariable("CMS_MSDRG_VERSION") ?? "V440";
        var formulas = CmsJsonTableImporter.ImportFormulas(tables!, version);
        var codes = CmsJsonTableImporter.Import(tables!, version);

        Assert.Equal(version, formulas.Version);

        // Every MDC CMS numbers, plus 0 (pre-MDC), 29 and 99.
        Assert.True(
            formulas.Mdcs.Count > 20,
            $"Only {formulas.Mdcs.Count} MDCs have a ranking for {version}.");
        Assert.True(
            formulas.RowCount > 500,
            $"Only {formulas.RowCount} rows imported for {version}.");

        // Each MDC's rows come back in rank order.
        foreach (var mdc in formulas.Mdcs)
        {
            var ranks = formulas.ForMdc(mdc).Select(row => row.Rank).ToList();
            Assert.Equal(ranks.Order(), ranks);
        }

        // The rows that assign no DRG are CMS's MDC-assignment rows and MCE
        // edits. In several MDCs one of them shares a rank with a real DRG
        // row, which is only harmless because no code carries an MDCnn
        // operand -- so the row cannot match while a DRG is being assigned.
        var mdcOperands = formulas.Mdcs
            .SelectMany(formulas.ForMdc)
            .Where(row => !row.AssignsDrg)
            .Select(row => row.FormulaText)
            .Where(text => text.StartsWith("MDC", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(mdcOperands);

        var carried = mdcOperands
            .Where(operand => codes.CarriesDiagnosisOperand(operand)
                || codes.CarriesProcedureOperand(operand))
            .ToList();

        Assert.Empty(carried);
    }

    /// <summary>
    /// A real release's CC exclusion lists import, and every group a principal
    /// diagnosis names is a group the release holds.
    ///
    /// <para>
    /// Skipped unless <c>CMS_MSDRG_TABLES</c> names a directory. The scale is
    /// the point: CC exclusions are the mechanism that stops a stay being paid
    /// twice for one clinical fact, so a version filter that matched almost
    /// nothing would quietly raise what a large share of stays is paid.
    /// </para>
    /// </summary>
    [SkippableFact]
    public void A_real_releases_cc_exclusion_lists_import()
    {
        var tables = Environment.GetEnvironmentVariable("CMS_MSDRG_TABLES");
        Skip.If(
            string.IsNullOrWhiteSpace(tables),
            "Set CMS_MSDRG_TABLES to a CMS MS-DRG release's table directory to run this.");

        var version = Environment.GetEnvironmentVariable("CMS_MSDRG_VERSION") ?? "V440";
        var exclusions = CmsJsonTableImporter.ImportExclusions(tables!, version);

        Assert.Equal(version, exclusions.Version);
        Assert.True(
            exclusions.PrincipalDiagnosisCount > 20_000,
            $"Only {exclusions.PrincipalDiagnosisCount} principal diagnoses have an "
                + $"exclusion group for {version}.");

        // A code CMS excludes against itself: tuberculous meningitis as a
        // secondary says nothing a tuberculous-meningitis principal did not.
        Assert.Contains("A170", exclusions.ExclusionsFor("A170"));
    }

    /// <summary>
    /// A real release's clusters import, and every cluster code they name is a
    /// code the procedure table knows.
    ///
    /// <para>
    /// That second part is the one worth checking. A cluster is only useful
    /// because its own code is looked up in the procedure table like any other
    /// procedure; a cluster whose code is absent from that table contributes
    /// nothing, and the stay would group as though the combination had never
    /// been recognised.
    /// </para>
    /// </summary>
    [SkippableFact]
    public void A_real_releases_clusters_import_and_resolve_to_procedure_codes()
    {
        var tables = Environment.GetEnvironmentVariable("CMS_MSDRG_TABLES");
        Skip.If(
            string.IsNullOrWhiteSpace(tables),
            "Set CMS_MSDRG_TABLES to a CMS MS-DRG release's table directory to run this.");

        var version = Environment.GetEnvironmentVariable("CMS_MSDRG_VERSION") ?? "V440";
        var codes = CmsJsonTableImporter.Import(tables!, version);
        var clusters = CmsJsonTableImporter.ImportClusters(tables!, version);

        // Only a small fraction of procedure codes take part in a cluster, so
        // the index should be small -- and not empty.
        Assert.InRange(clusters.IndexedCodeCount, 1, 5_000);

        var unknown = clusters
            .ClustersMentioning(clusters.AllIndexedCodes)
            .Select(cluster => cluster.ClusterCode)
            .Where(code => !codes.TryGetProcedure(code, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();

        Assert.Empty(unknown);
    }

    /// <summary>
    /// Every complication level in CHO's seed tables agrees with the CMS
    /// release's.
    ///
    /// <para>
    /// This is the test that would have caught two wrong levels in that file
    /// before the parity oracle did. The seed tables are a hand-written slice
    /// kept for the engine's own unit tests, and a hand-written slice drifts:
    /// J181 was recorded there as a CC when CMS makes it an MCC, and E1165 as
    /// a CC when CMS makes it neither. Both were found by running CMS's
    /// grouper, which is a slower and more roundabout way of asking a question
    /// the tables answer directly.
    /// </para>
    ///
    /// <para>
    /// Gated like the import above, because it needs a CMS release. It is the
    /// check an operator should run when they load a new one.
    /// </para>
    /// </summary>
    [SkippableFact]
    public void The_seed_tables_agree_with_a_real_cms_release()
    {
        var tables = Environment.GetEnvironmentVariable("CMS_MSDRG_TABLES");
        Skip.If(
            string.IsNullOrWhiteSpace(tables),
            "Set CMS_MSDRG_TABLES to a CMS MS-DRG release's table directory to run this.");

        var version = Environment.GetEnvironmentVariable("CMS_MSDRG_VERSION") ?? "V440";
        var cms = CmsJsonTableImporter.Import(tables!, version);
        var seed = MsDrgSeedTables.Build();

        var disagreements = new List<string>();

        foreach (var entry in seed.Complications)
        {
            var code = entry.Key;
            var level = entry.Value.Level;

            if (!cms.TryGetDiagnosis(code, out var fromCms))
            {
                disagreements.Add(
                    $"{code}: seed says {level}, {version} has no such code");
                continue;
            }

            if (fromCms.Severity != level)
            {
                disagreements.Add(
                    $"{code}: seed says {level}, {version} says {fromCms.Severity}");
            }
        }

        Assert.Empty(disagreements);
    }
}
