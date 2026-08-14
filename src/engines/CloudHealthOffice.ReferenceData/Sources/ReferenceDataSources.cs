using CloudHealthOffice.ReferenceData.Domain;

namespace CloudHealthOffice.ReferenceData.Sources;

public interface IReferenceDataSource
{
    string SourceId { get; }
    Task<ReferenceDataPackage> RetrieveAsync(
        ReferenceDataSourceRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ReferenceDataSourceRequest(string? Version = null, DateOnly? EffectiveDate = null);

public sealed record ReferenceDataPackage
{
    public required string SourceId { get; init; }
    public required string SourceVersion { get; init; }
    public required Stream Content { get; init; }
    public required string Checksum { get; init; }
    public DateTimeOffset RetrievedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record ReferenceDataPreview(
    string SourceId,
    string SourceVersion,
    int RecordCount,
    IReadOnlyList<string> ValidationErrors);

/// <summary>Stages are separate so retrieving data can never implicitly activate it.</summary>
public interface IReferenceDataImporter
{
    Task<ReferenceDataPackage> RetrieveAsync(IReferenceDataSource source, ReferenceDataSourceRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<ReferenceCode>> ParseAsync(ReferenceDataPackage package, CancellationToken ct = default);
    IReadOnlyList<ReferenceCode> Normalize(IReadOnlyList<ReferenceCode> records);
    IReadOnlyList<string> Validate(IReadOnlyList<ReferenceCode> records);
    ReferenceDataPreview Preview(ReferenceDataPackage package, IReadOnlyList<ReferenceCode> records, IReadOnlyList<string> errors);
    Task<ImportResult> ImportAsync(IReadOnlyList<ReferenceCode> records, CancellationToken ct = default);
    Task ActivateAsync(string sourceId, string sourceVersion, CancellationToken ct = default);
}

public sealed record ImportResult(int ImportedCount, bool AlreadyImported, string Checksum);
