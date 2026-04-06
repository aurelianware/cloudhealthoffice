using CloudHealthOffice.BenchmarkClaimGenerator.Models;

namespace CloudHealthOffice.BenchmarkClaimGenerator.Output;

/// <summary>
/// Interface for writing generated claims to an output format.
/// Implementations handle serialization and file management.
/// </summary>
public interface ICorpusWriter : IAsyncDisposable
{
    /// <summary>
    /// Initialize the writer (create output directories, open streams, etc.).
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Write a single claim to the output.
    /// Thread-safe: multiple producers may call this concurrently.
    /// </summary>
    Task WriteClaimAsync(SyntheticClaim claim, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finalize the output (close streams, write summary files, etc.).
    /// </summary>
    Task FinalizeAsync(CancellationToken cancellationToken = default);
}
