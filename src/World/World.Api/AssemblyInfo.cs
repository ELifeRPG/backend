using System.Runtime.CompilerServices;

// Review round 3's ruling, and it is narrower than it looks. Three findings across this phase — task
// 1's `retryable` problem-detail flag, task 3's result-to-IResult mapping, and task 4's malformed-scope
// rejection — were all pure functions of a DTO or a union value, with no HTTP, DI or database
// dependence. The only thing stopping any of them being tested directly was `private static`.
//
// So this opens exactly that seam rather than standing up a WebApplicationFactory harness: two members
// of WorldModule become `internal` (TryParseApplySnapshotCommand and ToProblemOrOk) and the integration
// test project can assert on them as the plain functions they already are. No public API changes, no
// change to the endpoint's shape, and no host to start — which matters, since the Api host does not
// start cleanly offline. A full host harness stays a separate, phase-sized decision; it would not have
// caught any of the three earlier than this does.
[assembly: InternalsVisibleTo("ELifeRPG.World.IntegrationTests")]
