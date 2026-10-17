namespace CloudHealthOffice.Infrastructure.Extensions;

/// <summary>
/// The database backend a service was wired against by <c>AddChoDatabase</c>. MongoDB
/// (including Azure DocumentDB, reached through the MongoDB driver) is the only one
/// (ADR 017); <c>AddChoDatabase</c> returns null when no database is configured.
/// </summary>
public enum ChoDatabaseProvider
{
    /// <summary>MongoDB, or a MongoDB-wire-compatible service such as Azure DocumentDB.</summary>
    MongoDb,
}
