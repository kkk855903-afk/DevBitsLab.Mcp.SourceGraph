using Xunit;

// Disable cross-class test parallelization for the integration suite.
//
// Every integration test spawns a server child process via `ServerHarness.StartAsync`,
// which calls `TryClearSourcegraphDir(serveArgs)` to wipe the `.sourcegraph/` directory
// next to the `--solution` path. When multiple test classes run in parallel against the
// SAME solution fixture (e.g. InitializeTests, ColdIndexTests, MultiScopeTests all use
// `Sample.sln`), one class's wipe can race with another class's mid-cold-index state and
// crash the spawned server. macOS CI hit this; Ubuntu got lucky on timing.
//
// Serializing the integration suite costs single-digit seconds of wall-clock and removes
// the race entirely. A per-test temp-dir copy of each fixture would be a more durable
// fix; deferred until it's worth the complexity.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
