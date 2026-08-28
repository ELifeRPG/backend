using System.Text.Json;
using System.Text.Json.Serialization;
using ELifeRPG.Phone.Domain.Devices;

namespace ELifeRPG.Phone.Infrastructure.Common;

/// <summary>
/// Persists a <see cref="PhoneNumber"/> as a bare JSON string rather than as an object wrapping its
/// backing field, keeping the stored document readable and compact — <c>"44127788"</c>, not
/// <c>{"Value":"44127788","IsEmpty":false}</c>.
///
/// This lives in Infrastructure, and is handed to Marten's serializer by
/// <c>AddPhoneInfrastructure</c>, so that <see cref="PhoneNumber"/> itself carries no serialization
/// attribute — Domain projects depend on nothing, System.Text.Json included.
///
/// Registration is load-bearing, not cosmetic. Without it System.Text.Json falls back to the
/// struct's public surface and every number silently round-trips to empty, since there is no
/// settable public member to read back into.
/// </summary>
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
