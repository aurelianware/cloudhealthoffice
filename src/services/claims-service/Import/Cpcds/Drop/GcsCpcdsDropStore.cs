using System.Net;
using System.Text;
using Google;
using Google.Cloud.Storage.V1;

namespace ClaimsService.Import.Cpcds.Drop;

/// <summary>
/// <see cref="ICpcdsDropStore"/> over a Google Cloud Storage bucket: the
/// hosted drop, where the SFTP server writes each plan's uploads straight
/// into the bucket and claims-service reads them with its own Google
/// identity (Workload Identity on GKE). The layout is the folder store's, as
/// object name prefixes: <c>{tenant}/inbound/</c>, <c>{tenant}/processing/{run}/</c>,
/// <c>{tenant}/processed/{run}/</c>, <c>{tenant}/failed/{run}/</c>.
///
/// <para>
/// A bucket has no rename, so a file is taken the way a rename would take
/// it, in two steps that only one run can finish: copy it into the run's
/// prefix (only if it is still the generation that was listed), then delete
/// the inbound object, again only if it is still that generation. Two runs
/// can both copy; only one delete succeeds. The run whose delete fails
/// removes its copy and leaves the file to the other. A file rewritten
/// after it was listed fails the first precondition and waits for the next
/// poll, so a half-replaced extract is never loaded.
/// </para>
/// </summary>
public sealed class GcsCpcdsDropStore : ICpcdsDropStore
{
    private readonly StorageClient _client;
    private readonly string _bucket;

    public GcsCpcdsDropStore(StorageClient client, string bucket)
    {
        if (string.IsNullOrWhiteSpace(bucket))
            throw new ArgumentException($"{CpcdsDropOptions.SectionName}:Gcs:Bucket is required for the Gcs drop store.", nameof(bucket));
        _client = client;
        _bucket = bucket;
    }

    public async Task<IReadOnlyList<CpcdsDropFile>> ListReadyAsync(
        string tenantId, TimeSpan quietPeriod, DateTime nowUtc, CancellationToken ct)
    {
        var prefix = Prefix(tenantId, "inbound");
        var ready = new List<CpcdsDropFile>();

        // Delimiter "/": only the inbound folder itself, not anything an
        // uploader nested under it.
        await foreach (var obj in _client.ListObjectsAsync(_bucket, prefix, new ListObjectsOptions { Delimiter = "/" })
                           .WithCancellation(ct))
        {
            var name = obj.Name[prefix.Length..];
            if (name.Length == 0 || FolderCpcdsDropStore.IsTransient(name))
                continue;

            var updated = obj.UpdatedDateTimeOffset?.UtcDateTime ?? DateTime.MaxValue;
            if (updated > nowUtc - quietPeriod)
                continue;

            ready.Add(new CpcdsDropFile(name, obj.Name, (long)(obj.Size ?? 0), updated, obj.Generation));
        }

        return ready.OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
    }

    public async Task<CpcdsDropFile?> ClaimAsync(string tenantId, string runId, CpcdsDropFile file, CancellationToken ct)
    {
        var destination = Prefix(tenantId, "processing", runId) + file.Name;

        try
        {
            await _client.CopyObjectAsync(_bucket, file.Path, _bucket, destination,
                new CopyObjectOptions { IfSourceGenerationMatch = file.Generation }, ct);
        }
        catch (GoogleApiException ex) when (IsGoneOrChanged(ex))
        {
            return null;
        }

        try
        {
            await _client.DeleteObjectAsync(_bucket, file.Path,
                new DeleteObjectOptions { IfGenerationMatch = file.Generation }, ct);
        }
        catch (GoogleApiException ex) when (IsGoneOrChanged(ex))
        {
            // Another run deleted it first (or it was replaced): that run owns
            // the file. Our copy must not load as well.
            await DeleteQuietlyAsync(destination, CancellationToken.None);
            return null;
        }

        return file with { Path = destination };
    }

    public async Task<string> ReadAllTextAsync(CpcdsDropFile file, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await _client.DownloadObjectAsync(_bucket, file.Path, buffer, cancellationToken: ct);
        buffer.Position = 0;
        // As File.ReadAllTextAsync does: UTF-8 unless a byte order mark says otherwise.
        using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct);
    }

    public async Task<string> CompleteAsync(string tenantId, string runId, bool succeeded, CancellationToken ct)
    {
        var source = Prefix(tenantId, "processing", runId);
        var destination = Prefix(tenantId, succeeded ? "processed" : "failed", runId);

        // Only this run writes under its processing prefix, so a plain move is safe.
        var names = new List<string>();
        await foreach (var obj in _client.ListObjectsAsync(_bucket, source).WithCancellation(ct))
            names.Add(obj.Name);

        foreach (var name in names)
        {
            await _client.CopyObjectAsync(_bucket, name, _bucket, destination + name[source.Length..], cancellationToken: ct);
            await DeleteQuietlyAsync(name, ct);
        }

        return $"gs://{_bucket}/{destination}";
    }

    private async Task DeleteQuietlyAsync(string name, CancellationToken ct)
    {
        try
        {
            await _client.DeleteObjectAsync(_bucket, name, cancellationToken: ct);
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    private static bool IsGoneOrChanged(GoogleApiException ex)
        => ex.HttpStatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed;

    /// <summary>The object name prefix for a tenant folder, with the folder store's naming rules.</summary>
    internal static string Prefix(string tenantId, params string[] parts)
    {
        if (!FolderCpcdsDropStore.SafeSegment.IsMatch(tenantId))
            throw new ArgumentException("Not a usable tenant folder name.", nameof(tenantId));
        foreach (var part in parts)
        {
            if (!FolderCpcdsDropStore.SafeSegment.IsMatch(part))
                throw new ArgumentException("Not a usable folder name.", nameof(parts));
        }
        return string.Join('/', [tenantId, .. parts]) + "/";
    }
}
