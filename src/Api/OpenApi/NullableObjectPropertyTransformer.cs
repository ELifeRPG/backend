using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ELifeRPG.Api.OpenApi;

/// <summary>
/// ASP.NET writes a nullable DTO property as <c>oneOf [null, $ref]</c>. Kiota cannot deserialize that
/// shape: with no discriminator, its composed-type wrapper never fills the DTO, so every field is
/// silently dropped. <c>anyOf [$ref, null]</c> is still nullable, and Kiota generates it as a plain
/// nullable model.
/// </summary>
internal sealed class NullableObjectPropertyTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        var properties = (document.Components?.Schemas?.Values ?? [])
            .SelectMany(schema => schema.Properties?.Values ?? [])
            .OfType<OpenApiSchema>();

        foreach (var property in properties.Where(IsNullableReference))
        {
            property.AnyOf = [.. property.OneOf!.OrderBy(IsNull)]; // reference first, then null
            property.OneOf = null;
        }

        return Task.CompletedTask;
    }

    private static bool IsNullableReference(OpenApiSchema property) =>
        property.OneOf is { Count: 2 } members
        && members.Any(IsNull)
        && members.Any(member => member is OpenApiSchemaReference);

    private static bool IsNull(IOpenApiSchema schema) => schema.Type == JsonSchemaType.Null;
}
