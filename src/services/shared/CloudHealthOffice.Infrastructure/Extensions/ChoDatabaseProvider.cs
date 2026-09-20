namespace CloudHealthOffice.Infrastructure.Extensions;

/// <summary>
/// The database backend a service was wired against by <c>AddChoDatabase</c>.
/// <para>
/// Services that ship both a MongoDB and a Cosmos DB repository use the returned value to pick
/// the matching implementation, so the choice is made once, from configuration, instead of being
/// re-derived in each service.
/// </para>
/// <para>
/// Cosmos DB's MongoDB API reports as <see cref="MongoDb"/>: it is reached through the MongoDB
/// driver, so the Mongo repository is the correct implementation for it.
/// </para>
/// </summary>
public enum ChoDatabaseProvider
{
    /// <summary>MongoDB, or any MongoDB-wire-compatible service such as Cosmos DB for MongoDB. The default.</summary>
    MongoDb,

    /// <summary>Cosmos DB accessed through its native SDK. Opt-in via <c>Database:Provider=CosmosDb</c>.</summary>
    CosmosDb
}
