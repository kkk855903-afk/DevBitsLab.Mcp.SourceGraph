using Xunit;

// Disable cross-class test parallelization for the unit-test assembly.
//
// Several test classes in this assembly mutate SQLite connection-pool state via
// `SqliteConnection.ClearAllPools()` — a process-global operation needed when a test
// is about to mutate a DB file (e.g. corrupt its bytes mid-test) and must release any
// pooled handles first. With xUnit's default parallel-by-class scheduling, ClearAllPools
// can fire on one thread while another class's test is mid-`Dapper.QueryAsync` on a
// pooled connection in another thread; the pool yanks the handle out from under the
// query and the test host segfaults.
//
// Within-class test parallelism is still enabled (xUnit's default), and the integration
// suite is a separate assembly that runs in its own process — neither is affected by
// this attribute. Total wall-clock cost is small: the unit suite runs serially in
// ~60s vs ~50s parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
