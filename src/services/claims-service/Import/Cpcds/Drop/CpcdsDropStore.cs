using System.Text.RegularExpressions;

namespace ClaimsService.Import.Cpcds.Drop;

/// <summary>
/// A file in a tenant's drop. <see cref="Path"/> is where the store keeps it
/// (a file path, or an object name); <see cref="Generation"/> is the object
/// generation on a store that versions objects, so a file rewritten after it
/// was listed is not taken as the version that was listed.
/// </summary>
public sealed record CpcdsDropFile(string Name, string Path, long Length, DateTime LastWriteUtc, long? Generation = null);

/// <summary>
/// Where the files arrive and where they go after a load. A run first moves
/// every file it will load into its own <c>processing/{runId}</c> folder.
/// Exactly one run can take each file (an atomic rename on a folder, a
/// generation-matched delete on a bucket), so when several pods poll the
/// same drop no lock is needed.
/// </summary>
public interface ICpcdsDropStore
{
    /// <summary>Files in the tenant's inbound folder that have not changed for <paramref name="quietPeriod"/>.</summary>
    Task<IReadOnlyList<CpcdsDropFile>> ListReadyAsync(string tenantId, TimeSpan quietPeriod, DateTime nowUtc, CancellationToken ct);

    /// <summary>Moves the file into the run's folder; null when another run took it first, or it changed since it was listed.</summary>
    Task<CpcdsDropFile?> ClaimAsync(string tenantId, string runId, CpcdsDropFile file, CancellationToken ct);

    Task<string> ReadAllTextAsync(CpcdsDropFile file, CancellationToken ct);

    /// <summary>Moves the run's folder to <c>processed/{runId}</c>, or <c>failed/{runId}</c>, and says where.</summary>
    Task<string> CompleteAsync(string tenantId, string runId, bool succeeded, CancellationToken ct);
}

/// <summary><see cref="ICpcdsDropStore"/> over a mounted folder.</summary>
public sealed class FolderCpcdsDropStore : ICpcdsDropStore
{
    internal static readonly Regex SafeSegment = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.Compiled);

    private readonly string _root;

    public FolderCpcdsDropStore(string root) => _root = Path.GetFullPath(root);

    public Task<IReadOnlyList<CpcdsDropFile>> ListReadyAsync(
        string tenantId, TimeSpan quietPeriod, DateTime nowUtc, CancellationToken ct)
        => Task.FromResult(ListReady(tenantId, quietPeriod, nowUtc));

    private IReadOnlyList<CpcdsDropFile> ListReady(string tenantId, TimeSpan quietPeriod, DateTime nowUtc)
    {
        var inbound = TenantFolder(tenantId, "inbound");
        if (!Directory.Exists(inbound)) return [];

        return new DirectoryInfo(inbound).EnumerateFiles()
            .Where(f => !IsTransient(f.Name))
            .Where(f => f.LastWriteTimeUtc <= nowUtc - quietPeriod)
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => new CpcdsDropFile(f.Name, f.FullName, f.Length, f.LastWriteTimeUtc))
            .ToList();
    }

    public Task<CpcdsDropFile?> ClaimAsync(string tenantId, string runId, CpcdsDropFile file, CancellationToken ct)
        => Task.FromResult(Claim(tenantId, runId, file));

    private CpcdsDropFile? Claim(string tenantId, string runId, CpcdsDropFile file)
    {
        var target = Directory.CreateDirectory(TenantFolder(tenantId, "processing", runId)).FullName;
        var destination = Path.Combine(target, file.Name);
        try
        {
            File.Move(file.Path, destination);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException)
        {
            return null;
        }
        return file with { Path = destination };
    }

    public Task<string> ReadAllTextAsync(CpcdsDropFile file, CancellationToken ct) => File.ReadAllTextAsync(file.Path, ct);

    public Task<string> CompleteAsync(string tenantId, string runId, bool succeeded, CancellationToken ct)
        => Task.FromResult(Complete(tenantId, runId, succeeded));

    private string Complete(string tenantId, string runId, bool succeeded)
    {
        var source = TenantFolder(tenantId, "processing", runId);
        var parent = Directory.CreateDirectory(TenantFolder(tenantId, succeeded ? "processed" : "failed")).FullName;
        var destination = Path.Combine(parent, runId);
        if (Directory.Exists(source))
            Directory.Move(source, destination);
        return destination;
    }

    /// <summary>Partial uploads and hidden files are never read.</summary>
    public static bool IsTransient(string name) =>
        name.StartsWith('.')
        || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".filepart", StringComparison.OrdinalIgnoreCase);

    private string TenantFolder(string tenantId, params string[] parts)
    {
        if (!SafeSegment.IsMatch(tenantId))
            throw new ArgumentException("Not a usable tenant folder name.", nameof(tenantId));
        foreach (var part in parts)
        {
            if (!SafeSegment.IsMatch(part))
                throw new ArgumentException("Not a usable folder name.", nameof(parts));
        }
        return Path.Combine([_root, tenantId, .. parts]);
    }
}
