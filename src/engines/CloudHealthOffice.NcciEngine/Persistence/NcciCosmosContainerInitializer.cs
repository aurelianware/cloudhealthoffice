using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.NcciEngine.Persistence;

/// <summary>Cosmos database and container names used by the NCCI engine (configurable).</summary>
internal sealed record NcciCosmosContainers(string Database, string Pairs, string Mues, string Version, string LoadLedger)
{
    public const string PartitionKeyPath = "/tenantId";

    public static NcciCosmosContainers Resolve(IConfiguration configuration) => new(
        configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice",
        configuration["NcciEngine:PairContainer"] ?? "NcciPairs",
        configuration["NcciEngine:MueContainer"] ?? "MueEntries",
        configuration["NcciEngine:VersionContainer"] ?? "NcciVersion",
        configuration["NcciEngine:LoadLedgerContainer"] ?? "NcciLoadLedger");

    public IEnumerable<string> All => [Pairs, Mues, Version, LoadLedger];
}

/// <summary>
/// Creates the NCCI containers (partition key <c>/tenantId</c>) at host
/// startup when they do not exist, so a new container such as
/// <c>NcciLoadLedger</c> needs no manual provisioning step. Existing
/// containers are left untouched.
/// </summary>
internal sealed class NcciCosmosContainerInitializer : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NcciCosmosContainerInitializer> _logger;

    public NcciCosmosContainerInitializer(
        IServiceProvider services,
        IConfiguration configuration,
        ILogger<NcciCosmosContainerInitializer> logger)
    {
        _services = services;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var client = _services.GetService<CosmosClient>();
        if (client is null)
        {
            _logger.LogDebug("Skipping NCCI Cosmos container setup because CosmosClient is not registered.");
            return;
        }

        var names = NcciCosmosContainers.Resolve(_configuration);
        var database = client.GetDatabase(names.Database);
        foreach (var container in names.All)
        {
            try
            {
                await database.CreateContainerIfNotExistsAsync(
                    new ContainerProperties(container, NcciCosmosContainers.PartitionKeyPath),
                    cancellationToken: cancellationToken);
            }
            catch (CosmosException ex)
            {
                // Don't take the host down (claims adjudication also runs here);
                // NCCI reads/loads against a missing container fail loudly instead.
                _logger.LogError(ex,
                    "Could not ensure NCCI Cosmos container '{Container}' on database '{Database}' ({Status}).",
                    container, names.Database, ex.StatusCode);
            }
        }

        _logger.LogInformation("NCCI Cosmos containers checked on database '{Database}'.", names.Database);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
