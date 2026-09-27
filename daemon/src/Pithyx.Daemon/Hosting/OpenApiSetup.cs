using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Realtime;

namespace Pithyx.Daemon.Hosting;

/// <summary>
/// The OpenAPI document is the single source for the generated TypeScript client: endpoints, <see cref="ApiProblem"/>,
/// the <c>ErrorCode</c> union and, later, the WebSocket messages.
/// </summary>
public static class OpenApiSetup
{
    public const string DocumentPath = "/api/openapi.json";
    private const string ProblemMediaType = "application/problem+json";

    public static IServiceCollection AddPithyxOpenApi(this IServiceCollection services) =>
        services.AddOpenApi(options =>
        {
            options.AddSchemaTransformer(MarkPropertiesRequired);
            options.AddDocumentTransformer(AddContractsAsync);
        });

    // The daemon always serializes every property, so responses contain all of them (nullable ones as null).
    // In requests, constructor parameters with a default value may be left out.
    private static Task MarkPropertiesRequired(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.JsonTypeInfo.Kind == JsonTypeInfoKind.Object && schema.Properties is { Count: > 0 })
        {
            schema.Required = new HashSet<string>(
                context.JsonTypeInfo.Properties
                    .Where(property => property.AssociatedParameter is not { HasDefaultValue: true })
                    .Select(property => property.Name),
                StringComparer.Ordinal);
        }
        return Task.CompletedTask;
    }

    private static async Task AddContractsAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info = new OpenApiInfo { Title = "Pithyx API", Version = "v1" };
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

        await AddRealtimeContractsAsync(document, context, cancellationToken);

        // A failed job carries an error code and params, typed like those of a problem.
        if (document.Components.Schemas.TryGetValue("Job", out var jobSchema) && jobSchema.Properties is { } job)
        {
            job["errorCode"] = new OpenApiSchema
            {
                OneOf = [new OpenApiSchema { Type = JsonSchemaType.Null }, new OpenApiSchemaReference("ErrorCode", document)],
                Description = job["errorCode"].Description,
            };
            job["errorParams"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Null | JsonSchemaType.Object,
                AdditionalProperties = new OpenApiSchema(),
                Description = job["errorParams"].Description,
            };
        }

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

    // The WebSocket protocol is not an HTTP operation, so its messages and the data type of every topic are added as
    // components: ServerMessage (discriminated by type), ClientMessage, TopicSnapshots and TopicEvents.
    private static async Task AddRealtimeContractsAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        var schemas = document.Components!.Schemas!;

        async Task<IOpenApiSchema> ComponentAsync(string name, Type type, string? typeValue = null)
        {
            if (!schemas.ContainsKey(name))
            {
                var schema = await context.GetOrCreateSchemaAsync(type, cancellationToken: cancellationToken);
                if (typeValue is not null)
                {
                    schema.Properties!["type"] = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create(typeValue)] };
                }
                schemas[name] = schema;
            }
            return new OpenApiSchemaReference(name, document);
        }

        var client = await context.GetOrCreateSchemaAsync(typeof(ClientMessage), cancellationToken: cancellationToken);
        client.Properties!["type"] = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create("sub"), JsonValue.Create("unsub")] };
        schemas["ClientMessage"] = client;

        var variants = new Dictionary<string, (string Name, Type Type)>(StringComparer.Ordinal)
        {
            ["ack"] = ("AckMessage", typeof(AckMessage)),
            ["snap"] = ("SnapshotMessage", typeof(SnapshotMessage)),
            ["event"] = ("EventMessage", typeof(EventMessage)),
            ["error"] = ("ErrorMessage", typeof(ErrorMessage)),
        };
        var mapping = new Dictionary<string, OpenApiSchemaReference>(StringComparer.Ordinal);
        var oneOf = new List<IOpenApiSchema>();
        foreach (var (typeValue, (name, type)) in variants)
        {
            var reference = (OpenApiSchemaReference)await ComponentAsync(name, type, typeValue);
            mapping[typeValue] = reference;
            oneOf.Add(reference);
        }
        var error = (OpenApiSchema)schemas["ErrorMessage"];
        error.Properties!["code"] = new OpenApiSchemaReference("ErrorCode", document);
        error.Properties["params"] = new OpenApiSchema { Type = JsonSchemaType.Object, AdditionalProperties = new OpenApiSchema() };
        schemas["ServerMessage"] = new OpenApiSchema
        {
            OneOf = oneOf,
            Discriminator = new OpenApiDiscriminator { PropertyName = "type", Mapping = mapping },
        };

        var closeCodes = Enum.GetValues<WebSocketCloseCode>();
        schemas["WebSocketCloseCode"] = new OpenApiSchema
        {
            Type = JsonSchemaType.Integer,
            Enum = [.. closeCodes.Select(code => (JsonNode)JsonValue.Create((int)code))],
            Extensions = new Dictionary<string, IOpenApiExtension>(StringComparer.Ordinal)
            {
                ["x-enum-varnames"] = new JsonNodeExtension(new JsonArray([.. closeCodes.Select(code => (JsonNode)JsonValue.Create(code.ToString()))])),
            },
        };

        var registry = context.ApplicationServices.GetRequiredService<TopicRegistry>();
        var snapshots = new OpenApiSchema { Type = JsonSchemaType.Object, Properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal) };
        var events = new OpenApiSchema { Type = JsonSchemaType.Object, Properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal) };
        foreach (var definition in registry.All)
        {
            snapshots.Properties[definition.Pattern] = await ComponentAsync(definition.SnapshotType.Name, definition.SnapshotType);
            events.Properties[definition.Pattern] = await ComponentAsync(definition.EventType.Name, definition.EventType);
        }
        snapshots.Required = new HashSet<string>(snapshots.Properties.Keys, StringComparer.Ordinal);
        events.Required = new HashSet<string>(events.Properties.Keys, StringComparer.Ordinal);
        schemas["TopicSnapshots"] = snapshots;
        schemas["TopicEvents"] = events;
    }
}
