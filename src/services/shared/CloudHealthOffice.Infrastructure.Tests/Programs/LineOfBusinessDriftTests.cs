using System.Text.RegularExpressions;
using CloudHealthOffice.Infrastructure.Programs;

namespace CloudHealthOffice.Infrastructure.Tests.Programs;

/// <summary>
/// Every service-local line-of-business enum must match the shared
/// <see cref="LineOfBusiness"/> (same names, same numbers) until it is replaced by it
/// (docs/architecture/line-of-business.md). The known exceptions are listed with the
/// migration that removes them; the list may only shrink.
/// </summary>
public class LineOfBusinessDriftTests
{
    /// <summary>Enums that differ today, by repository path, and why. Remove an entry when its migration lands.</summary>
    private static readonly Dictionary<string, string> KnownExceptions = new()
    {
        ["src/services/appeals-service/Models/Appeal.cs"] =
            "Marketplace and MedicarePartD; moves to the shared enum plus a Part D benefit type (consolidation PR 2).",
        ["src/services/shared/CloudHealthOffice.Appeals.Contracts/AppealDtoEnums.cs"] =
            "Wire copy of the appeals enum (consolidation PR 2).",
        ["src/services/payment-service/Models/PaymentRun.cs"] =
            "Unnumbered with Marketplace; stored run criteria need a one-time 0-3 to 1-4 migration (consolidation PR 3).",
        ["src/services/eligibility-service/Models/Eligibility.cs"] =
            "Unnumbered 0-5; stored records need a one-time migration to 1-6 (consolidation PR 4).",
        ["src/engines/CloudHealthOffice.ProviderEnrollmentService/Models/EnrollmentModels.cs"] =
            "A flags enum of programs (STAR, CHIP ...), to be renamed ProgramParticipation (consolidation PR 7).",
    };

    private static readonly Regex EnumDeclaration = new(
        @"\benum\s+(?<name>\w*LineOfBusiness)\s*(?::\s*\w+\s*)?\{(?<body>[^}]*)\}",
        RegexOptions.Compiled);

    private static readonly (string Name, int Value)[] Canonical =
        LineOfBusinessNames.All.Select(l => (l.ToString(), (int)l)).ToArray();

    [Fact]
    public void Every_local_line_of_business_enum_matches_the_shared_one()
    {
        var found = LocalEnums().ToList();
        found.Should().NotBeEmpty("the scan should see the service enums (is the repository root right?)");

        var drifted = found
            .Where(e => !KnownExceptions.ContainsKey(e.Path))
            .Where(e => !Matches(e.Members))
            .Select(e => $"{e.Path}: {e.Name} {{ {string.Join(", ", e.Members.Select(m => $"{m.Name}={m.Value}"))} }}")
            .ToList();

        drifted.Should().BeEmpty(
            "a line-of-business enum must use the shared names and numbers (Commercial=1, Medicare=2, Medicaid=3, Exchange=4, TRICARE=5, VA=6, optional Unknown=0). " +
            "Better: use CloudHealthOffice.Infrastructure.Programs.LineOfBusiness instead of a local enum.");
    }

    [Fact]
    public void Known_exceptions_are_removed_once_fixed()
    {
        var byPath = LocalEnums().GroupBy(e => e.Path).ToDictionary(g => g.Key, g => g.ToList());

        var stale = KnownExceptions.Keys
            .Where(path => !byPath.TryGetValue(path, out var enums) || enums.All(e => Matches(e.Members)))
            .ToList();

        stale.Should().BeEmpty("these enums now match the shared one or are gone, so take them off KnownExceptions");
    }

    private static bool Matches(IReadOnlyList<(string Name, int Value)> members)
    {
        var withoutUnknown = members.Where(m => !(m.Name == "Unknown" && m.Value == 0)).ToList();
        return withoutUnknown.SequenceEqual(Canonical);
    }

    private sealed record LocalEnum(string Path, string Name, IReadOnlyList<(string Name, int Value)> Members);

    private static IEnumerable<LocalEnum> LocalEnums()
    {
        var root = RepoRoot();
        var shared = Path.Combine(root, "src", "services", "shared", "CloudHealthOffice.Infrastructure", "Programs");
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.StartsWith(shared, StringComparison.Ordinal))
                continue;
            var text = File.ReadAllText(file);
            if (!text.Contains("LineOfBusiness", StringComparison.Ordinal)) continue;
            var code = StripComments(text);
            foreach (System.Text.RegularExpressions.Match m in EnumDeclaration.Matches(code))
            {
                yield return new LocalEnum(
                    Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'),
                    m.Groups["name"].Value,
                    Members(m.Groups["body"].Value));
            }
        }
    }

    private static List<(string Name, int Value)> Members(string body)
    {
        var members = new List<(string, int)>();
        var next = 0;
        foreach (var raw in body.Split(','))
        {
            var part = Regex.Replace(raw, @"\[[^\]]*\]", string.Empty).Trim();
            if (part.Length == 0) continue;
            var eq = part.IndexOf('=');
            var name = (eq < 0 ? part : part[..eq]).Trim();
            // Unparseable values (flags like 1 << 3) count as -1: they never match the shared enum.
            var value = eq < 0 ? next : int.TryParse(part[(eq + 1)..].Trim(), out var v) ? v : -1;
            members.Add((name, value));
            next = value + 1;
        }
        return members;
    }

    private static string StripComments(string code)
    {
        code = Regex.Replace(code, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(code, @"//[^\n]*", string.Empty);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "services", "shared", "CloudHealthOffice.Infrastructure")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
