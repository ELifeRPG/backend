using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ELifeRPG.Banking.Domain;

/// <summary>
/// What a booking request sent with an <c>Idempotency-Key</c> answered, so the same request sent again
/// gets the same answer instead of a second booking. Same idea as World's <c>AppliedBatch</c>: a plain
/// Marten document, never a projection, written in the same session as the booking's events so both
/// commit or neither does.
///
/// Keyed on <see cref="BuildKey"/>'s <c>{gameServerId}:{key}</c>, never the raw key, so two game servers
/// that happen to pick the same key never see or overwrite each other's receipt.
///
/// Only successful bookings get a receipt. A refused request (insufficient balance, not authorized,
/// ...) booked nothing, so sending it again is simply evaluated again.
/// </summary>
public sealed class BookingReceipt
{
    public const int MaxKeyLength = 128;

    /// <summary>How long a key is remembered. After that the same key books again.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    /// <summary><c>{gameServerId}:{key}</c>, see <see cref="BuildKey"/>.</summary>
    public required string Id { get; init; }

    public required GameServerId GameServerId { get; init; }

    /// <summary>The key as the caller sent it.</summary>
    public required string Key { get; init; }

    /// <summary>Hash of the operation and every request field, see <see cref="ComputeFingerprint"/>. A different one under the same key is a reused key.</summary>
    public required string Fingerprint { get; init; }

    public required BankTransactionId TransactionId { get; init; }

    public required decimal Amount { get; init; }

    public required decimal Fee { get; init; }

    public required decimal NewBalance { get; init; }

    public required DateTimeOffset BookedAt { get; init; }

    public static bool IsValidKey(string? key) => key is { Length: > 0 and <= MaxKeyLength };

    public static string BuildKey(GameServerId gameServerId, string key) => $"{gameServerId.Value}:{key}";

    public bool IsExpired(DateTimeOffset now) => now - BookedAt > Retention;

    /// <summary>
    /// SHA-256 over the operation name and the request's fields, each in an invariant form: decimals with
    /// two places (so 10 and 10.00 match), Guids in "D" format, null as an empty field.
    /// </summary>
    public static string ComputeFingerprint(string operation, params object?[] fields)
    {
        var text = string.Join('\u001f', fields.Select(Format).Prepend(operation));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

        static string Format(object? field) => field switch
        {
            null => string.Empty,
            decimal amount => amount.ToString("0.00", CultureInfo.InvariantCulture),
            Guid id => id.ToString("D"),
            string text => text,
            _ => throw new ArgumentException($"Unsupported fingerprint field type {field.GetType().Name}."),
        };
    }
}
