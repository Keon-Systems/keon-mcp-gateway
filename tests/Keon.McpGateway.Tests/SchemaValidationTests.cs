using System.Text.Json.Nodes;
using Json.Schema;

namespace Keon.McpGateway.Tests;

public sealed class SchemaValidationTests
{
    private readonly JsonObject _schemaRoot;
    private readonly string _fixturesRoot;

    public SchemaValidationTests()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        _schemaRoot = JsonNode.Parse(File.ReadAllText(Path.Combine(repoRoot, "contracts", "mcp_gateway.v1.schema.json")))!.AsObject();
        _fixturesRoot = Path.Combine(repoRoot, "tests", "Keon.McpGateway.Tests", "Fixtures");
    }

    [Theory]
    [InlineData("ToolsListRequest", "list-request.valid.json")]
    [InlineData("ToolsListResponse", "list-response.valid.json")]
    [InlineData("ToolsInvokeRequest", "invoke-request.valid.json")]
    [InlineData("ToolsInvokeResponse", "invoke-approved.valid.json")]
    [InlineData("ToolsInvokeResponse", "invoke-denied.valid.json")]
    [InlineData("ToolsInvokeResponse", "invoke-error.valid.json")]
    public void Fixture_validates_against_definition(string definitionName, string fixtureName)
    {
        var instance = JsonNode.Parse(File.ReadAllText(Path.Combine(_fixturesRoot, fixtureName)));
        var defs = _schemaRoot["$defs"]?.DeepClone();
        var wrapper = defs is null || defs[definitionName] is null
            ? null
            : new JsonObject
            {
                ["$schema"] = _schemaRoot["$schema"]?.GetValue<string>(),
                ["$defs"] = defs,
                ["allOf"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["$ref"] = $"#/$defs/{definitionName}"
                    }
                }
            };
        var definition = wrapper is null ? null : JsonSchema.FromText(wrapper.ToJsonString());

        Assert.NotNull(definition);
        var results = definition!.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(results.IsValid, $"Fixture {fixtureName} failed definition {definitionName}");
    }
}
