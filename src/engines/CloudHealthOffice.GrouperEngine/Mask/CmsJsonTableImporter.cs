using System.Collections.Frozen;
using System.Text.Json;
using CloudHealthOffice.GrouperEngine.Domain;

using CloudHealthOffice.GrouperEngine.Severity;

namespace CloudHealthOffice.GrouperEngine.Mask;

/// <summary>
/// Imports a CMS MS-DRG release's code tables from the JSON a release carries,
/// for one grouper version.
///
/// <para>
/// This exists because the tables cannot be committed. They are CMS table
/// data, distributed per requester, and the release package carries American
/// Hospital Association copyright in the UB-04 code descriptions. So an
/// operator points this at a release they hold a licence for and the engine
/// reads it at startup; nothing CMS-derived is vendored into the repository,
/// and there is no fallback that quietly groups without it.
/// </para>
///
/// <para>
/// <b>One release holds several versions.</b> Every row carries the list of
/// versions it applies to, and a code's history is a list of rows: the same
/// code can be a CC in one version and an MCC in the next. So a version is
/// chosen at import, every row is filtered to it, and a row set that offers
/// two answers for one code and one version is a contradiction the import
/// refuses rather than picks from.
/// </para>
/// </summary>
public static class CmsJsonTableImporter
{
    private const string DiagnosisFile = "diagnoses.json";
    private const string ProcedureFile = "procedures.json";
    private const string DiagnosisPatternFile = "dxPatterns.json";
    private const string ProcedurePatternFile = "prPatterns.json";
    private const string SexMdcFile = "codeGenderMdcs.json";
    private const string ClusterFile = "clusters.json";
    private const string PrincipalExclusionFile = "pdxExclusionGroups.json";
    private const string ExclusionGroupFile = "exclusionGroups.json";

    /// <summary>
    /// Imports the CC exclusion lists in <paramref name="directory"/> for
    /// <paramref name="version"/>.
    ///
    /// <para>
    /// Two files: one mapping each principal diagnosis to a group id for a
    /// version, and one holding the groups. The groups are shared heavily —
    /// V44 points 26,168 principal diagnoses at 3,013 groups, the largest of
    /// which lists 5,565 codes — so each group is read once and the index
    /// holds references to it.
    /// </para>
    ///
    /// <para>
    /// A principal diagnosis naming a group the group file does not hold is
    /// refused rather than skipped. Skipping it would silently stop excluding
    /// anything for that principal, and the consequence of under-excluding is
    /// a higher-paying DRG, not an error.
    /// </para>
    /// </summary>
    public static InMemoryExclusionSource ImportExclusions(string directory, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        using var groupDocument = ReadDocument(directory, ExclusionGroupFile);

        var groups = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

        foreach (var group in
            Member(groupDocument.RootElement, "exclusionGroups", ExclusionGroupFile)
                .EnumerateObject())
        {
            // The secondary's code is matched case-insensitively, where CMS
            // matches it exactly. The difference is deliberate and one-sided:
            // CHO's own code lookup is already case-insensitive, so matching
            // exactly here would exclude nothing for a differently-cased code
            // that the rest of the engine recognises -- and under-excluding
            // pays more, not less.
            var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (group.Value.TryGetProperty("codes", out var listed))
            {
                foreach (var code in listed.EnumerateArray())
                {
                    var value = code.GetString();
                    if (!string.IsNullOrEmpty(value))
                    {
                        codes.Add(value);
                    }
                }
            }

            groups[group.Name] = codes;
        }

        using var principalDocument = ReadDocument(directory, PrincipalExclusionFile);

        var byPrincipal = new Dictionary<string, IReadOnlySet<string>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var principal in
            Member(principalDocument.RootElement, "pdx_exclusion_groups", PrincipalExclusionFile)
                .EnumerateObject())
        {
            if (!principal.Value.TryGetProperty("groups", out var rows))
            {
                continue;
            }

            foreach (var row in rows.EnumerateArray())
            {
                if (!MentionsVersion(row, version))
                {
                    continue;
                }

                var id = row.TryGetProperty("groupId", out var groupId)
                    ? groupId.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : throw new MsDrgTableImportException(
                        $"{PrincipalExclusionFile} has a row for '{principal.Name}' with no "
                        + "'groupId'.");

                if (!groups.TryGetValue(id, out var codes))
                {
                    throw new MsDrgTableImportException(
                        $"{PrincipalExclusionFile} points '{principal.Name}' at exclusion group "
                        + $"'{id}', which {ExclusionGroupFile} does not hold.");
                }

                if (byPrincipal.TryGetValue(principal.Name, out var already)
                    && !ReferenceEquals(already, codes))
                {
                    throw new MsDrgTableImportException(
                        $"{PrincipalExclusionFile} gives '{principal.Name}' two different "
                        + $"exclusion groups for {version}. The release is inconsistent and "
                        + "guessing which to use would change what the stay is paid.");
                }

                byPrincipal[principal.Name] = codes;
            }
        }

        return new InMemoryExclusionSource(version, byPrincipal);
    }

    /// <summary>
    /// Imports the procedure clusters in <paramref name="directory"/> for
    /// <paramref name="version"/>.
    ///
    /// <para>
    /// Separate from <see cref="Import"/> because the two are used at
    /// different points and a table set without clusters is a coherent thing
    /// to have in a test — but not in production, where a stay whose
    /// qualifying procedure is a combination would be under-coded.
    /// </para>
    /// </summary>
    public static InMemoryClusterSource ImportClusters(string directory, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        using var document = ReadDocument(directory, ClusterFile);

        var clusters = new List<MsDrgCluster>();

        foreach (var cluster in Member(document.RootElement, "clusters", ClusterFile)
            .EnumerateObject())
        {
            if (!TryClusterRowForVersion(cluster.Value, version, cluster.Name, out var row))
            {
                continue;
            }

            // CMS numbers the choice groups from one and the numbering need not
            // be dense, so the groups are collected by their own number and
            // then flattened in that order rather than indexed positionally.
            var byChoice = new SortedDictionary<int, HashSet<string>>();

            if (row.TryGetProperty("choices", out var choices))
            {
                foreach (var choice in choices.EnumerateArray())
                {
                    var number = choice.TryGetProperty("choice", out var numbered)
                        ? numbered.GetInt32()
                        : byChoice.Count + 1;

                    if (!byChoice.TryGetValue(number, out var group))
                    {
                        group = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        byChoice[number] = group;
                    }

                    if (choice.TryGetProperty("codes", out var codes))
                    {
                        foreach (var code in codes.EnumerateArray())
                        {
                            var value = code.GetString();
                            if (!string.IsNullOrEmpty(value))
                            {
                                group.Add(value);
                            }
                        }
                    }
                }
            }

            if (byChoice.Count == 0)
            {
                continue;
            }

            var restricted = new HashSet<int>();
            if (row.TryGetProperty("suppressionMdcs", out var mdcs))
            {
                foreach (var mdc in mdcs.EnumerateArray())
                {
                    restricted.Add(mdc.GetInt32());
                }
            }

            clusters.Add(new MsDrgCluster(
                cluster.Name,
                byChoice.Values.Cast<IReadOnlySet<string>>().ToList(),
                restricted));
        }

        if (clusters.Count == 0)
        {
            throw new MsDrgTableImportException(
                $"The release in '{directory}' carries no clusters for version '{version}'. "
                + "Check the version against the release's own tables rather than grouping "
                + "without clusters, which under-codes any stay whose qualifying procedure is "
                + "a combination.");
        }

        return new InMemoryClusterSource(clusters);
    }

    /// <summary>
    /// A cluster's rows sit under <c>cluster</c> rather than <c>list</c>, so
    /// the version pick is its own method. Unlike a code, a cluster that offers
    /// two rows for one version is not refused: CMS's own reader takes them as
    /// it finds them, and a cluster is a set of codes rather than a claim about
    /// one code, so the first is as good an answer as the release gives.
    /// </summary>
    private static bool TryClusterRowForVersion(
        JsonElement cluster, string version, string name, out JsonElement row)
    {
        row = default;

        if (!cluster.TryGetProperty("cluster", out var rows))
        {
            return false;
        }

        foreach (var candidate in rows.EnumerateArray())
        {
            if (MentionsVersion(candidate, version))
            {
                row = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Imports the tables in <paramref name="directory"/> for
    /// <paramref name="version"/>.
    /// </summary>
    /// <param name="directory">
    /// A directory holding the release's table JSON. Nothing is written and
    /// nothing outside the named files is read.
    /// </param>
    /// <param name="version">
    /// The CMS grouper version, as the tables spell it — <c>V440</c> for the
    /// FY2026 V44.0 release. A version no row mentions is an error, not an
    /// empty table set, because an empty table set makes every code unknown and
    /// every stay ungroupable for a reason nobody would look for here.
    /// </param>
    /// <exception cref="MsDrgTableImportException">
    /// A file is missing or unreadable, a row contradicts another, or the
    /// release does not carry this version.
    /// </exception>
    public static ImportedCodeTables Import(string directory, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        if (!Directory.Exists(directory))
        {
            throw new MsDrgTableImportException(
                $"No CMS table directory at '{directory}'.");
        }

        // Operand lists are shared: CMS stores them once per pattern number and
        // points many thousands of codes at the same one. Keeping that sharing
        // rather than copying a list per code is the difference between a few
        // hundred lists and 165,000 of them.
        var diagnosisPatterns = ReadPatterns(directory, DiagnosisPatternFile);
        var procedurePatterns = ReadPatterns(directory, ProcedurePatternFile);

        var sexMdcs = ReadSexMdcs(directory, version);
        var diagnoses = ReadDiagnoses(directory, version, diagnosisPatterns, sexMdcs);
        var procedures = ReadProcedures(directory, version, procedurePatterns);

        if (diagnoses.Count == 0)
        {
            throw new MsDrgTableImportException(
                $"The release in '{directory}' carries no diagnosis rows for version "
                + $"'{version}'. Check the version against the release's own tables rather "
                + "than grouping against an empty table set.");
        }

        return new ImportedCodeTables(version, diagnoses, procedures);
    }

    /// <summary>
    /// Reads a pattern file: a pattern number to the operand list it names.
    ///
    /// <para>
    /// Both the diagnosis and the procedure pattern files wrap their contents
    /// in a member called <c>dxPatterns</c>. That is CMS's spelling in both
    /// files, not a copy-and-paste error here, and reading
    /// <c>prPatterns.json</c> for a <c>prPatterns</c> member finds nothing.
    /// </para>
    /// </summary>
    private static FrozenDictionary<int, IReadOnlyList<string>> ReadPatterns(
        string directory, string file)
    {
        using var document = ReadDocument(directory, file);

        var patterns = new Dictionary<int, IReadOnlyList<string>>();

        foreach (var pattern in Member(document.RootElement, "dxPatterns", file)
            .EnumerateObject())
        {
            if (!int.TryParse(pattern.Name, out var number))
            {
                throw new MsDrgTableImportException(
                    $"{file} has a pattern named '{pattern.Name}', which is not a number.");
            }

            var operands = new List<string>();
            if (pattern.Value.TryGetProperty("operands", out var listed))
            {
                foreach (var operand in listed.EnumerateArray())
                {
                    var name = operand.GetString();
                    if (!string.IsNullOrEmpty(name))
                    {
                        operands.Add(name);
                    }
                }
            }

            patterns[number] = operands;
        }

        return patterns.ToFrozenDictionary();
    }

    private static FrozenDictionary<string, IReadOnlyDictionary<int, int>> ReadSexMdcs(
        string directory, string version)
    {
        using var document = ReadDocument(directory, SexMdcFile);

        var bySexMdc = new Dictionary<string, IReadOnlyDictionary<int, int>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var code in Member(document.RootElement, "CodeGenderMdcs", SexMdcFile)
            .EnumerateObject())
        {
            if (!TryRowForVersion(code.Value, version, SexMdcFile, code.Name, out var row))
            {
                continue;
            }

            if (!row.TryGetProperty("genderMdcMap", out var map))
            {
                continue;
            }

            var mdcs = new Dictionary<int, int>();
            foreach (var entry in map.EnumerateObject())
            {
                if (int.TryParse(entry.Name, out var sex))
                {
                    mdcs[sex] = entry.Value.GetInt32();
                }
            }

            if (mdcs.Count > 0)
            {
                bySexMdc[code.Name] = mdcs;
            }
        }

        return bySexMdc.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static FrozenDictionary<string, DiagnosisCodeAttributes> ReadDiagnoses(
        string directory,
        string version,
        FrozenDictionary<int, IReadOnlyList<string>> patterns,
        FrozenDictionary<string, IReadOnlyDictionary<int, int>> sexMdcs)
    {
        using var document = ReadDocument(directory, DiagnosisFile);

        var diagnoses = new Dictionary<string, DiagnosisCodeAttributes>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var code in Member(document.RootElement, "dxMap", DiagnosisFile)
            .EnumerateObject())
        {
            if (!TryRowForVersion(code.Value, version, DiagnosisFile, code.Name, out var row))
            {
                continue;
            }

            sexMdcs.TryGetValue(code.Name, out var codeSexMdcs);

            diagnoses[code.Name] = new DiagnosisCodeAttributes(
                Mdc: row.TryGetProperty("mdc", out var mdc) ? mdc.GetInt32() : 0,
                Severity: ParseSeverity(row, DiagnosisFile, code.Name),
                Operands: Operands(row, "operandsPatternNum", patterns, DiagnosisFile, code.Name),
                DiagnosisCategoryOperands: Operands(
                    row, "dxCatListPatternNum", patterns, DiagnosisFile, code.Name),
                HacOperands: Operands(
                    row, "hacOperandsPatternNum", patterns, DiagnosisFile, code.Name),
                SexMdcs: codeSexMdcs);
        }

        return diagnoses.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static FrozenDictionary<string, ProcedureCodeAttributes> ReadProcedures(
        string directory,
        string version,
        FrozenDictionary<int, IReadOnlyList<string>> patterns)
    {
        using var document = ReadDocument(directory, ProcedureFile);

        var procedures = new Dictionary<string, ProcedureCodeAttributes>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var code in Member(document.RootElement, "codes", ProcedureFile)
            .EnumerateObject())
        {
            if (!TryRowForVersion(code.Value, version, ProcedureFile, code.Name, out var row))
            {
                continue;
            }

            procedures[code.Name] = new ProcedureCodeAttributes(
                Operands(row, "operandsPatternNum", patterns, ProcedureFile, code.Name));
        }

        return procedures.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Picks the one row in a code's <c>list</c> that applies to this version.
    ///
    /// <para>
    /// A code with no row for the version is simply not in this version's
    /// tables — new codes and retired ones both look like this — so that is a
    /// miss and not an error. Two rows for one version is a different matter:
    /// the release is telling us two things about one code, there is no basis
    /// for preferring either, and silently taking the first is how a grouper
    /// acquires a wrong answer it can never explain.
    /// </para>
    /// </summary>
    private static bool TryRowForVersion(
        JsonElement code, string version, string file, string name, out JsonElement row)
    {
        row = default;

        if (!code.TryGetProperty("list", out var rows))
        {
            return false;
        }

        var found = false;

        foreach (var candidate in rows.EnumerateArray())
        {
            if (!MentionsVersion(candidate, version))
            {
                continue;
            }

            if (found)
            {
                throw new MsDrgTableImportException(
                    $"{file} gives code '{name}' more than one row for version "
                    + $"'{version}'. The release contradicts itself about this code, so "
                    + "there is nothing to choose between them.");
            }

            row = candidate;
            found = true;
        }

        return found;
    }

    private static bool MentionsVersion(JsonElement row, string version)
    {
        if (!row.TryGetProperty("versions", out var versions))
        {
            return false;
        }

        foreach (var candidate in versions.EnumerateArray())
        {
            if (string.Equals(
                candidate.GetString(), version, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> Operands(
        JsonElement row,
        string member,
        FrozenDictionary<int, IReadOnlyList<string>> patterns,
        string file,
        string name)
    {
        if (!row.TryGetProperty(member, out var number))
        {
            return [];
        }

        var pattern = number.GetInt32();

        if (!patterns.TryGetValue(pattern, out var operands))
        {
            throw new MsDrgTableImportException(
                $"{file} points code '{name}' at operand pattern {pattern} via "
                + $"'{member}', and no such pattern exists. The table files are from "
                + "different releases, or one of them is truncated.");
        }

        return operands;
    }

    private static ComplicationLevel ParseSeverity(
        JsonElement row, string file, string name)
    {
        if (!row.TryGetProperty("severity", out var severity))
        {
            return ComplicationLevel.None;
        }

        return severity.GetString() switch
        {
            "MCC" => ComplicationLevel.Mcc,
            "CC" => ComplicationLevel.Cc,
            "NCC" or null or "" => ComplicationLevel.None,
            var other => throw new MsDrgTableImportException(
                $"{file} gives code '{name}' a severity of '{other}', which is not one of "
                + "CMS's three. Guessing which it meant would change paid amounts."),
        };
    }

    private static JsonElement Member(JsonElement root, string name, string file) =>
        root.TryGetProperty(name, out var member)
            ? member
            : throw new MsDrgTableImportException(
                $"{file} has no '{name}' member. This is not a CMS table file of the "
                + "shape this importer reads.");

    private static JsonDocument ReadDocument(string directory, string file)
    {
        var path = Path.Combine(directory, file);

        if (!File.Exists(path))
        {
            throw new MsDrgTableImportException($"No '{file}' in '{directory}'.");
        }

        try
        {
            using var stream = File.OpenRead(path);
            return JsonDocument.Parse(stream);
        }
        catch (JsonException exception)
        {
            throw new MsDrgTableImportException(
                $"'{path}' is not readable as JSON.", exception);
        }
        catch (IOException exception)
        {
            throw new MsDrgTableImportException($"'{path}' could not be read.", exception);
        }
    }

}

/// <summary>
/// A CMS release's code tables, imported for one version.
///
/// <para>
/// Frozen rather than merely read-only: built once at startup and then read on
/// every claim, which is what <see cref="FrozenDictionary{TKey, TValue}"/> is
/// for. The counts are public because they are the only cheap check that an
/// import of a real release went right — a release that imported eleven codes
/// imported the wrong version, and every claim after that would be ungroupable
/// for a reason nobody would look for in the table files.
/// </para>
/// </summary>
public sealed class ImportedCodeTables : IMsDrgCodeAttributeSource
{
    private readonly FrozenDictionary<string, DiagnosisCodeAttributes> diagnoses;
    private readonly FrozenDictionary<string, ProcedureCodeAttributes> procedures;

    internal ImportedCodeTables(
        string version,
        FrozenDictionary<string, DiagnosisCodeAttributes> diagnoses,
        FrozenDictionary<string, ProcedureCodeAttributes> procedures)
    {
        Version = version;
        this.diagnoses = diagnoses;
        this.procedures = procedures;
    }

    /// <inheritdoc/>
    public string Version { get; }

    /// <summary>How many diagnosis codes the import found for this version.</summary>
    public int DiagnosisCount => diagnoses.Count;

    /// <summary>How many procedure codes the import found for this version.</summary>
    public int ProcedureCount => procedures.Count;

    /// <inheritdoc/>
    public bool TryGetDiagnosis(string code, out DiagnosisCodeAttributes attributes)
    {
        ArgumentNullException.ThrowIfNull(code);
        return diagnoses.TryGetValue(code, out attributes!);
    }

    /// <inheritdoc/>
    public bool TryGetProcedure(string code, out ProcedureCodeAttributes attributes)
    {
        ArgumentNullException.ThrowIfNull(code);
        return procedures.TryGetValue(code, out attributes!);
    }
}
