namespace ELifeRPG.Items.Domain.Exceptions;

/// <summary>
/// Raised when a write would leave two catalog entries claiming one prefab class name. The handler's
/// read-then-write check catches the ordinary case; this covers the race between two concurrent
/// creates, where the unique index is the only thing left standing. Translated from Postgres 23505
/// by the repository and mapped to a result union case by the handler — never surfaced as a 500.
/// </summary>
public sealed class DuplicatePrefabClassNameException(string prefabClassName)
    : Exception($"Another catalog entry already claims the prefab class name '{prefabClassName}'.")
{
    public string PrefabClassName { get; } = prefabClassName;
}
