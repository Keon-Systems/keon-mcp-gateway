using System.Text.Json.Nodes;
using Keon.McpGateway.Contracts;
using Keon.McpGateway.Runtime;

namespace Keon.McpGateway.Tools;

public sealed class LaunchHardeningHandler : IToolHandler
{
    private readonly RuntimeClient _runtimeClient;
    private readonly ToolRegistryAccessor _registryAccessor;

    public LaunchHardeningHandler(RuntimeClient runtimeClient, ToolRegistryAccessor registryAccessor)
    {
        _runtimeClient = runtimeClient;
        _registryAccessor = registryAccessor;
    }

    public string Name => "keon.launch.hardening.v1";

    public IReadOnlyList<string> RequiredScopes => ["keon:mcp:invoke", "keon:attest"];

    public ToolDefinition Definition(bool includeSchemas)
        => new(
            Name,
            "1.0.0",
            "Launch hardening attestation tool (governed).",
            RequiredScopes,
            "high",
            includeSchemas ? _registryAccessor.Registry.GetHardeningSchema() : null,
            includeSchemas ? new JsonObject { ["type"] = "object", ["additionalProperties"] = true } : null);

    public async Task<ToolInvocationResult> InvokeAsync(ToolInvocationContext context, CancellationToken ct)
    {
        RuntimeDecisionRecord decision;
        try
        {
            decision = await _runtimeClient.DecideAsync(new RuntimeDecideRequest(
                context.Request.TenantId,
                context.Request.ActorId,
                context.Request.CorrelationId,
                "execute",
                "launch_hardening",
                null,
                "launch:hardening",
                context.Request.Arguments["submitted_by"]?.GetValue<string>() ?? "launch-hardening-attestation",
                context.Request.Arguments), ct);
        }
        catch (RuntimeClientException ex)
        {
            return new ToolInvocationResult(ex.StatusCode, McpResults.Error(context.Request.CorrelationId, Name, "RUNTIME_DECIDE_FAILED", ex.Message, ex.StatusCode, ex.Retryable, context.Directive.ReceiptId));
        }

        var receipts = new ReceiptRefs(context.Directive.ReceiptId, context.Intent.ReceiptId, null, decision.ReceiptId, null, IdFactory.NewReceiptId("rcpt_out"), null);
        if (decision.Status == "denied")
        {
            return new ToolInvocationResult(StatusCodes.Status200OK, new McpSuccessResponse(context.Request.CorrelationId, Name, true, new DecisionEnvelope("denied", decision.PolicyHash, decision.PolicyId, decision.PolicyVersion, decision.ReasonCode), null, receipts));
        }

        return new ToolInvocationResult(StatusCodes.Status200OK, new McpSuccessResponse(context.Request.CorrelationId, Name, true, new DecisionEnvelope("approved", decision.PolicyHash, decision.PolicyId, decision.PolicyVersion, decision.ReasonCode), new JsonObject
        {
            ["status"] = "stubbed",
            ["message"] = "Launch hardening runtime wiring is deferred; decision path is active."
        }, receipts));
    }
}
