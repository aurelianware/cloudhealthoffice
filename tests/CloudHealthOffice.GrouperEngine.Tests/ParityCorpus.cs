using System.Globalization;
using CloudHealthOffice.GrouperEngine.Models;
using Xunit;

namespace CloudHealthOffice.GrouperEngine.Tests;

/// <summary>
/// Reads the CMS parity corpus and the answers CMS's grouper gave for it.
///
/// <para>
/// Both files live in <c>tools/cms-grouper-parity-oracle/corpus/</c>, outside
/// this project, because they belong to the oracle that produces them rather
/// than to the tests that consume them. They are pipe-separated, with
/// <c>#</c> comments and a header row.
/// </para>
/// </summary>
internal static class ParityCorpus
{
    /// <summary>One invented stay from the corpus.</summary>
    internal sealed record Claim(
        string Id,
        string PrincipalDiagnosis,
        IReadOnlyList<(string Code, string Poa)> SecondaryDiagnoses,
        IReadOnlyList<string> Procedures,
        int AgeYears,
        string Sex,
        string DischargeStatus,
        string Why)
    {
        /// <summary>
        /// Translates the corpus row into a CHO grouping request.
        ///
        /// <para>
        /// The discharge date is the one thing the corpus does not carry: the
        /// CMS jars are version-pinned rather than date-driven, so the oracle
        /// never needed one. CHO selects its table set by discharge date, so a
        /// date inside the seed slice's window is supplied here. That is a
        /// property of how the two groupers are configured, not a difference in
        /// what they were asked.
        /// </para>
        /// </summary>
        internal MsDrgGroupingRequest ToRequest() => new()
        {
            PrincipalDiagnosis = new GroupingDiagnosis(PrincipalDiagnosis, "Y"),
            SecondaryDiagnoses = SecondaryDiagnoses
                .Select(d => new GroupingDiagnosis(d.Code, TranslatePoa(d.Poa)))
                .ToList(),
            Procedures = Procedures.Select(p => new GroupingProcedure(p, null)).ToList(),
            DischargeDate = DischargeDateInSeedWindow,
            AgeYears = AgeYears,
            Sex = Sex switch { "MALE" => "M", "FEMALE" => "F", _ => "U" },
            DischargeStatus = DischargeStatus switch
            {
                "HOME_SELFCARE_ROUTINE" => "01",
                "SHORT_TERM_HOSPITAL" => "02",
                "DIED" => "20",
                "HOSPICE_HOME" => "50",
                _ => "01",
            },
            LengthOfStayDays = 4,
        };

        /// <summary>
        /// CMS names present-on-admission values after the GFC enum
        /// (<c>Y N U W E ONE BLANK INVALID</c>); CHO carries the UB-04
        /// character. The two agree on the four that matter.
        /// </summary>
        private static string? TranslatePoa(string poa) => poa switch
        {
            "Y" => "Y",
            "N" => "N",
            "U" => "U",
            "W" => "W",
            "ONE" => "1",
            "E" => "E",
            _ => null,
        };
    }

    /// <summary>One stay, with the DRG CMS assigned to it.</summary>
    internal sealed record ExpectedResult(
        Claim Claim,
        string Drg,
        string Mdc,
        string BaseDrg,
        string Severity,
        string MedSurg,
        string ReturnCode,
        string HacStatus,
        string SecondaryDiagnosisUsage);

    /// <summary>A discharge date inside the seed slice's FY 2026 window.</summary>
    private static readonly DateOnly DischargeDateInSeedWindow = new(2026, 3, 15);

    /// <summary>
    /// Loads the corpus joined to the oracle's answers, skipping the one row
    /// CMS rejected outright — a claim with no DRG is not a parity case, and
    /// CHO's handling of unknown codes is asserted directly in
    /// <see cref="TableDrivenMsDrgGrouperTests"/>.
    /// </summary>
    internal static IReadOnlyList<ExpectedResult> Load()
    {
        var directory = CorpusDirectory();
        var claims = ReadClaims(Path.Combine(directory, "reference-claims.psv"));
        var results = new List<ExpectedResult>();

        foreach (var fields in ReadRows(Path.Combine(directory, "expected-v44.psv")))
        {
            Assert.True(fields.Length >= 11, $"expected-v44.psv row is short: {fields[0]}");

            var id = fields[0];
            Assert.True(claims.ContainsKey(id), $"expected-v44.psv has no corpus row for {id}");

            if (fields[6] != "OK")
            {
                continue;
            }

            results.Add(new ExpectedResult(
                claims[id], fields[1], fields[2], fields[3], fields[4], fields[5],
                fields[6], fields[7], fields[10]));
        }

        Assert.NotEmpty(results);
        return results;
    }

    private static Dictionary<string, Claim> ReadClaims(string path)
    {
        var claims = new Dictionary<string, Claim>(StringComparer.Ordinal);

        foreach (var fields in ReadRows(path))
        {
            Assert.True(fields.Length >= 8, $"reference-claims.psv row is short: {fields[0]}");

            claims[fields[0]] = new Claim(
                fields[0],
                fields[1],
                ParsePoaList(fields[2]),
                SplitList(fields[3]),
                int.Parse(fields[4], CultureInfo.InvariantCulture),
                fields[5],
                fields[6],
                fields[7]);
        }

        return claims;
    }

    private static IEnumerable<string[]> ReadRows(string path)
    {
        Assert.True(File.Exists(path), $"parity corpus file is missing: {path}");

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("id|", StringComparison.Ordinal))
            {
                continue;
            }

            yield return line.Split('|').Select(f => f.Trim()).ToArray();
        }
    }

    private static List<(string Code, string Poa)> ParsePoaList(string field) =>
        SplitList(field)
            .Select(token => token.Split(':', 2))
            .Select(parts => (parts[0].Trim(), parts.Length > 1 ? parts[1].Trim() : "Y"))
            .ToList();

    private static List<string> SplitList(string field) =>
        field.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    /// <summary>
    /// Walks up from the test assembly to the repository root and down into the
    /// oracle's corpus directory. Copying the files into the test project would
    /// mean two copies that can drift; the oracle owns them.
    /// </summary>
    private static string CorpusDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName, "tools", "cms-grouper-parity-oracle", "corpus");

            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "could not find tools/cms-grouper-parity-oracle/corpus above " +
            AppContext.BaseDirectory);
    }
}
