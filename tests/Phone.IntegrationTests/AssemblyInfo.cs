using Xunit;

// HiveSettings is a hive-wide singleton, and several tests lower and restore a phone cap through it
// — the per-minute quota, the contact limit, the retention limit, the group size. Running classes in
// parallel would let those leak into other classes' sends.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
