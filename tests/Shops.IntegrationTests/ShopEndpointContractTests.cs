using ELifeRPG.Shops.Domain;
// ShopModule's namespace, not ELifeRPG.Shops.Api — endpoint classes disable CheckNamespace.
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace ELifeRPG.Shops.IntegrationTests;

public sealed class ShopEndpointContractTests
{
    [Theory]
    [InlineData("Personal", ShopOwnerType.Personal)]
    [InlineData("Corporate", ShopOwnerType.Corporate)]
    public void TryParseOwnerType_AcceptsTheDeclaredNames(string raw, ShopOwnerType expected)
    {
        Assert.True(ShopModule.TryParseOwnerType(raw, out var ownerType, out var problem));
        Assert.Equal(expected, ownerType);
        Assert.Null(problem);
    }

    [Theory]
    [InlineData("personal", ShopOwnerType.Personal)]
    [InlineData("CORPORATE", ShopOwnerType.Corporate)]
    [InlineData("cOrPoRaTe", ShopOwnerType.Corporate)]
    public void TryParseOwnerType_IsCaseInsensitive(string raw, ShopOwnerType expected)
    {
        Assert.True(ShopModule.TryParseOwnerType(raw, out var ownerType, out _));
        Assert.Equal(expected, ownerType);
    }

    [Theory]
    [InlineData("7")]
    [InlineData("-1")]
    [InlineData("2")]
    public void TryParseOwnerType_RefusesNumbersThatNameNoMember(string raw)
    {
        Assert.False(ShopModule.TryParseOwnerType(raw, out _, out var problem));
        AssertIsOwnerTypeBadRequest(problem);
    }

    // This used to assert the opposite: IsDefined admits a real member's ordinal, so "0" and "1"
    // parsed. WireEnum now requires a name, because an ordinal is the stored form of an enum here
    // (ARCHITECTURE.md §9e) and accepting one over HTTP tied every client to Postgres's numbering —
    // Phone renumbered AppKey and ?appKey=3 silently changed which app it meant.
    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    public void TryParseOwnerType_RefusesTheOrdinalOfARealMember(string raw)
    {
        Assert.False(ShopModule.TryParseOwnerType(raw, out _, out var problem));
        AssertIsOwnerTypeBadRequest(problem);
    }

    // A name still parses case-insensitively, and leading whitespace is tolerated — only the
    // numeric form is refused.
    [Theory]
    [InlineData("personal", ShopOwnerType.Personal)]
    [InlineData("  Corporate", ShopOwnerType.Corporate)]
    public void TryParseOwnerType_AcceptsAMemberName(string raw, ShopOwnerType expected)
    {
        Assert.True(ShopModule.TryParseOwnerType(raw, out var ownerType, out _));
        Assert.Equal(expected, ownerType);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Municipal")]
    public void TryParseOwnerType_RefusesMissingAndUnknownNames(string? raw)
    {
        Assert.False(ShopModule.TryParseOwnerType(raw, out _, out var problem));
        AssertIsOwnerTypeBadRequest(problem);
    }

    private static void AssertIsOwnerTypeBadRequest(IResult? problem)
    {
        var details = Assert.IsType<ProblemHttpResult>(problem).ProblemDetails;
        Assert.Equal(StatusCodes.Status400BadRequest, details.Status);
        Assert.Contains(nameof(ShopOwnerType.Personal), details.Title);
        Assert.Contains(nameof(ShopOwnerType.Corporate), details.Title);
    }
}
