using Keon.McpGateway.Contracts;
using Keon.McpGateway.Runtime;

namespace Keon.McpGateway.Tools;

public sealed class GovernedExecuteHandler : IToolHandler
{
    private readonly RuntimeClient _runtimeClient;
    private readonly ToolRegistryAccessor _registryAccessor;

    public GovernedExecuteHandler(RuntimeClient runtimeClient, ToolRegistryAccessor registryAccessor)
    {
        _runtimeClient = runtimeClient;
        _registryAccessor = registryAccessor;
    }

    public string Name => "keon.governed.execute.v1";

    public IReadOnlyList<string> RequiredScopes => ["keon:mcp:invoke", "keon:execute"];

    public ToolDefinition Definition(bool includeSchemas)
        => new(
            Name,
            "1.0.0",
            "Universal governed execution adapter (Decide -> optional Execute). Emits Directive/Intent/Decision/Outcome receipts.",
            RequiredScopes,
            "high",
            includeSchemas ? _registryAccessor.Registry.GetDefinition("GovernedExecuteInputSchema") : null,
            includeSchemas ? _registryAccessor.Registry.GetDefinition("GovernedExecuteOutputSchema") : null);

    public async Task<ToolInvocationResult> InvokeAsync(ToolInvocationContext context, CancellationToken ct)
    {
        var args = context.Request.Arguments;
        var mode = args["mode"]?.GetValue<string>() ?? "decide_then_execute";
        var purpose = args["purpose"]?.GetValue<string>();
        var action = args["action"]?.GetValue<string>() ?? "execute";
        var resource = args["resource"]?.AsObject() ?? [];
        var parameters = args["params"]?.AsObject() ?? [];
        var resourceType = resource["type"]?.GetValue<string>() ?? "unknown";
        var resourceId = resource["id"]?.GetValue<string>();
        var resourceScope = resource["scope"]?.GetValue<string>();
        var requestReceipt = mode == "decide_then_execute" ? IdFactory.NewReceiptId("rcpt_req") : null;

        RuntimeDecisionRecord decision;
        try
        {
            decision = await _runtimeClient.DecideAsync(new RuntimeDecideRequest(
                context.Request.TenantId,
                context.Request.ActorId,
                context.Request.CorrelationId,
                action,
                resourceType,
                resourceId,
                resourceScope,
                purpose,
                parameters), ct);
        }
        catch (RuntimeClientException ex)
        {
            return new ToolInvocationResult(ex.StatusCode, McpResults.Error(context.Request.CorrelationId, Name, "RUNTIME_DECIDE_FAILED", ex.Message, ex.StatusCode, ex.Retryable, context.Directive.ReceiptId));
        }

        if (string.IsNullOrWhiteSpace(decision.PolicyHash))
        {
            return new ToolInvocationResult(StatusCodes.Status500InternalServerError, McpResults.Error(context.Request.CorrelationId, Name, "GOVERNANCE_FAIL_CLOSED", "PolicyHash missing from runtime decision response.", StatusCodes.Status500InternalServerError, false, context.Directive.ReceiptId));
        }

        var outcomeReceipt = IdFactory.NewReceiptId("rcpt_out");
        var receipts = new ReceiptRefs(context.Directive.ReceiptId, context.Intent.ReceiptId, requestReceipt, decision.ReceiptId, null, outcomeReceipt, null);

        if (decision.Status == "denied")
        {
            return new ToolInvocationResult(StatusCodes.Status200OK, new McpSuccessResponse(context.Request.CorrelationId, Name, true, new DecisionEnvelope("denied", decision.PolicyHash, decision.PolicyId, decision.PolicyVersion, decision.ReasonCode), null, receipts));
        }

        if (mode == "decide_only")
        {
            return new ToolInvocationResult(StatusCodes.Status200OK, new McpSuccessResponse(context.Request.CorrelationId, Name, true, new DecisionEnvelope("approved", decision.PolicyHash, decision.PolicyId, decision.PolicyVersion, decision.ReasonCode), null, receipts));
        }

        RuntimeExecutionRecord execution;
        try
        {
            execution = await _runtimeClient.ExecuteAsync(new RuntimeExecuteRequest(
                context.Request.TenantId,
                context.Request.ActorId,
                context.Request.CorrelationId,
                context.Request.IdempotencyKey,
                decision.ReceiptId,
                action,
                resourceType,
                resourceId,
                parameters), ct);
        }
        catch (RuntimeClientException ex)
        {
            return new ToolInvocationResult(ex.StatusCode, McpResults.Error(context.Request.CorrelationId, Name, "RUNTIME_EXECUTE_FAILED", ex.Message, ex.StatusCode, ex.Retryable, context.Directive.ReceiptId));
        }

        return new ToolInvocationResult(StatusCodes.Status200OK, new McpSuccessResponse(context.Request.CorrelationId, Name, true, new DecisionEnvelope("approved", decision.PolicyHash, decision.PolicyId, decision.PolicyVersion, decision.ReasonCode), execution.Result, receipts with { Execution = execution.ReceiptId }));
    }
}
