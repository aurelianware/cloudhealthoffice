namespace CHO.TerminologyService.Services.CodeSystemCatalog;

internal sealed class CodeSystemCatalogSeedService : IHostedService
{
    private readonly ICodeSystemCatalogRepository _repository;
    private readonly ILogger<CodeSystemCatalogSeedService> _logger;

    public CodeSystemCatalogSeedService(
        ICodeSystemCatalogRepository repository,
        ILogger<CodeSystemCatalogSeedService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _repository.UpsertManyAsync(BuiltInIcd10CmCatalog.Concepts, cancellationToken);
        _logger.LogInformation(
            "Seeded {ConceptCount} built-in ICD-10-CM code-system concepts",
            BuiltInIcd10CmCatalog.Concepts.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
