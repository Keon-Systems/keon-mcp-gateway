using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Json.Schema;
using Microsoft.Extensions.Options;

namespace Keon.McpGateway.Contracts;

public sealed class SchemaOptions
{
    public string SchemaPath { get; set; } = string.Empty;
}

public sealed class SchemaRegistry
{
    private readonly JsonObject _rootSchema;

    public SchemaRegistry(IOptions<SchemaOptions> options)
    {
        _rootSchema = JsonNode.Parse(File.ReadAllText(options.Value.SchemaPath))!.AsObject();
    }

    public SchemaValidationResult ValidateDefinition(string definitionName, JsonNode? instance)
    {
        var defsNode = _rootSchema["$defs"]?.DeepClone();
        if (defsNode is null || defsNode[definitionName] is null)
        {
            return new SchemaValidationResult(false, $"Schema definition not found: {definitionName}");
        }

        var wrapper = new JsonObject
        {
            ["$schema"] = _rootSchema["$schema"]?.GetValue<string>(),
            ["$defs"] = defsNode,
            ["allOf"] = new JsonArray
            {
                new JsonObject
                {
                    ["$ref"] = $"#/$defs/{definitionName}"
                }
            }
        };

        var definition = JsonSchema.FromText(wrapper.ToJsonString(JsonSerializerHelper.JsonOptions));
        var results = definition.Evaluate(instance, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List
        });

        return results.IsValid
            ? new SchemaValidationResult(true, string.Empty)
            : new SchemaValidationResult(false, $"Schema validation failed for {definitionName}");
    }
}

public sealed record SchemaValidationResult(bool IsValid, string Message);

public static class JsonSerializerHelper
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null
    };

    public static JsonNode? ToNode<T>(T value)
        => JsonSerializer.SerializeToNode(value, JsonOptions);

    public static DeserializeResult<T> Deserialize<T>(JsonNode node)
    {
        try
        {
            var value = node.Deserialize<T>(JsonOptions);
            return value is null
                ? new DeserializeResult<T>(default, "Deserialization returned null.")
                : new DeserializeResult<T>(value, null);
        }
        catch (Exception ex)
        {
            return new DeserializeResult<T>(default, ex.Message);
        }
    }
}

public sealed record DeserializeResult<T>(T? Value, string? ErrorMessage);

public static class CorrelationIdHelper
{
    public static string ResolveOrCreate(string? correlationId)
        => string.IsNullOrWhiteSpace(correlationId) ? IdFactory.NewCorrelationId() : correlationId;
}

public static class Hashing
{
    public static string Sha256(JsonNode? node)
    {
        var canonical = node?.ToJsonString(JsonSerializerHelper.JsonOptions) ?? "null";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}

public static class IdFactory
{
    public static string NewReceiptId(string prefix)
        => $"{prefix}_{Guid.NewGuid():N}";

    public static string NewCorrelationId()
        => $"c{Guid.NewGuid():N}";
}

public sealed record ToolsListRequest(
    [property: JsonPropertyName("tenant_id")] string TenantId,
    [property: JsonPropertyName("actor_id")] string ActorId,
    [property: JsonPropertyName("correlation_id")] string? CorrelationId,
    [property: JsonPropertyName("include_schemas")] bool? IncludeSchemas);

public sealed record ToolsListResponse(
    [property: JsonPropertyName("correlation_id")] string CorrelationId,
    [property: JsonPropertyName("tools")] IReadOnlyList<ToolDefinition> Tools);

public sealed record ToolsInvokeRequest(
    [property: JsonPropertyName("tenant_id")] string TenantId,
    [property: JsonPropertyName("actor_id")] string ActorId,
    [property: JsonPropertyName("correlation_id")] string CorrelationId,
    [property: JsonPropertyName("idempotency_key")] string? IdempotencyKey,
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("arguments")] JsonObject Arguments);

public sealed record ToolDefinition(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("required_scopes")] IReadOnlyList<string> RequiredScopes,
    [property: JsonPropertyName("risk_level")] string RiskLevel,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonPropertyName("input_schema")] JsonObject? InputSchema,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonPropertyName("output_schema")] JsonObject? OutputSchema);

public sealed record DecisionEnvelope(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("policy_hash")] string PolicyHash,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonPropertyName("policy_id")] string? PolicyId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonPropertyName("policy_version")] string? PolicyVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonPropertyName("reason_code")] string? ReasonCode);

public sealed record ReceiptRefs(
    [property: JsonPropertyName("directive")] string? Directive,
    [property: JsonPropertyName("intent")] string? Intent,
    [property: JsonPropertyName("request")] string? Request,
    [property: JsonPropertyName("decision")] string? Decision,
    [property: JsonPropertyName("execution")] string? Execution,
    [property: JsonPropertyName("outcome")] string? Outcome,
    [property: JsonPropertyName("evidence_pack")] string? EvidencePack);

public sealed record McpSuccessResponse(
    [property: JsonPropertyName("correlation_id")] string CorrelationId,
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("decision")] DecisionEnvelope Decision,
    [property: JsonPropertyName("result")] JsonObject? Result,
    [property: JsonPropertyName("receipts")] ReceiptRefs Receipts);

public sealed record McpErrorEnvelope(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("http_status")] int HttpStatus,
    [property: JsonPropertyName("retryable")] bool Retryable);

public sealed record McpErrorResponse(
    [property: JsonPropertyName("correlation_id")] string CorrelationId,
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("error")] McpErrorEnvelope Error,
    [property: JsonPropertyName("receipts")] ReceiptRefs Receipts);

public static class McpResults
{
    public static McpErrorResponse Error(string correlationId, string tool, string code, string message, int statusCode, bool retryable, string? directiveReceipt = null)
        => new(
            correlationId,
            tool,
            false,
            new McpErrorEnvelope(code, message, statusCode, retryable),
            new ReceiptRefs(directiveReceipt, null, null, null, null, null, null));
}

public sealed record DirectiveReceipt(string ReceiptId, string CorrelationId, string TenantId, string ActorId, string Tool, string ArgsHash, DateTimeOffset ReceivedAtUtc);

public sealed record IntentReceipt(string ReceiptId, string CorrelationId, string IntentType, string? ResourceScope, string? Purpose, JsonObject Constraints, DateTimeOffset ReceivedAtUtc);

public sealed record RuntimeDecisionRecord(string ReceiptId, string Status, string PolicyHash, string? PolicyId, string? PolicyVersion, string? ReasonCode);

public sealed record RuntimeExecutionRecord(string ReceiptId, JsonObject Result);

public sealed record AuthSuccess(System.Security.Claims.ClaimsPrincipal Principal);

public sealed record AuthResult(AuthSuccess? Principal, McpErrorResponse? Error);
