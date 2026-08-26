using Microsoft.AspNetCore.Http;

namespace ELifeRPG.Phone.Api.Common;

/// <summary>
/// Numbers arrive as free text — players type them by hand — so parsing happens once at the edge and
/// everything downstream deals in the canonical value object.
/// </summary>
internal static class PhoneNumberBinding
{
    public static bool TryParse(string? raw, out PhoneNumber number, out IResult? problem)
    {
        if (PhoneNumber.TryParse(raw, out number))
        {
            problem = null;
            return true;
        }

        problem = Results.Problem(
            title: $"'{raw}' is not a valid phone number; expected {PhoneNumber.DigitCount} digits",
            statusCode: StatusCodes.Status400BadRequest);
        return false;
    }

    public static bool TryParseAll(IReadOnlyList<string>? raw, out List<PhoneNumber> numbers, out IResult? problem)
    {
        numbers = [];

        if (raw is null || raw.Count == 0)
        {
            problem = Results.Problem(title: "At least one recipient is required", statusCode: StatusCodes.Status400BadRequest);
            return false;
        }

        foreach (var candidate in raw)
        {
            if (!TryParse(candidate, out var number, out problem))
            {
                return false;
            }

            numbers.Add(number);
        }

        problem = null;
        return true;
    }
}
