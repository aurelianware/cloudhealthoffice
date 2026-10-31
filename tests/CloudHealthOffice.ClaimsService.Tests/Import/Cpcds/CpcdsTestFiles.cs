namespace CloudHealthOffice.ClaimsService.Tests.Import.Cpcds;

/// <summary>The sample CPCDS file shipped with the importer's docs, and small builders for edge cases.</summary>
internal static class CpcdsTestFiles
{
    public const string SamplePath = "docs/integration/samples/cpcds/claims.csv";

    public static string Sample() => File.ReadAllText(Path.Combine(RepoRoot(), SamplePath));

    /// <summary>The sample with only the named claim's rows kept (header included).</summary>
    public static string SampleClaim(string claimId)
    {
        var lines = Sample().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        return string.Join('\n', new[] { lines[0] }.Concat(lines.Skip(1).Where(l => l.StartsWith(claimId + ",", StringComparison.Ordinal)))) + "\n";
    }

    /// <summary>A file from a header and rows given as column → value maps; missing values are empty.</summary>
    public static string Build(IReadOnlyList<string> header, params IReadOnlyDictionary<string, string>[] rows)
    {
        static string Quote(string v) => v.Contains(',') || v.Contains('"') || v.Contains('\n')
            ? "\"" + v.Replace("\"", "\"\"") + "\""
            : v;

        var lines = new List<string> { string.Join(',', header.Select(Quote)) };
        foreach (var row in rows)
            lines.Add(string.Join(',', header.Select(h => Quote(row.TryGetValue(h, out var v) ? v : string.Empty))));
        return string.Join("\r\n", lines) + "\r\n";
    }

    /// <summary>The columns of a minimal professional claim.</summary>
    public static readonly string[] ProfessionalHeader =
    [
        "Claim unique identifier", "Member id", "Claim type", "Claim service start date",
        "Claim billing provider NPI", "Claim billing provider network status", "Claim total submitted amount",
        "Claim total allowed amount", "Claim payment amount", "Claim payment denial code", "Claim payment status code",
        "Claim processing status code", "Line number", "Line submitted amount", "Procedure code",
        "Diagnosis code", "Diagnosis code type", "Diagnosis type",
    ];

    public static Dictionary<string, string> ProfessionalRow(string claimId = "C-1", string memberId = "M-1") => new()
    {
        ["Claim unique identifier"] = claimId,
        ["Member id"] = memberId,
        ["Claim type"] = "professional-nonclinician",
        ["Claim service start date"] = "2026-02-01",
        ["Claim billing provider NPI"] = "1234567893",
        ["Claim billing provider network status"] = "innetwork",
        ["Claim total submitted amount"] = "100.00",
        ["Claim total allowed amount"] = "80.00",
        ["Claim payment amount"] = "64.00",
        ["Line number"] = "1",
        ["Line submitted amount"] = "100.00",
        ["Procedure code"] = "99213",
        ["Diagnosis code"] = "J06.9",
        ["Diagnosis code type"] = "icd10",
        ["Diagnosis type"] = "principal",
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "cloudhealthoffice-main.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
