using Xunit;

// Serialize this assembly's test classes.
//
// StreamingCsvTests.TenThousandRowStream_PeakMemoryBounded measures a heap
// delta with GC.GetTotalMemory, which reports the *process* heap. Under xUnit's
// default per-collection parallelism the other classes here — several of which
// boot a WebApplicationFactory host — allocate inside that measurement window,
// and their allocations are charged to the memory test.
//
// Measured on a 4-core runner, 6 runs each: parallel 12480-32713 KB (a 2.6x
// spread); serialized 12126-12140 KB (a 0.1% spread). CI, with more cores,
// observed 91 MB against the test's 50 MB bound and failed the Test Metrics
// workflow on main (run 34484048990).
//
// This attribute is load bearing for that test. Removing it makes the suite
// intermittently red. Costs ~1s on this assembly.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
