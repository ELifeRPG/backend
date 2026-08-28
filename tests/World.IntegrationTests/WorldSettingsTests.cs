using System.Reflection;
using ELifeRPG.World.Api.Inventory;
using ELifeRPG.World.Application.Settings;
using ELifeRPG.World.Domain;
using ELifeRPG.World.Domain.Exceptions;
using ELifeRPG.World.Domain.Inventory;
using ELifeRPG.World.Domain.Items;
using ELifeRPG.World.Domain.Snapshots;
using ELifeRPG.World.Infrastructure.Common;
using Marten;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.World.IntegrationTests;

/// <summary>
/// Requires the local infra stack (`docker compose up -d postgres`). There is no HTTP-level test
/// harness in this repo (see every other *.IntegrationTests project's TestServices), so this covers
/// the same path the `GET /api/inventory/limits` endpoint dispatches through
/// (<c>WorldSettingsQuery</c>) plus the domain constants it composes alongside the settings, and the
/// same path `PATCH /api/inventory/limits` dispatches through (<c>UpdateWorldSettingsCommand</c>).
/// </summary>
public sealed class WorldSettingsTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public Task InitializeAsync()
    {
        _provider = TestServices.BuildProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task WorldSettingsQuery_WithNoStoredDocument_ReturnsThePhase1DefaultValues()
    {
        await using var scope = _provider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var settings = await mediator.Send(new WorldSettingsQuery());

        Assert.Equal(100, settings.MaxInstancesPerGrant);
        Assert.Equal(3600, settings.GroundItemTtlSeconds);
        Assert.Equal(50, settings.MaxPendingPageSize);
        Assert.Equal(3, settings.MaxDeliveryAttempts);
        // Task 3, fix round 1 item 5: a document written before this knob existed must read back with
        // this default rather than zero — see WorldSettings' own class doc comment on why every
        // property carries an initializer.
        Assert.Equal(86400, settings.BatchIdRetentionSeconds);
        // Task 4: the empty-payload guard's two halves. Both are required to trip it — a large sweep
        // alone is an honest mass-loss reconcile, and an empty payload alone is "the player logged out
        // naked". They are WorldSettings knobs rather than domain constants because the right numbers
        // are a deployment question (inventory sizes differ per ruleset), not a design one, and both
        // are published on GET /api/inventory/limits so the Bridge never has to discover them as a 422.
        Assert.Equal(25, settings.SuspiciousReconcileScopeRowsThreshold);
        Assert.Equal(3, settings.SuspiciousReconcileUpsertsThreshold);
        // Review round 1: the proportional arm. The near-empty arm alone is disarmed by three upserts
        // at any sweep size, so on its own it would let a mod naming three items wipe an inventory of
        // unbounded size — this is the test that actually scales with the size of what is at stake.
        Assert.Equal(90, settings.SuspiciousReconcileSweptPercentThreshold);
    }

    /// <summary>
    /// The other seven, so that all fifteen defaults are pinned rather than the eight that happened to
    /// be interesting to whichever task added them. Deliberately against a fresh in-memory instance and
    /// not through the query above: this asserts the property initializers themselves, which is what
    /// every deployment with no stored document runs on, and it stays true whatever the shared
    /// singleton row happens to hold while another test is mid-flight.
    /// </summary>
    [Fact]
    public void WorldSettings_PropertyInitializers_PinEveryRemainingDefault()
    {
        var settings = new WorldSettings();

        Assert.Equal(100, settings.MaxAcksPerBatch);
        Assert.Equal(32, settings.MaxChildrenPerAck);
        Assert.Equal(1000, settings.MaxUpsertsPerBatch);
        Assert.Equal(1000, settings.MaxDeletesPerBatch);
        Assert.Equal(1000, settings.MaxUnknownPrefabSightingsPerBatch);
        Assert.Equal(100, settings.MaxUnknownPrefabQueryPageSize);
        Assert.Equal(100_000, settings.MaxUnknownPrefabQueryOffset);
    }

    [Fact]
    public void StructuralDomainConstants_ComposedIntoTheLimitsResponse_MatchThePhase1Values()
    {
        // These are domain constants, not WorldSettings fields — see the phase 1 task brief's
        // Controller ruling. The limits endpoint (World.Api's WorldLimitsDto) reads them directly.
        Assert.Equal(6, ItemInstance.MaxContainerDepth);
        Assert.Equal(16, ItemAttributes.MaxKeys);
        Assert.Equal(64, ItemAttributes.MaxKeyLength);
        Assert.Equal(256, ItemAttributes.MaxValueLength);
        Assert.Equal(1_000_000_000_000_000L, ScopeCursor.MaxSequence);
    }

    /// <summary>
    /// docs/bridge.md promises an integrator that <c>GET /api/inventory/limits</c> publishes <b>every</b>
    /// cap the write path enforces, and that nothing on that list should ever appear as a literal in
    /// Bridge or mod code. That promise had already been broken twice before this test existed:
    /// <c>ItemAttributes.MaxKeyLength</c> was enforced and rejected as <c>AttributeLimit</c> but never
    /// published, and <c>ScopeCursor.MaxSequence</c> was enforced as <c>sequence_out_of_range</c> and had
    /// to be written into the doc as the very literal the doc forbids.
    ///
    /// Both slipped through because nothing ever constructed the full DTO — the settings tests asserted
    /// individual defaults, and a knob that was never wired into <see cref="WorldLimitsDto"/> was
    /// therefore invisible. This walks the sources instead of the destination, so the next one fails
    /// here rather than in an integrator's inbox.
    /// </summary>
    [Fact]
    public void WorldLimitsDto_PublishesEveryTunableKnobAndStructuralCap()
    {
        // Distinct per-knob values, not the defaults: three of the defaults are 1000 and two are 100, so
        // a pair of crossed wires in WorldLimitsDto.Create would read as correct against them.
        var settings = new WorldSettings();
        var probeValue = 7_001;
        foreach (var knob in TunableKnobs())
        {
            knob.SetValue(settings, probeValue++);
        }

        var published = WorldLimitsDto.Create(settings);
        var publishedProperties = typeof(WorldLimitsDto)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(x => x.Name);

        foreach (var knob in TunableKnobs())
        {
            Assert.True(
                publishedProperties.TryGetValue(knob.Name, out var published_),
                $"WorldSettings.{knob.Name} is enforced but not published on GET /api/inventory/limits. "
                + "Add it to WorldLimitsDto (and to docs/bridge.md's limits payload).");
            Assert.Equal(knob.GetValue(settings), published_!.GetValue(published));
        }

        foreach (var (constant, publishedName) in StructuralCaps())
        {
            Assert.True(
                publishedProperties.TryGetValue(publishedName, out var published_),
                $"{constant.DeclaringType!.Name}.{constant.Name} is a structural cap the write path "
                + $"enforces, but no WorldLimitsDto.{publishedName} publishes it.");
            Assert.Equal(constant.GetRawConstantValue(), published_!.GetValue(published));
        }
    }

    /// <summary>
    /// The write half of the same promise. A knob nobody can turn is not tunable, which is the whole
    /// reason <c>PATCH /api/inventory/limits</c> exists — so a knob that is published but has no way in,
    /// or has a way in with no bound on it, fails here.
    /// </summary>
    [Fact]
    public void EveryTunableKnob_IsSettableThroughTheUpdateCommandAndBounded()
    {
        var commandParameters = typeof(UpdateWorldSettingsCommand)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(x => x.Name);
        var requestParameters = typeof(UpdateWorldLimitsRequestDto)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(x => x.Name);

        foreach (var knob in TunableKnobs())
        {
            Assert.True(
                commandParameters.ContainsKey(knob.Name),
                $"WorldSettings.{knob.Name} cannot be set: UpdateWorldSettingsCommand has no {knob.Name}.");
            Assert.True(
                requestParameters.ContainsKey(knob.Name),
                $"WorldSettings.{knob.Name} cannot be set over HTTP: UpdateWorldLimitsRequestDto has no {knob.Name}.");
            Assert.True(
                UpdateWorldSettingsHandler.SettingBounds.ContainsKey(knob.Name),
                $"WorldSettings.{knob.Name} is settable but unbounded. Give it a range in "
                + "UpdateWorldSettingsHandler's bounds table — tasks 2-5 bounded every other "
                + "caller-controlled number on this module's write path.");

            // The default must itself be legal, or the first PATCH that touches any other knob would
            // read this one back through GetAsync and hand a value its own bounds reject.
            var (min, max) = UpdateWorldSettingsHandler.SettingBounds[knob.Name];
            var @default = (int)knob.GetValue(new WorldSettings())!;
            Assert.InRange(@default, min, max);
        }

        // ...and no bound for a knob that no longer exists.
        var knobNames = TunableKnobs().Select(x => x.Name).ToHashSet();
        Assert.All(UpdateWorldSettingsHandler.SettingBounds.Keys, name => Assert.Contains(name, knobNames));
    }

    /// <summary>
    /// Exercised against an in-memory repository rather than Postgres deliberately: this is where all
    /// of the handler's behaviour lives, and running it against the real store would mutate the shared
    /// settings singleton that every other World integration test reads its thresholds from.
    /// </summary>
    [Fact]
    public async Task UpdateWorldSettings_LeavesOmittedFieldsUnchanged()
    {
        var repository = new FixedWorldSettingsRepository(new WorldSettings { MaxAcksPerBatch = 42 });
        var handler = new UpdateWorldSettingsHandler(repository);

        var updated = await handler.Handle(
            new UpdateWorldSettingsCommand(SuspiciousReconcileScopeRowsThreshold: 60), CancellationToken.None);

        Assert.Equal(60, updated.SuspiciousReconcileScopeRowsThreshold);
        Assert.Equal(42, updated.MaxAcksPerBatch);
        Assert.Equal(3, updated.SuspiciousReconcileUpsertsThreshold);
        Assert.Equal(1, repository.UpsertCount);
    }

    [Theory]
    // The three reconcile-guard thresholds phase 2 settled three separate times on the grounds that
    // they are retunable — the values a deployment is most likely to actually change.
    [InlineData(nameof(WorldSettings.SuspiciousReconcileScopeRowsThreshold), 0)]
    [InlineData(nameof(WorldSettings.SuspiciousReconcileScopeRowsThreshold), 10_001)]
    [InlineData(nameof(WorldSettings.SuspiciousReconcileUpsertsThreshold), -1)]
    // A percentage above 100 can never fire, which silently disarms the proportional arm.
    [InlineData(nameof(WorldSettings.SuspiciousReconcileSweptPercentThreshold), 101)]
    [InlineData(nameof(WorldSettings.SuspiciousReconcileSweptPercentThreshold), 0)]
    // A batch cap large enough to hold an arbitrarily long transaction is not a cap.
    [InlineData(nameof(WorldSettings.MaxUpsertsPerBatch), 0)]
    [InlineData(nameof(WorldSettings.MaxUpsertsPerBatch), int.MaxValue)]
    [InlineData(nameof(WorldSettings.MaxUnknownPrefabQueryOffset), -1)]
    [InlineData(nameof(WorldSettings.MaxUnknownPrefabQueryOffset), int.MaxValue)]
    public async Task UpdateWorldSettings_WithAnOutOfRangeValue_RejectsAndWritesNothing(string setting, int value)
    {
        var repository = new FixedWorldSettingsRepository(new WorldSettings());
        var handler = new UpdateWorldSettingsHandler(repository);

        var exception = await Assert.ThrowsAsync<WorldSettingOutOfRangeException>(
            async () => await handler.Handle(CommandSetting(setting, value), CancellationToken.None));

        Assert.Equal(setting, exception.Setting);
        // Nothing is stored: validation runs over the whole request before the upsert, so a request
        // naming one good value and one bad one leaves the document untouched rather than half-applied.
        Assert.Equal(0, repository.UpsertCount);
    }

    /// <summary>
    /// The one test that touches the real store, because "the settings actually persist" is not
    /// provable anywhere else. It writes exactly one knob — <c>MaxUnknownPrefabQueryOffset</c>, which no
    /// other test in this suite reads — and hard-deletes the singleton afterwards, so the window in
    /// which a concurrently-running test could observe a stored document contains only default values
    /// for every knob any of them cares about.
    /// </summary>
    [Fact]
    public async Task WorldSettings_UpsertedThroughTheRepository_ReadsBackFromPostgres()
    {
        try
        {
            await using (var scope = _provider.CreateAsyncScope())
            {
                var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
                var updated = await mediator.Send(new UpdateWorldSettingsCommand(MaxUnknownPrefabQueryOffset: 123_456));
                Assert.Equal(123_456, updated.MaxUnknownPrefabQueryOffset);
            }

            await using (var scope = _provider.CreateAsyncScope())
            {
                var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
                var reloaded = await mediator.Send(new WorldSettingsQuery());

                Assert.Equal(123_456, reloaded.MaxUnknownPrefabQueryOffset);
                // A stored document written before some future knob existed must still read that knob
                // back at its initializer rather than at zero — WorldSettings' class doc comment's
                // load-bearing claim, now reachable for the first time because a document exists at all.
                Assert.Equal(100, reloaded.MaxInstancesPerGrant);
                Assert.Equal(25, reloaded.SuspiciousReconcileScopeRowsThreshold);
            }
        }
        finally
        {
            await using var scope = _provider.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IWorldStore>();
            await using var session = store.LightweightSession();
            session.Delete<WorldSettings>(WorldSettings.SingletonId);
            await session.SaveChangesAsync(CancellationToken.None);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IWorldStore>();
            await using var session = store.QuerySession();
            Assert.Null(await session.LoadAsync<WorldSettings>(WorldSettings.SingletonId, CancellationToken.None));
        }
    }

    /// <summary>Every operationally tunable knob: <see cref="WorldSettings"/>' own settable properties, minus its identity.</summary>
    private static IReadOnlyList<PropertyInfo> TunableKnobs() =>
        [.. typeof(WorldSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(x => x.CanWrite && x.Name != nameof(WorldSettings.Id))
            .OrderBy(x => x.Name, StringComparer.Ordinal)];

    /// <summary>
    /// The structural caps: public numeric constants on the types the write path validates against,
    /// paired with the name each is published under. Three of the four <c>ItemAttributes</c> ones are
    /// renamed on the wire (<c>MaxKeys</c> would be meaningless in a payload full of other maximums), so
    /// the mapping is explicit; everything else is published under its own name, and a new constant with
    /// no entry here fails the completeness test rather than passing silently.
    /// </summary>
    private static IReadOnlyList<(FieldInfo Constant, string PublishedName)> StructuralCaps()
    {
        var renames = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [$"{nameof(ItemAttributes)}.{nameof(ItemAttributes.MaxKeys)}"] = "MaxAttributeKeys",
            [$"{nameof(ItemAttributes)}.{nameof(ItemAttributes.MaxKeyLength)}"] = "MaxAttributeKeyLength",
            [$"{nameof(ItemAttributes)}.{nameof(ItemAttributes.MaxValueLength)}"] = "MaxAttributeValueLength",
        };

        return
        [
            .. new[] { typeof(ItemInstance), typeof(ItemAttributes), typeof(UnknownPrefabSighting), typeof(ScopeCursor) }
                .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.Static))
                .Where(x => x is { IsLiteral: true, IsInitOnly: false }
                    && (x.FieldType == typeof(int) || x.FieldType == typeof(long)))
                .Select(x => (
                    Constant: x,
                    PublishedName: renames.GetValueOrDefault($"{x.DeclaringType!.Name}.{x.Name}", x.Name)))
                .OrderBy(x => x.PublishedName, StringComparer.Ordinal),
        ];
    }

    private static UpdateWorldSettingsCommand CommandSetting(string setting, int value) => setting switch
    {
        nameof(WorldSettings.SuspiciousReconcileScopeRowsThreshold) =>
            new UpdateWorldSettingsCommand(SuspiciousReconcileScopeRowsThreshold: value),
        nameof(WorldSettings.SuspiciousReconcileUpsertsThreshold) =>
            new UpdateWorldSettingsCommand(SuspiciousReconcileUpsertsThreshold: value),
        nameof(WorldSettings.SuspiciousReconcileSweptPercentThreshold) =>
            new UpdateWorldSettingsCommand(SuspiciousReconcileSweptPercentThreshold: value),
        nameof(WorldSettings.MaxUpsertsPerBatch) => new UpdateWorldSettingsCommand(MaxUpsertsPerBatch: value),
        nameof(WorldSettings.MaxUnknownPrefabQueryOffset) =>
            new UpdateWorldSettingsCommand(MaxUnknownPrefabQueryOffset: value),
        _ => throw new ArgumentOutOfRangeException(nameof(setting), setting, "No command shape for this knob."),
    };
}
