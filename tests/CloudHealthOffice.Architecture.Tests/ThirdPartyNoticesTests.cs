using System.Text.RegularExpressions;

namespace CloudHealthOffice.Architecture.Tests;

/// <summary>
/// THIRD-PARTY-NOTICES.md lists every package the build uses. A package added
/// to Directory.Packages.props without regenerating the notices
/// (scripts/licenses/generate-third-party-notices.py) fails here. Version bumps
/// are not checked, so Dependabot updates don't fail; regenerate periodically.
/// </summary>
public class ThirdPartyNoticesTests
{
    [Fact]
    public void Every_central_package_is_in_the_third_party_notices()
    {
        var root = Root();
        var props = File.ReadAllText(Path.Combine(root, "Directory.Packages.props"));
        var notices = File.ReadAllText(Path.Combine(root, "THIRD-PARTY-NOTICES.md"));

        var ids = Regex.Matches(props, "PackageVersion Include=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(ids);
        var missing = ids.Where(id => !notices.Contains($"| {id} |", StringComparison.Ordinal)).ToList();

        Assert.True(missing.Count == 0,
            "Not in THIRD-PARTY-NOTICES.md (run scripts/licenses/generate-third-party-notices.py): " + string.Join(", ", missing));
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Packages.props")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
