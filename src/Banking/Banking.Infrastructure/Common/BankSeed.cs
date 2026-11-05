using ELifeRPG.Banking.Domain;
using ELifeRPG.Banking.Domain.Events;
using Marten;
using Marten.Exceptions;
using Marten.Schema;

namespace ELifeRPG.Banking.Infrastructure.Common;

/// <summary>
/// The precooked banks. Players never open a bank — server owners provide them — so they are seeded
/// here at store initialisation rather than through a command. Each bank keeps a fixed id, so
/// re-running is a no-op and account streams can reference it across restarts.
///
/// Opening a bank through a web UI is not planned yet; until then, adding a bank means adding a
/// <see cref="BankOpened"/> to <see cref="Banks"/>.
/// </summary>
public sealed class BankSeed : IInitialData
{
    public static readonly BankId FederalELifeBankId = new(Guid.Parse("0f6c2b0e-5d1a-4c3e-9b8f-2a7d4e1c6b90"));

    public static readonly IReadOnlyList<BankOpened> Banks =
    [
        new(FederalELifeBankId, "Federal E-Life Bank", "FEBK", 0.20m, 0.02m),
    ];

    public async Task Populate(IDocumentStore store, CancellationToken cancellation)
    {
        foreach (var bank in Banks)
        {
            await using var session = store.LightweightSession();
            if (await session.Events.FetchStreamStateAsync(bank.Id.Value, cancellation) is not null)
            {
                continue;
            }

            session.Events.StartStream<Bank>(bank.Id.Value, bank);
            try
            {
                await session.SaveChangesAsync(cancellation);
            }
            catch (ExistingStreamIdCollisionException)
            {
                // Another instance seeded it between the check and the write.
            }
        }
    }
}
