using ELifeRPG.Shared.Kernel;
using Xunit;

namespace ELifeRPG.Banking.Domain.UnitTests;

public class MoneyTests
{
    [Theory]
    [InlineData("0.005", "0.01")]
    [InlineData("0.015", "0.02")]
    [InlineData("0.0049", "0.00")]
    [InlineData("1.2345", "1.23")]
    [InlineData("-0.005", "-0.01")]
    public void RoundToCents_RoundsHalfAwayFromZero(string value, string expected)
    {
        Assert.Equal(decimal.Parse(expected), Money.RoundToCents(decimal.Parse(value)));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("1.5", true)]
    [InlineData("1.50", true)]
    [InlineData("1.500", true)]
    [InlineData("1.005", false)]
    [InlineData("0.001", false)]
    public void IsWholeCents_ComparesTheValueNotTheScale(string value, bool expected)
    {
        Assert.Equal(expected, Money.IsWholeCents(decimal.Parse(value)));
    }

    [Theory]
    [InlineData("0.01", true)]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("1.005", false)]
    public void IsValidAmount_RequiresPositiveWholeCents(string value, bool expected)
    {
        Assert.Equal(expected, Money.IsValidAmount(decimal.Parse(value)));
    }
}
