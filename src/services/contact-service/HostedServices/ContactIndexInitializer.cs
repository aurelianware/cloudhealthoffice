using ContactService.Models;
using ContactService.Repositories;
using MongoDB.Driver;

namespace ContactService.HostedServices;

/// <summary>
/// Creates the contact and reason indexes once at startup, so the first
/// request does not pay for them and the uniqueness constraints are in place
/// before anything is written.
/// </summary>
public sealed class ContactIndexInitializer(
    IMongoDatabase database,
    IConfiguration configuration,
    ContactIndexes contactIndexes,
    ContactReasonIndexes reasonIndexes,
    ILogger<ContactIndexInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var contacts = configuration["MongoDb:ContactsCollection"] ?? "Contacts";
        var reasons = configuration["MongoDb:ContactReasonsCollection"] ?? "ContactReasons";

        try
        {
            await contactIndexes.EnsureAsync(database.GetCollection<Contact>(contacts), ct);
            await reasonIndexes.EnsureAsync(database.GetCollection<ContactReason>(reasons), ct);
        }
        catch (Exception ex)
        {
            // Startup continues: the repositories ensure indexes on their
            // first write too, so a database that is slow to come up does not
            // stop the host from reaching Ready.
            logger.LogWarning(ex, "Contact indexes could not be created at startup; the first write will retry.");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
