using CareManagementService.Models;
using CareManagementService.Repositories;
using MongoDB.Driver;

namespace CareManagementService.HostedServices;

/// <summary>
/// Creates the care-case indexes once at startup, so the first request does
/// not pay for them and the case-number uniqueness constraint is in place
/// before anything is written.
/// </summary>
public sealed class CareCaseIndexInitializer(
    IMongoDatabase database,
    IConfiguration configuration,
    CareCaseIndexes indexes,
    ILogger<CareCaseIndexInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var collectionName = configuration["MongoDb:CareCasesCollection"] ?? "CareCases";

        try
        {
            await indexes.EnsureAsync(database.GetCollection<CareCase>(collectionName), ct);
        }
        catch (Exception ex)
        {
            // Startup continues: the repository ensures indexes on its first
            // write too, so a database that is slow to come up does not stop
            // the host from reaching Ready.
            logger.LogWarning(ex, "Care case indexes could not be created at startup; the first write will retry.");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
