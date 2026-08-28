using ELifeRPG.World.Domain.Exceptions;

namespace ELifeRPG.World.Domain.Items;

/// <summary>
/// A capped, freeform bag for the properties that don't earn their own typed field — a serial
/// number, a paint scheme, a lock combination. <see cref="ItemInstance.Durability"/> and
/// <see cref="ItemInstance.Ammo"/> are the two values that actually churn and are first-class typed
/// fields instead; everything else lives here.
///
/// Validated on construction via <see cref="Create"/> so an oversized bag can never reach storage.
/// <see cref="Empty"/> and JSON deserialization bypass validation deliberately: an already-persisted
/// row was valid when it was written (or was <see cref="Empty"/>, trivially valid), and re-validating
/// every load buys nothing. Never indexed — see the phase 1 plan's infrastructure notes.
/// </summary>
public sealed class ItemAttributes
{
    public const int MaxKeys = 16;
    public const int MaxKeyLength = 64;
    public const int MaxValueLength = 256;

    public static readonly ItemAttributes Empty = new();

    public IReadOnlyDictionary<string, string> Values { get; private set; } = new Dictionary<string, string>();

    public static ItemAttributes Create(IReadOnlyDictionary<string, string> values)
    {
        Validate(values);
        return new ItemAttributes { Values = new Dictionary<string, string>(values) };
    }

    private static void Validate(IReadOnlyDictionary<string, string> values)
    {
        if (values.Count > MaxKeys)
        {
            throw new AttributeLimitExceededException(
                $"An item instance may carry at most {MaxKeys} attributes; {values.Count} were supplied.");
        }

        foreach (var (key, value) in values)
        {
            // Backstop, not the primary gate (review round 3): System.Text.Json can deserialize a
            // JSON null as a dictionary *value* (a JSON object key is always a string by grammar, so
            // only the value side needs this) even though this dictionary's declared type is
            // IReadOnlyDictionary<string, string> with no nullable annotation on the value. The
            // primary gate is WorldModule.TryParseApplySnapshotCommand, which rejects this at the
            // parse layer before a command is ever built — this check exists so that the *next*
            // parse-layer leaf someone misses degrades to a per-instance AttributeLimit rejection
            // through the catch already around this call in ApplySnapshotCommand.cs, rather than an
            // unhandled 500. Every domain constructor on this path should hold this same property;
            // see the task 5 report for which others were checked and found already safe.
            if (value is null)
            {
                throw new AttributeLimitExceededException($"Attribute value for key '{key}' must not be null.");
            }

            if (key.Length > MaxKeyLength)
            {
                throw new AttributeLimitExceededException(
                    $"Attribute key '{key}' is {key.Length} characters, exceeding the {MaxKeyLength}-character limit.");
            }

            if (value.Length > MaxValueLength)
            {
                throw new AttributeLimitExceededException(
                    $"Attribute value for key '{key}' is {value.Length} characters, exceeding the {MaxValueLength}-character limit.");
            }
        }
    }
}
