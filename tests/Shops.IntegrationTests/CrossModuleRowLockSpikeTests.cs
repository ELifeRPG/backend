using ELifeRPG.Accounts.Application.Sessions;
using ELifeRPG.Accounts.Domain;
using ELifeRPG.Banking.Application.BankAccounts;
using ELifeRPG.Banking.Application.Banks;
using ELifeRPG.Characters.Application.Characters;
using ELifeRPG.Items.Application.Items;
using ELifeRPG.Shared.Integration;
using ELifeRPG.Shared.Integration.Abstractions;
using ELifeRPG.Shared.Kernel;
using ELifeRPG.Shops.Application.Common;
using ELifeRPG.Shops.Application.Shops;
using ELifeRPG.Shops.Domain;
using ELifeRPG.Shops.Infrastructure.Common;
using Mediator;
using Marten.Services;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace ELifeRPG.Shops.IntegrationTests;

/// <summary>
/// GO/NO-GO SPIKE for Task 1c: Postgres row-locking as the cross-module concurrency mechanism.
/// Tasks 1 and 1b found that Marten's optimistic-concurrency machinery (both FetchForWriting and
/// explicit version-checked appends) doesn't work on ForTransaction-bound sessions due to broken
/// version tracking at the Marten/JasperFx level. This spike tests a Postgres-native alternative:
/// acquire a row-level lock (SELECT ... FOR UPDATE) directly against the shared NpgsqlTransaction
/// before loading/mutating the aggregate, then use a plain, unversioned Events.Append.
///
/// The row lock serializes writes at the Postgres level, bypassing Marten's broken version machinery
/// entirely while still preventing concurrent modifications. This spike tests (i) the happy path
/// (no false-positive when no contention), and (ii) true concurrent-access serialization when two
/// transactions race for the same listing.
/// </summary>
public sealed class CrossModuleRowLockSpikeTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public Task InitializeAsync()
    {
        _provider = TestServices.BuildProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
    }

    [Fact]
    public async Task ForTransactionBoundSession_RowLockThenPlainAppend_HappyPathPersists()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var listingId = await SeedListingAsync(mediator, stock: 10);

        var transactionFactory = scope.ServiceProvider.GetRequiredService<ICrossModuleTransactionFactory>();
        var store = scope.ServiceProvider.GetRequiredService<IShopsStore>();

        await using (var transaction = await transactionFactory.BeginAsync(CancellationToken.None))
        {
            var npgsqlTransaction = transaction.Handle.Unwrap();

            // Acquire a row-level lock on the ShopListing doc row.
            await AcquireRowLockAsync(npgsqlTransaction, listingId.Value);

            var options = SessionOptions.ForTransaction(npgsqlTransaction, shouldAutoCommit: false);
            await using var session = store.OpenSession(options);

            // Load the aggregate and perform the domain operation.
            var listing = await session.LoadAsync<ShopListing>(listingId, CancellationToken.None);
            Assert.NotNull(listing);

            var domainEvent = listing!.Purchase(3);

            // Plain, unversioned append (bypassing Marten's broken version tracking).
            session.Events.Append(listingId.Value, domainEvent);
            await session.SaveChangesAsync(CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Verify the change persisted correctly.
        var listingRepository = scope.ServiceProvider.GetRequiredService<IShopListingRepository>();
        var reloaded = await listingRepository.FindByIdAsync(listingId, CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.Equal(7, reloaded!.Stock);
    }

    [Fact]
    public async Task ForTransactionBoundSession_RowLockThenPlainAppend_ConcurrentAttemptsSerializeCorrectly()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var listingId = await SeedListingAsync(mediator, stock: 1);

        var transactionFactory = scope.ServiceProvider.GetRequiredService<ICrossModuleTransactionFactory>();
        var store = scope.ServiceProvider.GetRequiredService<IShopsStore>();
        var listingRepository = scope.ServiceProvider.GetRequiredService<IShopListingRepository>();

        // Two concurrent attempts to purchase the same listing (only 1 unit available).
        // One should succeed, the other should either:
        // a) Detect insufficient stock after acquiring the lock and release it, or
        // b) Fail with an error because the stock was consumed by the first transaction
        int successCount = 0;
        Exception? transaction1Exception = null;
        Exception? transaction2Exception = null;

        var task1 = Task.Run(async () =>
        {
            try
            {
                await using var tx = await transactionFactory.BeginAsync(CancellationToken.None);
                var npgsqlTx = tx.Handle.Unwrap();

                await AcquireRowLockAsync(npgsqlTx, listingId.Value);

                var options = SessionOptions.ForTransaction(npgsqlTx, shouldAutoCommit: false);
                await using var session = store.OpenSession(options);

                var listing = await session.LoadAsync<ShopListing>(listingId, CancellationToken.None);
                Assert.NotNull(listing);

                // This will succeed or throw depending on current stock.
                var domainEvent = listing!.Purchase(1);

                session.Events.Append(listingId.Value, domainEvent);
                await session.SaveChangesAsync(CancellationToken.None);
                await tx.CommitAsync(CancellationToken.None);

                Interlocked.Increment(ref successCount);
            }
            catch (Exception ex)
            {
                transaction1Exception = ex;
            }
        });

        var task2 = Task.Run(async () =>
        {
            try
            {
                await using var tx = await transactionFactory.BeginAsync(CancellationToken.None);
                var npgsqlTx = tx.Handle.Unwrap();

                await AcquireRowLockAsync(npgsqlTx, listingId.Value);

                var options = SessionOptions.ForTransaction(npgsqlTx, shouldAutoCommit: false);
                await using var session = store.OpenSession(options);

                var listing = await session.LoadAsync<ShopListing>(listingId, CancellationToken.None);
                Assert.NotNull(listing);

                // This will succeed or throw depending on current stock.
                var domainEvent = listing!.Purchase(1);

                session.Events.Append(listingId.Value, domainEvent);
                await session.SaveChangesAsync(CancellationToken.None);
                await tx.CommitAsync(CancellationToken.None);

                Interlocked.Increment(ref successCount);
            }
            catch (Exception ex)
            {
                transaction2Exception = ex;
            }
        });

        await Task.WhenAll(task1, task2);

        // Exactly one purchase should have succeeded (stock was 1).
        Assert.Equal(1, successCount);

        // Verify final state: stock should be 0 (one purchase succeeded) and neither is negative.
        var reloaded = await listingRepository.FindByIdAsync(listingId, CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.Equal(0, reloaded!.Stock);
        Assert.True(reloaded!.Stock >= 0, "Stock should never go negative");
    }

    /// <summary>
    /// Acquire a Postgres row-level lock on the ShopListing document row. Filters by the table's
    /// primary key (`id` alone — tenancy removed, see ARCHITECTURE.md §9e gotcha 9) — production
    /// code (see MartenShopListingRepository.ReserveStockAsync) mirrors this exactly.
    /// </summary>
    private static async Task AcquireRowLockAsync(NpgsqlTransaction transaction, Guid listingId)
    {
        var connection = transaction.Connection;
        Assert.NotNull(connection);

        await using var cmd = connection!.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT id FROM shops.mt_doc_shoplisting WHERE id = @id FOR UPDATE";
        cmd.Parameters.AddWithValue("@id", listingId);

        await cmd.ExecuteScalarAsync(CancellationToken.None);
    }

    private async Task<ShopListingId> SeedListingAsync(IMediator mediator, int stock)
    {
        var sellerId = await CreateCharacterAsync(mediator);
        var payoutAccountId = await OpenPersonalBankAccountAsync(mediator, sellerId);
        var openResult = await mediator.Send(new OpenShopCommand(ShopOwnerType.Personal, sellerId, null, "Spike Test Shop", payoutAccountId));
        Assert.True(openResult is OpenShopResult.Opened, $"Expected Opened, got {openResult}");
        if (openResult is not OpenShopResult.Opened opened)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        var itemResult = await mediator.Send(new CreateItemCommand("Spike Item", $"ELRPG_Test_Spike_{Guid.NewGuid():N}"));
        Assert.True(itemResult is CreateItemResult.Created, $"Expected Created, got {itemResult}");
        if (itemResult is not CreateItemResult.Created item)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        var addResult = await mediator.Send(new AddListingCommand(opened.ShopId, item.ItemId, 5m, stock, sellerId));
        Assert.True(addResult is AddListingResult.Added, $"Expected Added, got {addResult}");
        if (addResult is not AddListingResult.Added added)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        return added.ListingId;
    }

    private async Task<CharacterId> CreateCharacterAsync(IMediator mediator)
    {
        // Accounts come from portal signup now, not from joining the gameserver.
        AccountId accountId;
        using (var scope = _provider.CreateScope())
        {
            accountId = (await TestAccounts.CreateAsync(scope.ServiceProvider)).Id;
        }

        var result = await mediator.Send(new CreateCharacterCommand(accountId, "Spike Test Character"));
        Assert.True(result is CreateCharacterResult.Created, $"Expected Created, got {result}");
        if (result is not CreateCharacterResult.Created created)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        return created.CharacterId;
    }

    private async Task<BankAccountId> OpenPersonalBankAccountAsync(IMediator mediator, CharacterId characterId)
    {
        var bank = await mediator.Send(new OpenBankCommand("Spike Test Bank", 0.20m, 0.02m));
        var result = await mediator.Send(new OpenBankAccountCommand(bank.Id, characterId));
        Assert.True(result is OpenBankAccountResult.Opened, $"Expected Opened, got {result}");
        if (result is not OpenBankAccountResult.Opened opened)
        {
            throw new InvalidOperationException("Unreachable.");
        }

        return opened.BankAccountId;
    }
}
