using System.Security.Cryptography;
using System.Text;
using FluentAssertions;

namespace CloudHealthOffice.FhirService.Tests.FhirArtifacts;

/// <summary>
/// Snapshot test: SHA-256 of each artifact's raw JSON is compared against a
/// committed hash file under Snapshots/. Catches unintentional drift in
/// profile content. When an intentional change is made, the failure
/// message includes a one-line command to regenerate the snapshot.
/// </summary>
public class ArtifactSnapshotTests
{
    private static readonly string _snapshotsDirectory = ResolveSnapshotsDirectory();

    public static IEnumerable<object[]> AllArtifactFiles
        => TestArtifactFiles.AllJsonFiles.Select(path => new object[] { path });

    [Theory]
    [MemberData(nameof(AllArtifactFiles))]
    public void Artifact_hash_matches_committed_snapshot(string absolutePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(absolutePath);
        var snapshotPath = Path.Combine(_snapshotsDirectory, $"{fileName}.sha256");

        var actualHash = ComputeSha256(absolutePath);

        if (!File.Exists(snapshotPath))
        {
            Assert.Fail(
                $"Snapshot for {fileName} does not exist. If this artifact is new, register its snapshot:\n" +
                $"  echo \"{actualHash}\" > {snapshotPath}");
        }

        var expectedHash = File.ReadAllText(snapshotPath).Trim();

        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            Assert.Fail(
                $"Artifact {fileName} changed. If intentional, update snapshot:\n" +
                $"  echo \"{actualHash}\" > {snapshotPath}");
        }
    }

    private static string ComputeSha256(string filePath)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hashBytes = sha.ComputeHash(stream);
        var sb = new StringBuilder(hashBytes.Length * 2);
        foreach (var b in hashBytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static string ResolveSnapshotsDirectory()
    {
        var assemblyDir = Path.GetDirectoryName(
            System.Reflection.Assembly.GetExecutingAssembly().Location)
            ?? throw new InvalidOperationException("Cannot locate test assembly directory");

        var dir = new DirectoryInfo(assemblyDir);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Snapshots");
            if (Directory.Exists(candidate) &&
                dir.Name == "CloudHealthOffice.FhirService.Tests")
                return candidate;

            var testRoot = Path.Combine(dir.FullName,
                "tests", "CloudHealthOffice.FhirService.Tests", "Snapshots");
            if (Directory.Exists(testRoot)) return testRoot;

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate Snapshots/ directory for the FhirService test project");
    }
}
