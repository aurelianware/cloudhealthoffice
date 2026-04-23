using System.IO;
using System.Reflection;

namespace CloudHealthOffice.FhirService.Tests.FhirArtifacts;

/// <summary>
/// Test helper that locates and reads CHO FHIR artifact source files from
/// <c>docs/fhir/profiles/</c>. Resolves the path relative to the repo root
/// by walking up from the test assembly's location. Used by artifact
/// validity, differential, and snapshot tests that verify the committed
/// source JSON — not the embedded copy.
/// </summary>
internal static class TestArtifactFiles
{
    private static readonly string _profilesDirectory = ResolveProfilesDirectory();

    public static string ProfilesDirectory => _profilesDirectory;

    public static IEnumerable<string> AllJsonFiles
        => Directory.EnumerateFiles(_profilesDirectory, "*.json");

    public static IEnumerable<string> JsonFilesMatching(string prefix)
        => Directory.EnumerateFiles(_profilesDirectory, $"{prefix}*.json");

    public static string ReadAllText(string absolutePath)
        => File.ReadAllText(absolutePath);

    private static string ResolveProfilesDirectory()
    {
        var assemblyDir = Path.GetDirectoryName(
            Assembly.GetExecutingAssembly().Location)
            ?? throw new InvalidOperationException("Cannot locate test assembly directory");

        var dir = new DirectoryInfo(assemblyDir);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "fhir", "profiles");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate docs/fhir/profiles/ by walking up from " + assemblyDir);
    }
}
