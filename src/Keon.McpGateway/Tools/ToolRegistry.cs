using System.Text.Json.Nodes;
using Keon.McpGateway.Contracts;

namespace Keon.McpGateway.Tools;

public sealed class ToolRegistry
{
    private readonly Dictionary<string, IToolHandler> _handlers;
    private readonly JsonObject _gatewaySchema;
    private readonly JsonObject _hardeningSchema;

    public ToolRegistry(GovernedExecuteHandler governedExecuteHandler, LaunchHardeningHandler launchHardeningHandler, IWebHostEnvironment environment)
    {
        _handlers = new Dictionary<string, IToolHandler>(StringComparer.Ordinal)
        {
            [governedExecuteHandler.Name] = governedExecuteHandler,
            [launchHardeningHandler.Name] = launchHardeningHandler
        };

        _gatewaySchema = JsonNode.Parse(File.ReadAllText(SchemaPathResolver.ResolveGatewaySchema(environment.ContentRootPath)))!.AsObject();
        _hardeningSchema = JsonNode.Parse(File.ReadAllText(SchemaPathResolver.ResolveHardeningSchema(environment.ContentRootPath)))!.AsObject();
    }

    public IReadOnlyList<ToolDefinition> ListTools(bool includeSchemas)
        => _handlers.Values.Select(handler => handler.Definition(includeSchemas)).ToArray();

    public IToolHandler? Resolve(string toolName)
        => _handlers.GetValueOrDefault(toolName);

    public IReadOnlyList<string> GetRequiredScopes(string toolName)
        => Resolve(toolName)?.RequiredScopes ?? [];

    public JsonObject GetDefinition(string definitionName)
        => _gatewaySchema["$defs"]![definitionName]!.DeepClone().AsObject();

    public JsonObject GetHardeningSchema()
        => _hardeningSchema.DeepClone().AsObject();
}
