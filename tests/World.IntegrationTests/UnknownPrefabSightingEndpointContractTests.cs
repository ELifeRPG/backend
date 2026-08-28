using ELifeRPG.World.Api.Inventory;
using ELifeRPG.World.Application.Inventory;
using ELifeRPG.World.Domain.Inventory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace ELifeRPG.World.IntegrationTests;

/// <summary>
/// The two pure functions behind <c>POST /api/inventory/unknown-prefabs</c>:
/// <c>WorldModule.TryParseRecordUnknownPrefabSightingsCommand</c> (every bound the task 5 brief names —
/// <c>count</c>, <c>firstSeenAt</c>, <c>prefabClassName</c>, <c>sampleContext</c>) and the
/// result-to-<see cref="IResult"/> mapping <c>WorldModule.ToProblemOrAccepted</c>. Neither touches
/// HTTP, DI, or the database — same seam <see cref="SnapshotEndpointContractTests"/> already uses, for
/// the same reason (see that file's own doc comment): an untested <c>private static</c> validation is
/// exactly the shape every earlier task's shipped defect took.
/// </summary>
public sealed class UnknownPrefabSightingEndpointContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    private static RecordUnknownPrefabSightingsRequestDto Request(params UnknownPrefabSightingRequestDto[] sightings) => new()
    {
        Sightings = sightings,
    };

    private static UnknownPrefabSightingRequestDto Sighting(
        string prefabClassName = "MyMod_SomePrefab",
        int count = 1,
        DateTimeOffset? firstSeenAt = null,
        string? sampleContext = null) => new()
    {
        PrefabClassName = prefabClassName,
        Count = count,
        FirstSeenAt = firstSeenAt ?? Now,
        SampleContext = sampleContext,
    };

    private static ProblemDetails ProblemOf(IResult result) => Assert.IsType<ProblemHttpResult>(result).ProblemDetails;

    [Fact]
    public void TryParse_ForAWellFormedSighting_Parses()
    {
        var parsed = WorldModule.TryParseRecordUnknownPrefabSightingsCommand(
            Request(Sighting(count: 4, sampleContext: "near the docks")), Now, out var command, out var problem);

        Assert.True(parsed);
        Assert.Null(problem);
        var sighting = Assert.Single(command.Sightings);
        Assert.Equal("MyMod_SomePrefab", sighting.PrefabClassName);
        Assert.Equal(4, sighting.Count);
        Assert.Equal("near the docks", sighting.SampleContext);
    }

    /// <summary>Leading/trailing whitespace must never split one prefab into two rows — see <see cref="UnknownPrefabSighting.BuildId"/>'s own doc comment.</summary>
    [Fact]
    public void TryParse_TrimsWhitespaceFromThePrefabClassName()
    {
        var parsed = WorldModule.TryParseRecordUnknownPrefabSightingsCommand(
            Request(Sighting(prefabClassName: "  MyMod_SomePrefab  ")), Now, out var command, out _);

        Assert.True(parsed);
        Assert.Equal("MyMod_SomePrefab", command.Sightings[0].PrefabClassName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryParse_ForAMissingOrBlankPrefabClassName_IsRejected(string? prefabClassName)
    {
        var parsed = WorldModule.TryParseRecordUnknownPrefabSightingsCommand(
            Request(Sighting(prefabClassName: prefabClassName!)), Now, out _, out var problem);

        Assert.False(parsed);
        var details = ProblemOf(problem!);
        Assert.Equal(StatusCodes.Status400BadRequest, details.Status);
        Assert.Contains("prefabClassName", details.Title);
        Assert.Equal(false, details.Extensions["retryable"]);
    }

    [Fact]
    public void TryParse_ForAPrefabClassNameOverTheMaxLength_IsRejected()
    {
        var tooLong = new string('a', UnknownPrefabSighting.MaxPrefabClassNameLength + 1);

        var parsed = WorldModule.TryParseRecordUnknownPrefabSightingsCommand(
            Request(Sighting(prefabClassName: tooLong)), Now, out _, out var problem);

        Assert.False(parsed);
        Assert.Contains("prefabClassName", ProblemOf(problem!).Title);
    }

    /// <summary>Exactly at the cap must still parse — the calibration half of the length check above.</summary>
    [Fact]
    public void TryParse_ForAPrefabClassNameAtExactlyTheMaxLength_Parses()
    {
        var atLimit = new string('a', UnknownPrefabSighting.MaxPrefabClassNameLength);

        var parsed = WorldModule.TryParseRecordUnknownPrefabSightingsCommand(
            Request(Sighting(prefabClassName: atLimit)), Now, out _, out _);

        Assert.True(parsed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TryParse_ForACountBelowOne_IsRejected(int count)
    {
        var parsed = WorldModule.TryParseRecordUnknownPrefabSightingsCommand(
            Request(Sighting(count: count)), Now, out _, out var problem);

        Assert.False(parsed);
        Assert.Contains("count", ProblemOf(problem!).Title);
    }

    [Fact]
    public void TryParse_ForACountOverTheMax_IsRejected()
    {
        var parsed = WorldModule.TryParseRecordUnknownPrefabSightingsCommand(
            Request(Sighting(count: UnknownPrefabSighting.MaxCountPerSighting + 1)), Now, out _, out var problem);

        Assert.False(parsed);
        Assert.Contains("count", ProblemOf(problem!).Title);
    }

    [Fact]
    public void TryParse_ForACountAtExactlyTheMax_Parses()
    {
        var parsed = WorldModule.TryParseRecordUnknownPrefabSightingsCommand(
            Request(Sighting(count: UnknownPrefabSighting.MaxCountPerSighting)), Now, out _, out _);

        Assert.True(parsed);
    }

    [Fact]
    public void TryParse_ForAFirstSeenAtWellInTheFuture_IsRejected()
    {
        var parsed = WorldModule.TryParseRecordUnknownPrefabSightingsCommand(
            Request(Sighting(firstSeenAt: Now.AddHours(1))), Now, out _, out var problem);

        Assert.False(parsed);
        Assert.Contains("firstSeenAt", ProblemOf(problem!).Title);
    }

    /// <summary>A small clock-skew allowance must not itself be rejected — see the endpoint's own comment on why some slack ahead of "now" is tolerated.</summary>
    [Fact]
    public void TryParse_ForAFirstSeenAtWithinTheClockSkewAllowance_Parses()
    {
        var parsed = WorldModule.TryParseRecordUnknownPrefabSightingsCommand(
            Request(Sighting(firstSeenAt: Now.AddMinutes(1))), Now, out _, out _);

        Assert.True(parsed);
    }

    [Fact]
    public void TryParse_ForAFirstSeenAtWellInThePast_IsRejected()
    {
        var parsed = WorldModule.TryParseRecordUnknownPrefabSightingsCommand(
            Request(Sighting(firstSeenAt: Now.AddDays(-31))), Now, out _, out var problem);

        Assert.False(parsed);
        Assert.Contains("firstSeenAt", ProblemOf(problem!).Title);
    }

    [Fact]
    public void TryParse_ForAFirstSeenAtAReasonableAmountInThePast_Parses()
    {
        var parsed = WorldModule.TryParseRecordUnknownPrefabSightingsCommand(
            Request(Sighting(firstSeenAt: Now.AddDays(-29))), Now, out _, out _);

        Assert.True(parsed);
    }

    [Fact]
    public void TryParse_ForASampleContextOverTheMaxLength_IsRejected()
    {
        var tooLong = new string('x', UnknownPrefabSighting.MaxSampleContextLength + 1);

        var parsed = WorldModule.TryParseRecordUnknownPrefabSightingsCommand(
            Request(Sighting(sampleContext: tooLong)), Now, out _, out var problem);

        Assert.False(parsed);
        Assert.Contains("sampleContext", ProblemOf(problem!).Title);
    }

    [Fact]
    public void TryParse_ForANullSampleContext_Parses()
    {
        var parsed = WorldModule.TryParseRecordUnknownPrefabSightingsCommand(
            Request(Sighting(sampleContext: null)), Now, out var command, out _);

        Assert.True(parsed);
        Assert.Null(command.Sightings[0].SampleContext);
    }

    /// <summary>
    /// An empty batch is not an error — the endpoint accepts it as a no-op, matching
    /// <see cref="RecordUnknownPrefabSightingsHandler"/>'s own early-return for the same shape.
    /// </summary>
    [Fact]
    public void TryParse_ForAnEmptySightingsArray_Parses()
    {
        var parsed = WorldModule.TryParseRecordUnknownPrefabSightingsCommand(Request(), Now, out var command, out _);

        Assert.True(parsed);
        Assert.Empty(command.Sightings);
    }

    /// <summary>
    /// One invalid entry fails the whole batch before any row is touched — invariant 5 (input bounds),
    /// checked here at index 1 so this also proves indices past the first are actually validated, not
    /// just the first element of the array.
    /// </summary>
    [Fact]
    public void TryParse_ForABatchWhereTheSecondEntryIsInvalid_RejectsTheWholeBatch()
    {
        var parsed = WorldModule.TryParseRecordUnknownPrefabSightingsCommand(
            Request(Sighting(prefabClassName: "Fine"), Sighting(prefabClassName: "")), Now, out _, out var problem);

        Assert.False(parsed);
        Assert.Contains("sightings[1]", ProblemOf(problem!).Title);
    }

    /// <summary>
    /// Review round 1: <c>required</c> on <see cref="RecordUnknownPrefabSightingsRequestDto.Sightings"/>
    /// stops System.Text.Json rejecting an absent key, but not an explicit JSON <c>null</c> — a
    /// <c>{ "sightings": null }</c> body used to reach <c>request.Sightings.Count</c> and NRE into an
    /// unhandled 500 instead of a 400.
    /// </summary>
    [Fact]
    public void TryParse_ForANullSightingsArray_IsRejectedWithoutThrowing()
    {
        var parsed = WorldModule.TryParseRecordUnknownPrefabSightingsCommand(
            new RecordUnknownPrefabSightingsRequestDto { Sightings = null! }, Now, out _, out var problem);

        Assert.False(parsed);
        Assert.Contains("sightings", ProblemOf(problem!).Title);
    }

    /// <summary>Same shape as the null-array case, but a <c>{ "sightings": [null] }</c> body — an individual array element can be null without the array itself being null.</summary>
    [Fact]
    public void TryParse_ForANullSightingsElement_IsRejectedWithoutThrowing()
    {
        var parsed = WorldModule.TryParseRecordUnknownPrefabSightingsCommand(
            new RecordUnknownPrefabSightingsRequestDto { Sightings = [null!] }, Now, out _, out var problem);

        Assert.False(parsed);
        Assert.Contains("sightings[0]", ProblemOf(problem!).Title);
    }

    /// <summary>Review round 1: an all-whitespace sampleContext must normalize to null, not store as a non-empty-looking-but-meaningless string.</summary>
    [Fact]
    public void TryParse_ForAWhitespaceOnlySampleContext_NormalizesToNull()
    {
        var parsed = WorldModule.TryParseRecordUnknownPrefabSightingsCommand(
            Request(Sighting(sampleContext: "   ")), Now, out var command, out _);

        Assert.True(parsed);
        Assert.Null(command.Sightings[0].SampleContext);
    }

    /// <summary>Review round 1: leading/trailing whitespace on an otherwise-valid sampleContext is trimmed, matching prefabClassName's own trimming.</summary>
    [Fact]
    public void TryParse_TrimsWhitespaceFromTheSampleContext()
    {
        var parsed = WorldModule.TryParseRecordUnknownPrefabSightingsCommand(
            Request(Sighting(sampleContext: "  near the docks  ")), Now, out var command, out _);

        Assert.True(parsed);
        Assert.Equal("near the docks", command.Sightings[0].SampleContext);
    }

    [Fact]
    public void ToProblemOrAccepted_ForRecorded_Is202Accepted()
    {
        var result = WorldModule.ToProblemOrAccepted(new RecordUnknownPrefabSightingsResult.Recorded(3));

        var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status202Accepted, statusResult.StatusCode);
    }

    [Fact]
    public void ToProblemOrAccepted_ForBatchTooLarge_Is400AndNotRetryable()
    {
        var details = ProblemOf(WorldModule.ToProblemOrAccepted(new RecordUnknownPrefabSightingsResult.BatchTooLarge(1500, 1000)));

        Assert.Equal(StatusCodes.Status400BadRequest, details.Status);
        Assert.Equal(false, details.Extensions["retryable"]);
        Assert.Contains("1500", details.Title);
        Assert.Contains("1000", details.Title);
    }
}
