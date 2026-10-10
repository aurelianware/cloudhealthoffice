// Shared by every test project that exercises a Cosmos DB repository against
// a real Cosmos DB (the Linux "vnext" emulator in CI). Linked into each
// project with <Compile Include="..\Shared\CosmosEmulator\*.cs" />.
//
// Local runs: start the emulator and the tests run; without it they are
// skipped. CI sets COSMOS_EMULATOR_REQUIRED=true so an emulator that never
// came up fails the job instead of skipping every test.
//
//   docker run -d -p 8081:8081 \
//     mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-EN20261008
//   dotnet test --filter Category=Cosmos
//
// Probed on vnext-EN20261008 (gateway/HTTP, the only mode it serves):
//   supported  conditional patch FilterPredicate (NOT IN, IS_DEFINED/IS_NULL,
//              nested paths, string comparison; 412 on mismatch, 404 on a
//              missing item), IfMatchEtag replace/delete (412), create
//              conflict (409), transactional batch incl. rollback, change
//              feed (latest version), cross-partition ORDER BY / OFFSET LIMIT,
//              SUM / MIN / COUNT, GROUP BY, TOP @param.
//   gap        ARRAY_LENGTH inside a patch FilterPredicate: 400 "The filter
//              predicate could not be evaluated" (it works in queries).
using System.Net;
using System.Net.Sockets;
using Microsoft.Azure.Cosmos;
using Xunit;

namespace CloudHealthOffice.Testing.Cosmos;

/// <summary>Settings and conventions every Cosmos emulator test uses.</summary>
public static class CosmosEmulator
{
    /// <summary>Trait value: <c>[Trait("Category", CosmosEmulator.Category)]</c>; select with <c>--filter Category=Cosmos</c>.</summary>
    public const string Category = "Cosmos";

    public const string EndpointVariable = "COSMOS_EMULATOR_ENDPOINT";
    public const string KeyVariable = "COSMOS_EMULATOR_KEY";
    public const string RequiredVariable = "COSMOS_EMULATOR_REQUIRED";
    public const string WaitSecondsVariable = "COSMOS_EMULATOR_WAIT_SECONDS";

    /// <summary>The emulator's fixed, publicly documented account key (not a secret).</summary>
    public const string WellKnownKey =
        "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    public static string Endpoint =>
        Environment.GetEnvironmentVariable(EndpointVariable) is { Length: > 0 } e ? e : "http://localhost:8081/";

    public static string Key =>
        Environment.GetEnvironmentVariable(KeyVariable) is { Length: > 0 } k ? k : WellKnownKey;

    public static bool Required =>
        string.Equals(Environment.GetEnvironmentVariable(RequiredVariable), "true", StringComparison.OrdinalIgnoreCase)
        || Environment.GetEnvironmentVariable(RequiredVariable) == "1";

    /// <summary>How long a required run keeps retrying the connection (default 120 s).</summary>
    public static TimeSpan RequiredWait =>
        int.TryParse(Environment.GetEnvironmentVariable(WaitSecondsVariable), out var s) && s >= 0
            ? TimeSpan.FromSeconds(s)
            : TimeSpan.FromSeconds(120);
}

/// <summary>
/// One emulator connection check per test assembly, shared by every class in
/// the <see cref="CosmosEmulatorCollection"/>. Tests isolate themselves with a
/// uniquely named database (<see cref="CreateDatabaseAsync"/>) that is deleted
/// when the fixture is disposed, or with unique tenant ids when the code under
/// test hard-codes its database name.
/// </summary>
public sealed class CosmosEmulatorFixture : IAsyncLifetime
{
    public const string CollectionName = "CosmosEmulator";

    private readonly List<CosmosClient> _clients = new();
    private readonly List<(CosmosClient Client, string Id)> _databases = new();
    private readonly object _gate = new();

    public bool Available { get; private set; }

    public string? UnavailableReason { get; private set; }

    public async Task InitializeAsync()
    {
        var required = CosmosEmulator.Required;
        // CI waits for the service container's health check first; this is a
        // backstop for a gateway that reports ready a moment before it is.
        var deadline = DateTime.UtcNow + (required ? CosmosEmulator.RequiredWait : TimeSpan.Zero);
        Exception? last = null;
        do
        {
            try
            {
                if (!await PortIsOpenAsync(new Uri(CosmosEmulator.Endpoint)))
                    throw new InvalidOperationException("nothing is listening");
                using var client = NewClient(null, null);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await client.ReadAccountAsync().WaitAsync(cts.Token);
                Available = true;
                return;
            }
            catch (Exception ex)
            {
                last = ex;
            }
            if (DateTime.UtcNow < deadline) await Task.Delay(TimeSpan.FromSeconds(3));
        } while (DateTime.UtcNow < deadline);

        UnavailableReason =
            $"Cosmos DB emulator not reachable at {CosmosEmulator.Endpoint} ({last?.GetType().Name}: {last?.Message.Split('\n')[0]}). " +
            $"Start it, or set {CosmosEmulator.EndpointVariable}.";
        if (required)
        {
            // CI must never silently skip the Cosmos suite.
            throw new InvalidOperationException(
                $"{CosmosEmulator.RequiredVariable}=true but {UnavailableReason}", last);
        }
    }

    /// <summary>Call first in every test (or its <c>InitializeAsync</c>); needs <c>[SkippableFact]</c>.</summary>
    public void SkipIfUnavailable() => Skip.IfNot(Available, UnavailableReason);

    /// <summary>
    /// A client for the emulator configured the way the service under test
    /// configures its own: pass the service's <paramref name="serializer"/>
    /// (System.Text.Json services) or <paramref name="serializerOptions"/>
    /// (services on the SDK's default Newtonsoft serializer).
    /// </summary>
    public CosmosClient CreateClient(CosmosSerializer? serializer = null, CosmosSerializationOptions? serializerOptions = null)
    {
        SkipIfUnavailable();
        var client = NewClient(serializer, serializerOptions);
        lock (_gate) _clients.Add(client);
        return client;
    }

    /// <summary>A new database <c>{prefix}_{guid}</c>, deleted when the run ends.</summary>
    public async Task<Database> CreateDatabaseAsync(CosmosClient client, string prefix)
    {
        var id = $"{(prefix.Length > 30 ? prefix[..30] : prefix)}_{Guid.NewGuid():N}";
        var database = (await client.CreateDatabaseAsync(id)).Database;
        lock (_gate) _databases.Add((client, id));
        return database;
    }

    /// <summary>Creates (or reuses) a container partitioned on <paramref name="partitionKeyPath"/>.</summary>
    public static async Task<Container> CreateContainerAsync(
        Database database, string name, string partitionKeyPath = "/tenantId")
    {
        var response = await database.CreateContainerIfNotExistsAsync(new ContainerProperties(name, partitionKeyPath));
        return response.Container;
    }

    public async Task DisposeAsync()
    {
        foreach (var (client, id) in _databases)
        {
            try
            {
                await client.GetDatabase(id).DeleteAsync();
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
            }
        }
        foreach (var client in _clients) client.Dispose();
    }

    private static CosmosClient NewClient(CosmosSerializer? serializer, CosmosSerializationOptions? serializerOptions)
    {
        var options = new CosmosClientOptions
        {
            // The emulator serves the gateway protocol only, on one endpoint.
            ConnectionMode = ConnectionMode.Gateway,
            LimitToEndpoint = true,
            RequestTimeout = TimeSpan.FromSeconds(30),
            MaxRetryAttemptsOnRateLimitedRequests = 20,
        };
        if (serializer is not null) options.Serializer = serializer;
        else if (serializerOptions is not null) options.SerializerOptions = serializerOptions;
        return new CosmosClient(CosmosEmulator.Endpoint, CosmosEmulator.Key, options);
    }

    private static async Task<bool> PortIsOpenAsync(Uri endpoint)
    {
        try
        {
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await tcp.ConnectAsync(endpoint.Host, endpoint.Port, cts.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>
/// Not parallel with any other collection: some suites shared with the Mongo
/// runs count process-wide metrics, and the emulator is one shared server.
/// </summary>
[CollectionDefinition(CosmosEmulatorFixture.CollectionName, DisableParallelization = true)]
public sealed class CosmosEmulatorCollection : ICollectionFixture<CosmosEmulatorFixture>
{
}
