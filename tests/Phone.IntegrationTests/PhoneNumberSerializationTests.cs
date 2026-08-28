using System.Text;
using ELifeRPG.Phone.Domain.Devices;
using ELifeRPG.Phone.Infrastructure.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ELifeRPG.Phone.IntegrationTests;

/// <summary>
/// PhoneNumber is stored as a bare JSON string rather than as an object wrapping its backing field.
/// That shape used to be pinned by a [JsonConverter] attribute on the domain type; it is now a
/// converter that Phone.Infrastructure hands to Marten's serializer, so these assert through the
/// configured store rather than through a stock JsonSerializer.
///
/// Going through the store is the point: the attribute could not be forgotten, but the registration
/// can be. Without it System.Text.Json falls back to the struct's public surface and every number
/// silently round-trips to empty — so this covers the wiring, not just the converter.
///
/// Needs no database. Marten builds the store lazily and never connects to assemble a serializer.
/// </summary>
public sealed class PhoneNumberSerializationTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public Task InitializeAsync()
    {
        _provider = TestServices.BuildProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    private Marten.ISerializer Serializer() =>
        _provider.GetRequiredService<IPhoneStore>().Options.Serializer();

    [Fact]
    public void Number_SerialisesAsABareString()
        => Assert.Equal("\"44127788\"", Serializer().ToJson(PhoneNumber.Parse("44127788")));

    [Fact]
    public void Number_RoundTripsThroughTheStoresSerializer()
    {
        var serializer = Serializer();
        var json = serializer.ToJson(PhoneNumber.Parse("44127788"));

        Assert.Equal(PhoneNumber.Parse("44127788"), Deserialize(serializer, json));
    }

    [Fact]
    public void Number_DeserialisingNull_YieldsEmpty()
        => Assert.True(Deserialize(Serializer(), "null").IsEmpty);

    [Fact]
    public void EmptyNumber_SerialisesAsNull()
        => Assert.Equal("null", Serializer().ToJson(default(PhoneNumber)));

    private static PhoneNumber Deserialize(Marten.ISerializer serializer, string json)
        => serializer.FromJson<PhoneNumber>(new MemoryStream(Encoding.UTF8.GetBytes(json)));
}
