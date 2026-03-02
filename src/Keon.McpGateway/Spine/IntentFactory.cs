using System.Text.Json.Nodes;
using Keon.McpGateway.Contracts;

namespace Keon.McpGateway.Spine;

public sealed class IntentFactory
{
    public IntentReceipt Create(ToolsInvokeRequest request, DirectiveReceipt directive)
    {
        var action = request.Arguments["action"]?.GetValue<string>();
        var intentType = request.Tool switch
        {
            "keon.governed.execute.v1" => $"governed.{action ?? "execute"}",
            "keon.launch.hardening.v1" => "launch.hardening.attest",
            _ => "unknown"
        };

        return new IntentReceipt(
            IdFactory.NewReceiptId("rcpt_int"),
            directive.CorrelationId,
            intentType,
            request.Arguments["resource"]?["scope"]?.GetValue<string>(),
            request.Arguments["purpose"]?.GetValue<string>(),
            new JsonObject
            {
                ["tool"] = request.Tool,
                ["idempotency_key"] = request.IdempotencyKey
            },
            DateTimeOffset.UtcNow);
    }
}
