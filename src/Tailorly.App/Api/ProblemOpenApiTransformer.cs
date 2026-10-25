using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Tailorly.App.Api;

/// <summary>
/// Publishes the RFC 7807 error body (ApiDesign §1.1) as the <c>ApiProblem</c> schema and adds it as the
/// <c>default</c> response of every operation, so the generated SPA client has a typed error.
/// </summary>
public sealed class ProblemOpenApiTransformer : IOpenApiDocumentTransformer
{
    public const string SchemaName = "ApiProblem";

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
        document.Components.Schemas[SchemaName] = new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Required = new HashSet<string> { "type", "title", "status", "code", "traceId" },
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["type"] = new OpenApiSchema { Type = JsonSchemaType.String },
                ["title"] = new OpenApiSchema { Type = JsonSchemaType.String },
                ["status"] = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32" },
                ["code"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "Stable error code (ApiDesign §10)" },
                ["detail"] = new OpenApiSchema { Type = JsonSchemaType.String },
                ["traceId"] = new OpenApiSchema { Type = JsonSchemaType.String },
                ["errors"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.Object,
                    AdditionalProperties = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Array,
                        Items = new OpenApiSchema { Type = JsonSchemaType.String },
                    },
                },
                ["extra"] = new OpenApiSchema { Type = JsonSchemaType.Object },
            },
        };

        foreach (var operation in document.Paths.Values.SelectMany(p => p.Operations?.Values ?? Enumerable.Empty<OpenApiOperation>()))
        {
            operation.Responses ??= new OpenApiResponses();
            operation.Responses.TryAdd("default", new OpenApiResponse
            {
                Description = "Error (RFC 7807)",
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/problem+json"] = new() { Schema = new OpenApiSchemaReference(SchemaName, document) },
                },
            });
        }

        return Task.CompletedTask;
    }
}
