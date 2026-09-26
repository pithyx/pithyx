using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Vyxis.Daemon.Errors;

namespace Vyxis.Daemon.Hosting;

/// <summary>
/// The OpenAPI document is the single source for the generated TypeScript client: endpoints, <see cref="ApiProblem"/>,
/// the <c>ErrorCode</c> union and, later, the WebSocket messages.
/// </summary>
public static class OpenApiSetup
{
    public const string DocumentPath = "/api/openapi.json";
    private const string ProblemMediaType = "application/problem+json";

    public static IServiceCollection AddVyxisOpenApi(this IServiceCollection services) =>
        services.AddOpenApi(options =>
        {
            options.AddSchemaTransformer(MarkPropertiesRequired);
            options.AddDocumentTransformer(AddContractsAsync);
        });

    // The daemon always serializes every property, so every property is present in responses.
    private static Task MarkPropertiesRequired(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.JsonTypeInfo.Kind == JsonTypeInfoKind.Object && schema.Properties is { Count: > 0 } properties)
        {
            schema.Required = new HashSet<string>(properties.Keys, StringComparer.Ordinal);
        }
        return Task.CompletedTask;
    }

    private static async Task AddContractsAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info = new OpenApiInfo { Title = "Vyxis API", Version = "v1" };
        document.Servers = [];
        document.Components ??= new OpenApiComponents();
        document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal);

        document.Components.Schemas["ErrorCode"] = new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            Enum = [.. ErrorCodes.All.Keys.Order(StringComparer.Ordinal).Select(code => (JsonNode)JsonValue.Create(code))],
        };

        var problem = await context.GetOrCreateSchemaAsync(typeof(ApiProblem), cancellationToken: cancellationToken);
        problem.Properties!["code"] = new OpenApiSchemaReference("ErrorCode", document);
        problem.Properties["params"] = new OpenApiSchema { Type = JsonSchemaType.Object, AdditionalProperties = new OpenApiSchema() };
        document.Components.Schemas["ApiProblem"] = problem;

        foreach (var path in document.Paths.Values)
        {
            if (path.Operations is null)
            {
                continue;
            }
            foreach (var operation in path.Operations.Values)
            {
                operation.Responses ??= [];
                operation.Responses["default"] = new OpenApiResponse
                {
                    Description = "Problem details with a stable error code.",
                    Content = new Dictionary<string, OpenApiMediaType>(StringComparer.Ordinal)
                    {
                        [ProblemMediaType] = new OpenApiMediaType { Schema = new OpenApiSchemaReference("ApiProblem", document) },
                    },
                };
            }
        }
    }
}
