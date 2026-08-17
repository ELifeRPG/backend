using System.Reflection;
using System.Text.Json.Serialization.Metadata;
using Marten;

namespace ELifeRPG.Shared.Infrastructure;

public static class MartenPrivateSetterExtensions
{
    /// <summary>
    /// Lets Marten's System.Text.Json serializer populate `{ get; private set; }` aggregate
    /// properties on load, so event-sourced domain models don't need a [JsonInclude] on every
    /// property just to satisfy document (de)serialization. Marten's own StoreOptions.Serializer
    /// NonPublicMembersStorage setting looks purpose-built for this but is dead code as of Marten
    /// 9.23.0 (verified by decompiling Marten.dll — the property is stored but never read by
    /// SerializerFactory), hence doing it directly via a JsonTypeInfo modifier.
    /// </summary>
    public static void UseSystemTextJsonWithPrivateSetters(this StoreOptions options)
        => options.UseSystemTextJsonForSerialization(configure: o => o.TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { AllowPrivateSetters },
        });

    private static void AllowPrivateSetters(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        foreach (var property in typeInfo.Properties)
        {
            if (property.Set is not null || property.AttributeProvider is not PropertyInfo { SetMethod: { } setMethod })
            {
                continue;
            }

            property.Set = (obj, value) => setMethod.Invoke(obj, [value]);
        }
    }
}
