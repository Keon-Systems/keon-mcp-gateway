using Keon.McpGateway.Contracts;

namespace Keon.McpGateway.ControlPlane;

public sealed class UsageEventOutbox
{
    private readonly ControlPlaneClient _controlPlaneClient;

    public UsageEventOutbox(ControlPlaneClient controlPlaneClient)
    {
        _controlPlaneClient = controlPlaneClient;
    }

    public async Task<bool> EnqueueAsync(
        ToolsInvokeRequest request,
        McpSuccessResponse response,
        GatewayApiKeySnapshot apiKey,
        CancellationToken ct)
    {
        var decision = NormalizeDecision(response.Decision.Status);
        var usage = new UsageEventRequest(
            TenantId: apiKey.TenantId,
            ProjectId: apiKey.ProjectId,
            EnvironmentId: apiKey.EnvironmentId,
            ApiKeyId: apiKey.ApiKeyId,
            ExecutionId: response.Receipts.Execution ?? response.Receipts.Decision ?? request.CorrelationId,
            ReceiptId: response.Receipts.Decision ?? response.Receipts.Execution,
            IdempotencyKey: request.IdempotencyKey,
            Endpoint: "/mcp/tools/invoke",
            Decision: decision,
            BillableUnits: 1,
            Billable: string.Equals(decision, "AUTHORIZED", StringComparison.OrdinalIgnoreCase),
            OccurredAtUtc: DateTimeOffset.UtcNow);

        return await _controlPlaneClient.PostUsageEventAsync(usage, ct);
    }

    private static string NormalizeDecision(string status)
        => string.Equals(status, "approved", StringComparison.OrdinalIgnoreCase) ? "AUTHORIZED" :
           string.Equals(status, "denied", StringComparison.OrdinalIgnoreCase) ? "DENIED" :
           "FAILED_SYSTEM";
}
