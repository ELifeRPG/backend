using ELifeRPG.Banking.Domain;
using Xunit;

namespace ELifeRPG.Banking.Domain.UnitTests;

public class BankAccountNumberTests
{
    // The example from issue #159, cross-checked against the standard IBAN MOD-97 algorithm.
    private const string Example = "EL90FEBK0123456789";

    [Fact]
    public void Create_CalculatesIbanCheckDigits()
        => Assert.Equal(Example, BankAccountNumber.Create("FEBK", "0123456789"));

    [Fact]
    public void Create_KeepsLeadingZeroInCheckDigits()
    {
        // Covers the zero-padding of check digits below 10 across many account numbers.
        for (var i = 0; i < 1000; i++)
        {
            var number = BankAccountNumber.Create("ABCD", i.ToString("D10"));

            Assert.Equal(BankAccountNumber.Length, number.Length);
            Assert.True(BankAccountNumber.TryNormalize(number, out _), number);
        }
    }

    [Theory]
    [InlineData("FEB")]
    [InlineData("FEBKS")]
    [InlineData("FEB1")]
    [InlineData("febk")]
    public void Create_RejectsInvalidBankCode(string code)
        => Assert.Throws<ArgumentException>(() => BankAccountNumber.Create(code, "0123456789"));

    [Theory]
    [InlineData("012345678")]
    [InlineData("012345678A")]
    public void Create_RejectsInvalidAccountDigits(string digits)
        => Assert.Throws<ArgumentException>(() => BankAccountNumber.Create("FEBK", digits));

    [Theory]
    [InlineData(Example)]
    [InlineData("EL90 FEBK 0123 4567 89")]
    [InlineData("el90febk0123456789")]
    [InlineData(" el90 febk 0123 4567 89 ")]
    public void TryNormalize_AcceptsTypedForms(string raw)
    {
        Assert.True(BankAccountNumber.TryNormalize(raw, out var number));
        Assert.Equal(Example, number);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("EL90FEBK0123456788")] // one digit off
    [InlineData("EL90FEBK1023456789")] // two digits swapped
    [InlineData("EL91FEBK0123456789")] // wrong check digits
    [InlineData("NL90FEBK0123456789")] // wrong prefix
    [InlineData("EL90FEBK012345678")] // too short
    [InlineData("EL90FEBK01234567890")] // too long
    [InlineData("EL90FEB10123456789")] // digit in the bank code
    [InlineData("EL90FEBK012345678A")] // letter in the account
    [InlineData("EL90-FEBK-0123-4567-89")] // only spaces are tolerated
    public void TryNormalize_RejectsInvalidNumbers(string? raw)
    {
        Assert.False(BankAccountNumber.TryNormalize(raw, out var number));
        Assert.Equal(string.Empty, number);
    }

    [Theory]
    [InlineData("FEBK", true)]
    [InlineData("febk", false)]
    [InlineData("FEB", false)]
    [InlineData("FÉBK", false)]
    [InlineData(null, false)]
    public void IsValidBankCode(string? code, bool expected)
        => Assert.Equal(expected, BankAccountNumber.IsValidBankCode(code));
}
