namespace ELifeRPG.Banking.Domain;

/// <summary>
/// The account number layout, modelled on the Dutch IBAN: <c>EL</c>, two MOD-97 check digits, the
/// bank's four-letter code, and ten account digits — e.g. <c>EL90FEBK0123456789</c>, printed in
/// groups of four as <c>EL90 FEBK 0123 4567 89</c>. The check digits are calculated exactly as for a
/// real IBAN, so a mistyped number is caught before anything looks it up.
///
/// The canonical form is compact and upper-case; that is what is stored, indexed and returned.
/// Grouping into fours is a display concern left to clients. Numbers stay plain strings rather than
/// a value object: the stored field is already a string Marten can index directly, so a struct would
/// only add a serializer converter and a duplicated index field (see Phone's PhoneNumber).
/// </summary>
public static class BankAccountNumber
{
    public const string Prefix = "EL";

    public const int BankCodeLength = 4;

    public const int AccountDigitCount = 10;

    public const int Length = 4 + BankCodeLength + AccountDigitCount;

    /// <summary>Exactly four letters A–Z. Codes are chosen in upper case; nothing normalises them.</summary>
    public static bool IsValidBankCode(string? code)
        => code is { Length: BankCodeLength } && code.All(IsUpperLetter);

    public static string Create(string bankCode, string accountDigits)
    {
        if (!IsValidBankCode(bankCode))
        {
            throw new ArgumentException($"'{bankCode}' is not a valid bank code; expected {BankCodeLength} letters A-Z.", nameof(bankCode));
        }

        if (accountDigits.Length != AccountDigitCount || !accountDigits.All(char.IsAsciiDigit))
        {
            throw new ArgumentException($"Expected {AccountDigitCount} account digits.", nameof(accountDigits));
        }

        var bban = bankCode + accountDigits;
        var checkDigits = 98 - Mod97(bban + Prefix + "00");
        return $"{Prefix}{checkDigits:00}{bban}";
    }

    /// <summary>
    /// Accepts the number as a player types it — spaces anywhere, any letter case — and yields the
    /// canonical form only if the layout and the check digits are both valid.
    /// </summary>
    public static bool TryNormalize(string? raw, out string number)
    {
        number = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var candidate = raw.Replace(" ", string.Empty).ToUpperInvariant();
        if (candidate.Length != Length
            || !candidate.StartsWith(Prefix, StringComparison.Ordinal)
            || !char.IsAsciiDigit(candidate[2]) || !char.IsAsciiDigit(candidate[3])
            || !IsValidBankCode(candidate[4..(4 + BankCodeLength)])
            || !candidate[(4 + BankCodeLength)..].All(char.IsAsciiDigit))
        {
            return false;
        }

        // A real IBAN validates by moving the first four characters to the end and requiring the
        // whole thing, letters converted to numbers, to be 1 mod 97.
        if (Mod97(candidate[4..] + candidate[..4]) != 1)
        {
            return false;
        }

        number = candidate;
        return true;
    }

    /// <summary>
    /// Mod 97 of the decimal string formed by replacing each letter with its value (A=10 … Z=35),
    /// folded digit by digit so it never needs a big integer.
    /// </summary>
    private static int Mod97(string value)
    {
        var remainder = 0;
        foreach (var character in value)
        {
            if (char.IsAsciiDigit(character))
            {
                remainder = ((remainder * 10) + (character - '0')) % 97;
            }
            else
            {
                remainder = ((remainder * 100) + (character - 'A' + 10)) % 97;
            }
        }

        return remainder;
    }

    private static bool IsUpperLetter(char character) => character is >= 'A' and <= 'Z';
}
