using ELifeRPG.Shared.Kernel;
using ELifeRPG.World.Api.Inventory;
using ELifeRPG.World.Application.Inventory;
using ELifeRPG.World.Domain.Snapshots;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace ELifeRPG.World.IntegrationTests;

/// <summary>
/// The two pure functions behind <c>POST /api/inventory/snapshots</c>: the request parse
/// (<c>WorldModule.TryParseApplySnapshotCommand</c>) and the result-to-<see cref="IResult"/> mapping
/// (<c>WorldModule.ToProblemOrOk</c>). Neither touches HTTP, DI or the database — they are a DTO
/// function and a union function — so these are ordinary unit tests that happen to live in this project
/// because it is the assembly <c>World.Api</c> grants <c>InternalsVisibleTo</c>.
///
/// <b>Why this file exists at all (review round 3's ruling).</b> Three findings across this phase — task
/// 1's <c>retryable</c> problem-detail flag, task 3's result mapping, and task 4's malformed-scope
/// rejection — were every one of them a pure function of a DTO or a union value, and every one of them
/// went untested for the same reason: it was <c>private static</c> inside an endpoint lambda's enclosing
/// class. A <c>WebApplicationFactory</c> harness would be phase-sized, would not start cleanly offline,
/// and would not have caught any of the three earlier than this does. Two <c>internal</c> members and
/// one extracted method is the whole cost.
///
/// No <see cref="TestServices"/> provider and no <c>IAsyncLifetime</c>: nothing here needs a scope.
/// </summary>
public sealed class SnapshotEndpointContractTests
{
    private static readonly GameServerId ServerId = new(Guid.NewGuid());

    private static ApplySnapshotRequestDto Request(SnapshotScopeRequestDto scope, string mode = "Partial", long? sequence = null) => new()
    {
        BatchId = Guid.NewGuid(),
        Scope = scope,
        Sequence = sequence,
        Mode = mode,
        Upserts = [],
        Deletes = [],
    };

    private static ProblemDetails ProblemOf(IResult result)
        => Assert.IsType<ProblemHttpResult>(result).ProblemDetails;

    /// <summary>
    /// Review round 2's HIGH, at the outer lock. A scope carrying <i>both</i> companion ids names two
    /// anchors at once — "this batch is about a character" and "this batch is about a container"
    /// together — and is malformed on its face, so it must never parse. Until round 2 it did: only the
    /// required id for the declared kind was checked, and both were copied into the command regardless,
    /// which let one stray field unlock an unrelated crate's contents for deletion inside the sweep.
    ///
    /// The handler is independently gated (see <c>ApplySnapshotTests</c>'s stray-container-id test,
    /// which dispatches past this parse on purpose). This is the lock that stops such a request existing
    /// in the first place.
    /// </summary>
    [Fact]
    public void TryParseApplySnapshotCommand_ForACharacterScopeCarryingAContainerId_IsRejected()
    {
        var parsed = WorldModule.TryParseApplySnapshotCommand(
            ServerId,
            Request(new SnapshotScopeRequestDto
            {
                Kind = "Character",
                CharacterId = Guid.NewGuid(),
                ContainerInstanceId = Guid.NewGuid(),
            }),
            out _,
            out var problem);

        Assert.False(parsed);
        Assert.NotNull(problem);

        var details = ProblemOf(problem);
        Assert.Equal(StatusCodes.Status400BadRequest, details.Status);
        Assert.Contains("containerInstanceId", details.Title);
        Assert.Equal(false, details.Extensions["retryable"]);
    }

    [Fact]
    public void TryParseApplySnapshotCommand_ForAContainerScopeCarryingACharacterId_IsRejected()
    {
        var parsed = WorldModule.TryParseApplySnapshotCommand(
            ServerId,
            Request(new SnapshotScopeRequestDto
            {
                Kind = "Container",
                CharacterId = Guid.NewGuid(),
                ContainerInstanceId = Guid.NewGuid(),
            }),
            out _,
            out var problem);

        Assert.False(parsed);
        Assert.NotNull(problem);

        var details = ProblemOf(problem);
        Assert.Equal(StatusCodes.Status400BadRequest, details.Status);
        Assert.Contains("characterId", details.Title);
    }

    /// <summary>
    /// Review round 1 (unknown-prefabs task, closed alongside the same-shaped defect there while this
    /// seam was open): <c>required</c> on <see cref="ApplySnapshotRequestDto.Scope"/> stops System.Text.Json
    /// rejecting an absent key, but not an explicit JSON <c>null</c> — a <c>{ "scope": null }</c> body
    /// used to reach <c>request.Scope.Kind</c> and NRE into an unhandled 500 instead of a 400.
    /// </summary>
    [Fact]
    public void TryParseApplySnapshotCommand_ForANullScope_IsRejectedWithoutThrowing()
    {
        var parsed = WorldModule.TryParseApplySnapshotCommand(
            ServerId,
            new ApplySnapshotRequestDto { BatchId = Guid.NewGuid(), Scope = null!, Mode = "Partial", Upserts = [], Deletes = [] },
            out _,
            out var problem);

        Assert.False(parsed);
        Assert.Contains("scope", ProblemOf(problem!).Title);
    }

    /// <summary>Same defect shape as the null-scope case, over <c>upserts</c>/<c>deletes</c> and their individual entries — a <c>{ "upserts": null }</c> or <c>{ "upserts": [null] }</c> body must 400, not 500.</summary>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void TryParseApplySnapshotCommand_ForNullUpsertsOrDeletesOrAnElement_IsRejectedWithoutThrowing(
        bool nullUpserts, bool nullUpsertElement, bool nullDeletes)
    {
        var request = new ApplySnapshotRequestDto
        {
            BatchId = Guid.NewGuid(),
            Scope = new SnapshotScopeRequestDto { Kind = "Character", CharacterId = Guid.NewGuid() },
            Mode = "Partial",
            Upserts = nullUpserts ? null! : nullUpsertElement ? [null!] : [],
            Deletes = nullDeletes ? null! : [],
        };

        var parsed = WorldModule.TryParseApplySnapshotCommand(ServerId, request, out _, out var problem);

        Assert.False(parsed);
        var title = ProblemOf(problem!).Title;
        Assert.True(
            title!.Contains("upserts", StringComparison.OrdinalIgnoreCase) || title.Contains("deletes", StringComparison.OrdinalIgnoreCase),
            $"Expected the title to name upserts or deletes, got: {title}");
    }

    /// <summary>
    /// Review round 2: the same defect shape one level deeper. <see cref="SnapshotUpsertRequestDto.Attributes"/>
    /// carries a property <i>initializer</i> (<c>= new Dictionary&lt;string, string&gt;()</c>), not
    /// <c>required</c> — but System.Text.Json only consults an initializer for an <i>absent</i> key,
    /// exactly like <c>required</c> only guards presence. An explicit <c>"attributes": null</c> used to
    /// reach <c>ItemAttributes.Create(values)</c> downstream (<c>ApplySnapshotCommand.cs</c> step 6) with
    /// <c>values == null</c>, NREing on <c>values.Count</c> instead of being rejected here.
    ///
    /// <b>This was not the last leaf in this family, and round 2's claim that it was is exactly why round
    /// 3 stopped enumerating case by case</b> — see <see cref="TryParseApplySnapshotCommand_ForAnAttributesDictionaryWithANullValue_IsRejectedWithoutThrowing"/>
    /// immediately below for the one this method's own null-dictionary check does not reach, and
    /// <c>ItemAttributes.Validate</c>'s own doc comment for the domain-level backstop added alongside it.
    /// </summary>
    [Fact]
    public void TryParseApplySnapshotCommand_ForANullAttributesDictionary_IsRejectedWithoutThrowing()
    {
        var request = new ApplySnapshotRequestDto
        {
            BatchId = Guid.NewGuid(),
            Scope = new SnapshotScopeRequestDto { Kind = "Character", CharacterId = Guid.NewGuid() },
            Mode = "Partial",
            Upserts =
            [
                new SnapshotUpsertRequestDto
                {
                    InstanceId = Guid.NewGuid(),
                    Revision = 1,
                    ItemId = Guid.NewGuid(),
                    Parent = new SnapshotParentRequestDto { Kind = "Character", CharacterId = Guid.NewGuid() },
                    Attributes = null!,
                },
            ],
            Deletes = [],
        };

        var parsed = WorldModule.TryParseApplySnapshotCommand(ServerId, request, out _, out var problem);

        Assert.False(parsed);
        Assert.Contains("attributes", ProblemOf(problem!).Title);
    }

    /// <summary>
    /// Review round 3: the leaf one level deeper than the dictionary itself, and the one round 2's
    /// enumeration missed. A JSON object's <i>keys</i> are always strings by grammar, but a <i>value</i>
    /// can be an explicit <c>null</c> — <c>{ "attributes": { "k": null } }</c> — which System.Text.Json
    /// deserializes straight into this <c>Dictionary&lt;string, string&gt;</c> despite its declared value
    /// type having no nullable annotation, clearing the null-*dictionary* check above and NREing on
    /// <c>value.Length</c> inside <c>ItemAttributes.Validate</c> instead.
    ///
    /// Three consecutive rounds of "fix the leaf, find another leaf" is what moved this from "enumerate
    /// harder" to "close the class": <c>ItemAttributes.Validate</c> now also rejects a <c>null</c> value
    /// with a named <c>AttributeLimitExceededException</c>, caught by the exact same existing
    /// <c>catch (AttributeLimitExceededException)</c> in <c>ApplySnapshotCommand.cs</c> step 6 that
    /// already handles an oversized bag — so a leaf this test suite still doesn't enumerate degrades to a
    /// per-instance rejection rather than a crash. This parse-layer check stays the primary gate (a
    /// malformed batch is rejected whole, before any row is read); the domain guard is strictly a
    /// backstop for whatever the parse layer missed.
    /// </summary>
    [Fact]
    public void TryParseApplySnapshotCommand_ForAnAttributesDictionaryWithANullValue_IsRejectedWithoutThrowing()
    {
        var request = new ApplySnapshotRequestDto
        {
            BatchId = Guid.NewGuid(),
            Scope = new SnapshotScopeRequestDto { Kind = "Character", CharacterId = Guid.NewGuid() },
            Mode = "Partial",
            Upserts =
            [
                new SnapshotUpsertRequestDto
                {
                    InstanceId = Guid.NewGuid(),
                    Revision = 1,
                    ItemId = Guid.NewGuid(),
                    Parent = new SnapshotParentRequestDto { Kind = "Character", CharacterId = Guid.NewGuid() },
                    Attributes = new Dictionary<string, string> { ["k"] = null! },
                },
            ],
            Deletes = [],
        };

        var parsed = WorldModule.TryParseApplySnapshotCommand(ServerId, request, out _, out var problem);

        Assert.False(parsed);
        Assert.Contains("attributes", ProblemOf(problem!).Title);
    }

    /// <summary>
    /// Review round 2, the other leaf one level deeper: <c>WorldTransformDto.Position</c>/<c>Rotation</c>
    /// are <c>required</c>, which stops an absent key but not an explicit JSON <c>null</c> — a
    /// <c>{ "parent": { "kind": "World", "transform": { "position": null, "rotation": {...} } } }</c> body
    /// used to reach <c>parent.Transform.Position.X</c> and NRE, even though <c>parent.Transform</c>
    /// itself was already checked non-null.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void TryParseApplySnapshotCommand_ForAWorldParentWithANullPositionOrRotation_IsRejectedWithoutThrowing(
        bool nullPosition, bool nullRotation)
    {
        var vector = new WorldVector3Dto { X = 0, Y = 0, Z = 0 };
        var request = new ApplySnapshotRequestDto
        {
            BatchId = Guid.NewGuid(),
            Scope = new SnapshotScopeRequestDto { Kind = "Character", CharacterId = Guid.NewGuid() },
            Mode = "Partial",
            Upserts =
            [
                new SnapshotUpsertRequestDto
                {
                    InstanceId = Guid.NewGuid(),
                    Revision = 1,
                    ItemId = Guid.NewGuid(),
                    Parent = new SnapshotParentRequestDto
                    {
                        Kind = "World",
                        Transform = new WorldTransformDto
                        {
                            Position = nullPosition ? null! : vector,
                            Rotation = nullRotation ? null! : vector,
                        },
                    },
                },
            ],
            Deletes = [],
        };

        var parsed = WorldModule.TryParseApplySnapshotCommand(ServerId, request, out _, out var problem);

        Assert.False(parsed);
        var title = ProblemOf(problem!).Title;
        Assert.True(
            title!.Contains("position", StringComparison.OrdinalIgnoreCase) || title.Contains("rotation", StringComparison.OrdinalIgnoreCase),
            $"Expected the title to name position or rotation, got: {title}");
    }

    /// <summary>The calibration half: each kind on its own, with only its own companion id, still parses.</summary>
    [Fact]
    public void TryParseApplySnapshotCommand_ForAScopeCarryingOnlyItsOwnCompanionId_Parses()
    {
        var characterId = Guid.NewGuid();
        var parsedCharacter = WorldModule.TryParseApplySnapshotCommand(
            ServerId,
            Request(new SnapshotScopeRequestDto { Kind = "Character", CharacterId = characterId }),
            out var characterCommand,
            out _);

        Assert.True(parsedCharacter);
        Assert.Equal(SnapshotScopeKind.Character, characterCommand!.ScopeKind);
        Assert.Equal(new CharacterId(characterId), characterCommand.ScopeCharacterId);
        Assert.Null(characterCommand.ScopeContainerInstanceId);

        var containerInstanceId = Guid.NewGuid();
        var parsedContainer = WorldModule.TryParseApplySnapshotCommand(
            ServerId,
            Request(new SnapshotScopeRequestDto { Kind = "Container", ContainerInstanceId = containerInstanceId }),
            out var containerCommand,
            out _);

        Assert.True(parsedContainer);
        Assert.Equal(SnapshotScopeKind.Container, containerCommand!.ScopeKind);
        Assert.Equal(new ItemInstanceId(containerInstanceId), containerCommand.ScopeContainerInstanceId);
        Assert.Null(containerCommand.ScopeCharacterId);
    }

    /// <summary>
    /// The <c>retryable</c> flag, asserted for the first time (task 1 shipped it untested; review round
    /// 3 ruled the seam in). The Bridge's SQLite buffer keys its whole retry policy on this: it is what
    /// separates "already done or you're behind — drop it" from "try again later", and getting it
    /// backwards on a single case either strands a valid batch forever or spins a doomed one.
    ///
    /// Every batch-level rejection this endpoint has is non-retryable except one, and that asymmetry is
    /// the contract. <c>concurrent_reconcile</c> is the exception because it is the only case that names
    /// no fault in the request at all — the batch was valid and merely lost a race for its scope's
    /// cursor, so an unmodified resend is exactly right.
    /// </summary>
    [Theory]
    [MemberData(nameof(NonRetryableResults))]
    public void ToProblemOrOk_ForEveryBatchLevelRejectionExceptConcurrentReconcile_IsNotRetryable(
        ApplySnapshotResult result, int expectedStatus)
    {
        var details = ProblemOf(WorldModule.ToProblemOrOk(result));

        Assert.Equal(expectedStatus, details.Status);
        Assert.Equal(false, details.Extensions["retryable"]);
    }

    public static TheoryData<ApplySnapshotResult, int> NonRetryableResults() => new()
    {
        { new ApplySnapshotResult.DuplicateInstanceId(new ItemInstanceId(Guid.NewGuid())), StatusCodes.Status400BadRequest },
        { new ApplySnapshotResult.BatchTooLarge("upserts", 5000, 1000), StatusCodes.Status400BadRequest },
        { new ApplySnapshotResult.SequenceOutOfRange(-1, ScopeCursor.MaxSequence), StatusCodes.Status400BadRequest },
        { new ApplySnapshotResult.UnsupportedFullScope(SnapshotScopeKind.Container), StatusCodes.Status400BadRequest },
        { new ApplySnapshotResult.WrongServer(), StatusCodes.Status409Conflict },
        { new ApplySnapshotResult.StaleSequence(42), StatusCodes.Status409Conflict },
        { new ApplySnapshotResult.SuspiciousReconcile(30, 30, 0, 25, 3, 90), StatusCodes.Status422UnprocessableEntity },
    };

    /// <summary>
    /// The one retryable outcome, and the only <c>Results.Problem</c> on this endpoint that deliberately
    /// does not go through the <c>NotRetryableExtensions</c> helper — see that helper's own comment. A
    /// plain, unmodified resend is the correct Bridge response, so this flag being <c>false</c> would
    /// silently discard a perfectly valid batch that merely committed second.
    /// </summary>
    [Fact]
    public void ToProblemOrOk_ForConcurrentReconcile_IsTheOneRetryableOutcome()
    {
        var details = ProblemOf(WorldModule.ToProblemOrOk(new ApplySnapshotResult.ConcurrentReconcile()));

        Assert.Equal(StatusCodes.Status409Conflict, details.Status);
        Assert.Equal(true, details.Extensions["retryable"]);
    }

    /// <summary>
    /// Review round 4: the <c>422</c>'s title is read by a staff member working out what happened, and
    /// its counts are over <i>sweep-eligible</i> rows — live, not pendingSpawn, not staff-removed. For
    /// the very case round 3 fixed (30 undelivered grants alongside 2 carried items) an unqualified
    /// "of the 2 rows in its scope" renders to someone looking at a character holding 32, who can only
    /// read it as a bug or a lie.
    ///
    /// The knob names keep "ScopeRows" and lean on their doc comments for the same distinction, which is
    /// a defensible trade for a published field. A sentence in a problem detail has no doc comment
    /// beside it, so it has to carry the qualification itself — and this is the assertion that keeps the
    /// two policies from being confused for each other later.
    /// </summary>
    [Fact]
    public void ToProblemOrOk_ForSuspiciousReconcile_QualifiesItsCountsAsSweepEligibleRows()
    {
        // 30 undelivered grants + 2 carried, an accurate payload naming both: 2 eligible rows, none swept.
        var details = ProblemOf(WorldModule.ToProblemOrOk(
            new ApplySnapshotResult.SuspiciousReconcile(WouldHaveSwept: 0, ScopeRowCount: 2, Upserts: 2, ScopeRowsThreshold: 25, UpsertsThreshold: 3, SweptPercentThreshold: 90)));

        Assert.Contains("sweep-eligible", details.Title);

        // The number and the noun have to travel together — an unqualified "of the 2 rows in its scope"
        // is exactly the sentence this test exists to keep out.
        Assert.DoesNotContain("2 rows in its scope", details.Title);
    }

    /// <summary><c>stale_sequence</c> carries <c>lastAppliedSequence</c> alongside the flag, so the Bridge can tell how far behind it fell rather than only that it is behind.</summary>
    [Fact]
    public void ToProblemOrOk_ForStaleSequence_CarriesTheLastAppliedSequence()
    {
        var details = ProblemOf(WorldModule.ToProblemOrOk(new ApplySnapshotResult.StaleSequence(42)));

        Assert.Equal(42L, details.Extensions["lastAppliedSequence"]);
    }

    /// <summary>
    /// The happy path is the one arm that is not a problem detail at all, so it is worth pinning
    /// separately: a mapping change that turned <c>Applied</c> into a 4xx would otherwise be invisible to
    /// every assertion above.
    /// </summary>
    [Fact]
    public void ToProblemOrOk_ForApplied_IsAnOkResponseCarryingTheCounts()
    {
        var batchId = Guid.NewGuid();
        var result = WorldModule.ToProblemOrOk(new ApplySnapshotResult.Applied(
            batchId, 7, AppliedCount: 3, SkippedNoOp: 1, Deleted: 2, CascadeDeleted: 4, Swept: 5, Rejected: [], ReplayOfPriorBatch: true));

        var body = Assert.IsType<Ok<ApplySnapshotResponseDto>>(result).Value;

        Assert.NotNull(body);
        Assert.Equal(batchId, body.BatchId);
        Assert.Equal(7, body.Sequence);
        Assert.Equal(3, body.Applied);
        Assert.Equal(1, body.SkippedNoOp);
        Assert.Equal(2, body.Deleted);
        Assert.Equal(4, body.CascadeDeleted);
        Assert.Equal(5, body.Swept);
        Assert.True(body.ReplayOfPriorBatch);
    }
}
