using System.Text.RegularExpressions;

namespace ClaimsService.Import.Cpcds.Drop;

/// <summary>A file in a tenant's drop.</summary>
public sealed record CpcdsDropFile(string Name, string Path, long Length, DateTime LastWriteUtc);

/// <summary>
/// Where the files arrive and where they go after a load. A run first moves
/// every file it will load into its own <c>processing/{runId}</c> folder. The
/// move is atomic, so when several pods poll the same drop exactly one of
/// them gets each file, and no lock is needed.
/// </summary>
public interface ICpcdsDropStore
{
    /// <summary>Files in the tenant's inbound folder that have not changed for <paramref name="quietPeriod"/>.</summary>
    IReadOnlyList<CpcdsDropFile> ListReady(string tenantId, TimeSpan quietPeriod, DateTime nowUtc);

    /// <summary>Moves the file into the run's folder; null when another run took it first.</summary>
    CpcdsDropFile? Claim(string tenantId, string runId, CpcdsDropFile file);

    Task<string> ReadAllTextAsync(CpcdsDropFile file, CancellationToken ct);

    /// <summary>Moves the run's folder to <c>processed/{runId}</c>, or <c>failed/{runId}</c>.</summary>
    string Complete(string tenantId, string runId, bool succeeded);
}

/// <summary><see cref="ICpcdsDropStore"/> over a mounted folder.</summary>
public sealed class FolderCpcdsDropStore : ICpcdsDropStore
{
    private static readonly Regex SafeSegment = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.Compiled);

    private readonly string _root;

    public FolderCpcdsDropStore(string root) => _root = Path.GetFullPath(root);

    public IReadOnlyList<CpcdsDropFile> ListReady(string tenantId, TimeSpan quietPeriod, DateTime nowUtc)
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

    public CpcdsDropFile? Claim(string tenantId, string runId, CpcdsDropFile file)
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

    public string Complete(string tenantId, string runId, bool succeeded)
    {
        var source = TenantFolder(tenantId, "processing", runId);
        var parent = Directory.CreateDirectory(TenantFolder(tenantId, succeeded ? "processed" : "failed")).FullName;
        var destination = Path.Combine(parent, runId);
        if (Directory.Exists(source))
            Directory.Move(source, destination);
        return destination;
    }

    /// <summary>Partial uploads and hidden files are never read.</summary>
    internal static bool IsTransient(string name) =>
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
