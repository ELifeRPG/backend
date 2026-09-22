namespace ELifeRPG.Shared.Kernel;

/// <summary>
/// Parsing for enums that cross the wire as strings. Lives here, next to the shared ids, because
/// every module's Api project needs the same two-part check and none of it touches ASP.NET — the
/// caller still builds its own response, so each endpoint keeps the problem shape it documents
/// (World's <c>retryable</c> extension, Items' empty-means-default).
/// </summary>
public static class WireEnum
{
    /// <summary>
    /// <see cref="Enum.TryParse{TEnum}(string?, bool, out TEnum)"/> also accepts the <i>numeric</i>
    /// form, so on its own it will hand back a value that names no member — <c>"7"</c> as
    /// <c>(TEnum)7</c>. <see cref="Enum.IsDefined{TEnum}(TEnum)"/> is what rejects that, and pairing
    /// the two is the only correct form. It lives in one place because the halves were previously
    /// copied to eight call sites and one of them shipped without the second half.
    /// </summary>
    public static bool TryParse<TEnum>(string? raw, out TEnum value)
        where TEnum : struct, Enum
        => Enum.TryParse(raw, ignoreCase: true, out value) && Enum.IsDefined(value);

    /// <summary>
    /// The 400's message, naming the members rather than restating them so it cannot drift from the
    /// enum. <paramref name="field"/> is the name as it appears on the wire (<c>"ownerType"</c>,
    /// <c>"deletes[].reason"</c>), not the C# member.
    /// </summary>
    public static string MustBeOneOf<TEnum>(string field)
        where TEnum : struct, Enum
        => $"{field} must be one of: {string.Join(", ", Enum.GetNames<TEnum>())}";
}
