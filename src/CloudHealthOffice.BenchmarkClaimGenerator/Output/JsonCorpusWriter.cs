using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.BenchmarkClaimGenerator.Models;

namespace CloudHealthOffice.BenchmarkClaimGenerator.Output;

/// <summary>
/// Writes the corpus as JSON files, partitioned by claim type.
/// Each partition contains one JSON file per batch of claims.
/// </summary>
public class JsonCorpusWriter : ICorpusWriter
{
    private readonly string _outputPath;
    private readonly int _claimsPerFile;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Dictionary<string, List<SyntheticClaim>> _buffers = new();
    private readonly Dictionary<string, int> _fileCounters = new();
    private int _totalWritten;

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonCorpusWriter"/> class.
    /// </summary>
    /// <param name="outputPath">Root directory for corpus output.</param>
    /// <param name="claimsPerFile">Number of claims per output file. Default is 10,000.</param>
    public JsonCorpusWriter(string outputPath, int claimsPerFile = 10_000)
    {
        _outputPath = outputPath;
        _claimsPerFile = claimsPerFile;
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
    }

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_outputPath);
        foreach (var type in new[] { "Professional", "Institutional", "Dental", "EdgeCase" })
        {
            Directory.CreateDirectory(Path.Combine(_outputPath, type));
            _buffers[type] = new List<SyntheticClaim>(_claimsPerFile);
            _fileCounters[type] = 0;
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task WriteClaimAsync(SyntheticClaim claim, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var buffer = _buffers[claim.ClaimType];
            buffer.Add(claim);
            _totalWritten++;

            if (buffer.Count >= _claimsPerFile)
            {
                await FlushBufferAsync(claim.ClaimType, cancellationToken);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task FinalizeAsync(CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            foreach (var type in _buffers.Keys)
            {
                if (_buffers[type].Count > 0)
                {
                    await FlushBufferAsync(type, cancellationToken);
                }
            }

            // Write corpus manifest
            var manifest = new
            {
                GeneratedAt = DateTime.UtcNow,
                TotalClaims = _totalWritten,
                Partitions = _fileCounters.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value)
            };

            var manifestPath = Path.Combine(_outputPath, "corpus-manifest.json");
            var manifestJson = JsonSerializer.Serialize(manifest, _jsonOptions);
            await File.WriteAllTextAsync(manifestPath, manifestJson, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task FlushBufferAsync(string claimType, CancellationToken cancellationToken)
    {
        var buffer = _buffers[claimType];
        if (buffer.Count == 0) return;

        var fileIndex = _fileCounters[claimType]++;
        var fileName = $"{claimType.ToLowerInvariant()}-{fileIndex:D5}.json";
        var filePath = Path.Combine(_outputPath, claimType, fileName);

        var json = JsonSerializer.Serialize(buffer, _jsonOptions);
        await File.WriteAllTextAsync(filePath, json, cancellationToken);
        buffer.Clear();
    }
}
