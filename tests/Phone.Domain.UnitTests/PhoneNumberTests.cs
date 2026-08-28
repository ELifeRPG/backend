using System.Text.Json;
using ELifeRPG.Phone.Domain.Exceptions;
using ELifeRPG.Phone.Domain.Devices;
using Xunit;

namespace ELifeRPG.Phone.Domain.UnitTests;

public class PhoneNumberTests
{
    [Fact]
    public void Parse_WithEightDigits_KeepsThemVerbatim()
    {
        var number = PhoneNumber.Parse("44127788");

        Assert.Equal("44127788", number.Value);
    }

    [Theory]
    [InlineData("4412 7788")]
    [InlineData("4412-7788")]
    [InlineData("+44127788")]
    [InlineData("  44127788  ")]
    [InlineData("(4412) 7788")]
    public void Parse_WithFormattingCharacters_NormalisesToBareDigits(string raw)
    {
        // Players type numbers by hand, so the canonical form has to survive punctuation — otherwise
        // two spellings of the same number would key two different threads.
        Assert.Equal("44127788", PhoneNumber.Parse(raw).Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("4412778")]      // seven digits
    [InlineData("441277889")]    // nine digits
    [InlineData("4412778a")]
    [InlineData("++44127788")]
    public void Parse_WithInvalidInput_ThrowsInvalidPhoneNumber(string raw)
    {
        Assert.Throws<InvalidPhoneNumberException>(() => PhoneNumber.Parse(raw));
    }

    [Fact]
    public void Parse_WithNull_ThrowsInvalidPhoneNumber()
    {
        Assert.Throws<InvalidPhoneNumberException>(() => PhoneNumber.Parse(null));
    }

    [Fact]
    public void TryParse_WithInvalidInput_ReturnsFalseAndDoesNotThrow()
    {
        Assert.False(PhoneNumber.TryParse("nope", out var number));
        Assert.True(number.IsEmpty);
    }

    [Fact]
    public void TryParse_WithValidInput_ReturnsTrue()
    {
        Assert.True(PhoneNumber.TryParse("4412-7788", out var number));
        Assert.Equal("44127788", number.Value);
    }

    [Fact]
    public void Equality_IgnoresOriginalFormatting()
    {
        Assert.Equal(PhoneNumber.Parse("4412 7788"), PhoneNumber.Parse("+44127788"));
    }

    [Fact]
    public void Default_IsEmptyAndDoesNotThrow()
    {
        // Never produced by Parse, but Marten can materialise a struct field before it is assigned.
        // Returning empty rather than throwing matches how the other aggregates treat unset strings.
        var number = default(PhoneNumber);

        Assert.True(number.IsEmpty);
        Assert.Equal(string.Empty, number.Value);
        Assert.Equal(string.Empty, number.ToString());
    }

    [Fact]
    public void Json_RoundTripsAsABareString()
    {
        // Serialised as a bare JSON string, not an object, so Marten can put a unique index straight
        // on SimCard.Number without reaching through a nested property.
        var json = JsonSerializer.Serialize(PhoneNumber.Parse("44127788"));

        Assert.Equal("\"44127788\"", json);
        Assert.Equal(PhoneNumber.Parse("44127788"), JsonSerializer.Deserialize<PhoneNumber>(json));
    }

    [Fact]
    public void Json_DeserialisingNull_YieldsEmpty()
    {
        Assert.True(JsonSerializer.Deserialize<PhoneNumber>("null").IsEmpty);
    }
}
