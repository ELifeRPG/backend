using ELifeRPG.Shared.Kernel;
using Marten;

namespace ELifeRPG.Shared.Infrastructure;

public static class MartenStronglyTypedIdExtensions
{
    private static readonly Type[] SharedKernelIds =
    [
        typeof(AccountId),
        typeof(BankAccountId),
        typeof(CharacterId),
        typeof(CompanyId),
        typeof(GameServerId),
        typeof(ItemId),
        typeof(ItemInstanceId),
    ];

    /// <summary>
    /// Registers the Shared.Kernel ids, plus <paramref name="moduleIds"/> (the ids the module itself
    /// defines), as Marten value types, so LINQ filters on them translate to the stored shape: a
    /// plain JSON value, read as <c>data ->> 'OwnerCharacterId'</c>.
    ///
    /// Every store must call this. Without it, whether a filter like
    /// <c>x.OwnerCharacterId!.Value.Value == id</c> works depends on what else the process did
    /// first: Marten only learns a type is a value type when some store maps a document identified
    /// by it, and that knowledge is process-wide. Until then the filter compiles to
    /// <c>data -> 'OwnerCharacterId' -> 'Value' ->> 'Value'</c>, which is always null, and the query
    /// silently returns nothing (#203: listing bank accounts by owner).
    /// </summary>
    public static void RegisterStronglyTypedIds(this StoreOptions options, params Type[] moduleIds)
    {
        foreach (var type in SharedKernelIds.Concat(moduleIds))
        {
            options.RegisterValueType(type);
        }
    }
}
