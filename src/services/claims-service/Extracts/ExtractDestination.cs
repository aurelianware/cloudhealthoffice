using ClaimsService.Import.Cpcds.Drop;
using Google.Cloud.Storage.V1;

namespace ClaimsService.Extracts;

/// <summary>
/// Where an extract's files go: the tenant's own drop, under
/// <c>{tenant}/outbound/extracts/{dataset}/{runId}/</c>. That is the same
/// storage the plan's SFTP login reads, where <c>/outbound</c> is
/// list-and-download only, so the plan collects the files with the login it
/// already uses to send its core extracts.
/// </summary>
public interface IExtractDestination
{
    /// <summary>
    /// Writes the local file to <c>{tenant}/outbound/extracts/{dataset}/{runId}/{name}</c>,
    /// whole or not at all (a reader never sees a partial file), and says where.
    /// </summary>
    Task<string> WriteAsync(string tenantId, string dataset, string runId, string name, string localPath, CancellationToken ct);
}

public static class ExtractPaths
{
    public const string Outbound = "outbound";
    public const string Extracts = "extracts";

    /// <summary>The path segments under the drop root, each checked like the drop's own folder names.</summary>
    public static string[] Segments(string tenantId, string dataset, string runId, string name)
    {
        string[] parts = [tenantId, Outbound, Extracts, dataset, runId, name];
        foreach (var part in parts)
        {
            if (!FolderCpcdsDropStore.SafeSegment.IsMatch(part))
                throw new ArgumentException($"Not a usable extract path segment: {part}");
        }
        return parts;
    }
}

/// <summary><see cref="IExtractDestination"/> over the mounted drop folder.</summary>
public sealed class FolderExtractDestination : IExtractDestination
{
    private readonly string _root;

    public FolderExtractDestination(string root) => _root = Path.GetFullPath(root);

    public async Task<string> WriteAsync(string tenantId, string dataset, string runId, string name, string localPath, CancellationToken ct)
    {
        var target = Path.Combine([_root, .. ExtractPaths.Segments(tenantId, dataset, runId, name)]);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        // Copy under a transient name (".part" is skipped by anything that
        // lists the drop), then rename: the file appears complete or not at all.
        var partial = target + ".part";
        await using (var source = File.OpenRead(localPath))
        await using (var destination = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            await source.CopyToAsync(destination, ct);
        File.Move(partial, target, overwrite: true);
        return target;
    }
}

/// <summary>
/// <see cref="IExtractDestination"/> over the drop bucket. An object upload
/// is atomic: a reader sees the whole object or none.
/// </summary>
public sealed class GcsExtractDestination : IExtractDestination
{
    private readonly StorageClient _client;
    private readonly string _bucket;

    public GcsExtractDestination(StorageClient client, string bucket)
    {
        if (string.IsNullOrWhiteSpace(bucket))
            throw new ArgumentException($"{CpcdsDropOptions.SectionName}:Gcs:Bucket is required for the Gcs drop store.", nameof(bucket));
        _client = client;
        _bucket = bucket;
    }

    public async Task<string> WriteAsync(string tenantId, string dataset, string runId, string name, string localPath, CancellationToken ct)
    {
        var objectName = string.Join('/', ExtractPaths.Segments(tenantId, dataset, runId, name));
        var contentType = name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "application/json" : "text/csv";
        await using var source = File.OpenRead(localPath);
        await _client.UploadObjectAsync(_bucket, objectName, contentType, source, cancellationToken: ct);
        return $"gs://{_bucket}/{objectName}";
    }
}
