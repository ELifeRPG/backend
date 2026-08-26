using Xunit;

// HiveSettings is a hive-wide singleton, and the rate-limit test has to lower and restore the
// per-minute cap. Running classes in parallel would let that leak into other classes' sends.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
