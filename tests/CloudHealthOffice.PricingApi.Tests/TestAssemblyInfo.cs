// Program.cs configures Serilog's static bootstrap logger and swallows startup
// exceptions; two test hosts starting at once in one process race on it and one
// fails with "The entry point exited without ever building an IHost". Host-based
// test classes therefore run one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
