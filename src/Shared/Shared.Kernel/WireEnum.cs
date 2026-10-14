namespace ELifeRPG.Shared.Kernel;

/// <summary>
/// Parsing for enums that cross the wire as strings. Lives here, next to the shared ids, because
/// every module's Api project needs the same check and none of it touches ASP.NET — the caller still
/// builds its own response, so each endpoint keeps the problem shape it documents (World's
/// <c>retryable</c> extension, Items' empty-means-default).
/// </summary>
public static class WireEnum
{
    /// <summary>
    /// Two guards, because <see cref="Enum.TryParse{TEnum}(string?, bool, out TEnum)"/> on its own
    /// hands back values that name no member.
    ///
    /// It accepts the <i>numeric</i> form, so <c>"7"</c> arrives as <c>(TEnum)7</c>.
    /// <see cref="Enum.IsDefined{TEnum}(TEnum)"/> rejects that — but not <c>"1"</c>, which is a real
    /// member's ordinal and so passes both. That is the hole this closes: an ordinal is the
    /// <i>stored</i> form of an enum in this solution (ARCHITECTURE.md §9e), append-only and free to
    /// be renumbered behind a wipe, and letting one in over HTTP couples every client to Postgres's
    /// numbering. Phone's <c>AppKey</c> renumbering is the worked example — <c>?appKey=3</c> silently
    /// changed which app it meant. So a name is required: no member name can begin with a digit or a
    /// sign, which makes the leading character enough to tell the two apart.
    ///
    /// The halves were once copied to eight call sites and one shipped without the second.
    /// </summary>
    public static bool TryParse<TEnum>(string? raw, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;

        var trimmed = raw?.TrimStart();
        if (string.IsNullOrEmpty(trimmed) || char.IsAsciiDigit(trimmed[0]) || trimmed[0] is '+' or '-')
        {
            return false;
        }

        return Enum.TryParse(trimmed, ignoreCase: true, out value) && Enum.IsDefined(value);
    }

    /// <summary>
    /// The 400's message, naming the members rather than restating them so it cannot drift from the
    /// enum. <paramref name="field"/> is the name as it appears on the wire (<c>"ownerType"</c>,
    /// <c>"deletes[].reason"</c>), not the C# member.
    /// </summary>
    public static string MustBeOneOf<TEnum>(string field)
        where TEnum : struct, Enum
        => $"{field} must be one of: {string.Join(", ", Enum.GetNames<TEnum>())}";
}
