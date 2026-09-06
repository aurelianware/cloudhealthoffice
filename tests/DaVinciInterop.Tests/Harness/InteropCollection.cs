namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// Forces every external interoperability scenario to run one at a time.
///
/// Scenarios are not independent of each other at the infrastructure level: they
/// share one Docker Compose project name and one set of published host ports, and
/// they all write the same run.json. Run in parallel — xUnit's default across
/// test classes — two scenarios would fight over the same container and the same
/// evidence file, and the failure would look like a flaky external implementation
/// rather than the harness contending with itself.
///
/// Every scenario class carries <c>[Collection(InteropCollection.Name)]</c>.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class InteropCollection
{
    public const string Name = "DaVinciInterop external scenarios";
}
