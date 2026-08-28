using Xunit;

// ItemCatalog.CatalogVersion is a *global* count of every raw event in the items schema
// (MartenItemRepository.GetCatalogVersionAsync), not a per-test or per-stream value — so any class
// creating an item shifts the number every other class is asserting against. Running classes in
// parallel therefore made BulkImport_WithAnEmptyPayload_SucceedsWithoutWriting ("the version did not
// move") and ItemsQuery_AfterAnotherItemIsCreated_ReportsAHigherCatalogVersion ("the version moved by
// this call") race each other, failing intermittently against a correct implementation.
//
// Same fix, and the same reasoning, as Phone.IntegrationTests' own AssemblyInfo.cs: shared global
// state that a test has to observe before and after means the classes touching it cannot overlap.
// This assembly runs in about a second, so serialising it costs nothing worth measuring.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
