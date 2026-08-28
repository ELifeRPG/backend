using System.Text.Json;
using System.Text.Json.Serialization;
using ELifeRPG.Phone.Domain.Exceptions;

namespace ELifeRPG.Phone.Domain.Devices;

/// <summary>
/// A subscriber number. It is minted with the phone and never moves: there is no card to carry it
/// to another handset, so the number and the device are one identity.
///
/// The canonical form is bare digits. Players type numbers by hand into the Messages app, so
/// <see cref="Parse"/> tolerates the punctuation people actually use and strips it — two spellings
/// of the same number must not key two different threads.
///
/// Serialised as a bare JSON string via <see cref="PhoneNumberJsonConverter"/> rather than as an
/// object, so Marten can put a unique index directly on <c>PhoneDevice.Number</c>.
/// </summary>
[JsonConverter(typeof(PhoneNumberJsonConverter))]
public readonly record struct PhoneNumber
{
    public const int DigitCount = 8;

    private readonly string? _value;

    private PhoneNumber(string value)
    {
        _value = value;
    }

    /// <summary>
    /// Empty for <c>default(PhoneNumber)</c>, which <see cref="Parse"/> never produces but Marten
    /// can materialise for a field written before it was assigned. Empty rather than throwing
    /// matches how the other aggregates treat unset strings, and never matches a real number.
    /// </summary>
    public string Value => _value ?? string.Empty;

    public bool IsEmpty => string.IsNullOrEmpty(_value);

    public static PhoneNumber Parse(string? raw)
    {
        if (!TryParse(raw, out var number))
        {
            throw new InvalidPhoneNumberException($"'{raw}' is not a valid phone number; expected {DigitCount} digits.");
        }

        return number;
    }

    public static bool TryParse(string? raw, out PhoneNumber number)
    {
        number = default;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        Span<char> digits = stackalloc char[DigitCount];
        var length = 0;
        var sawPlus = false;

        foreach (var candidate in raw)
        {
            switch (candidate)
            {
                case >= '0' and <= '9':
                    if (length == DigitCount)
                    {
                        return false;
                    }

                    digits[length++] = candidate;
                    continue;

                // A single leading '+' is tolerated because that is how a number gets read aloud and
                // written down; a second one means the input is malformed, not merely decorated.
                case '+' when !sawPlus && length == 0:
                    sawPlus = true;
                    continue;

                case ' ' or '-' or '(' or ')' or '/' or '.':
                    continue;

                default:
                    return false;
            }
        }

        if (length != DigitCount)
        {
            return false;
        }

        number = new PhoneNumber(new string(digits));
        return true;
    }

    public override string ToString() => Value;
}

internal sealed class PhoneNumberJsonConverter : JsonConverter<PhoneNumber>
{
    public override PhoneNumber Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();

        // Deliberately lenient: a stored value that no longer parses must not make a whole stream
        // unreadable. Parse guards the write path; this is the read path.
        return string.IsNullOrEmpty(raw) ? default : TryParseStored(raw);
    }

    public override void Write(Utf8JsonWriter writer, PhoneNumber value, JsonSerializerOptions options)
    {
        if (value.IsEmpty)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(value.Value);
    }

    private static PhoneNumber TryParseStored(string raw) =>
        PhoneNumber.TryParse(raw, out var number) ? number : default;
}
