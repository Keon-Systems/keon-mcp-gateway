using System.Text.Json.Nodes;
using Keon.McpGateway.Contracts;

namespace Keon.McpGateway.Spine;

public sealed class InvokeTerminalWriter
{
    public async Task<TerminalWriteResult> FinalizeAsync(
        int statusCode,
        object body,
        string correlationId,
        string tool,
        DirectiveReceipt? directive,
        IngressSpineEmitter emitter,
        CancellationToken ct)
    {
        if (emitter.Mode == IngressSpineMode.Off)
        {
            return new TerminalWriteResult(statusCode, body);
        }

        var outcomeReceiptId = body switch
        {
            McpSuccessResponse success => success.Receipts.Outcome ?? IdFactory.NewReceiptId("rcpt_out"),
            _ => IdFactory.NewReceiptId("rcpt_out")
        };

        var payload = body switch
        {
            McpSuccessResponse success => OutcomeFactory.FromSuccess(success),
            McpErrorResponse error => OutcomeFactory.FromError(error),
            _ => new JsonObject
            {
                ["terminal_status"] = "error",
                ["tool"] = tool,
                ["ok"] = false
            }
        };

        try
        {
            await emitter.AppendOutcomeAsync(correlationId, outcomeReceiptId, payload, ct);
            return new TerminalWriteResult(statusCode, body);
        }
        catch (IngressSpineException ex)
        {
            var failClosed = McpResults.Error(
                correlationId,
                tool,
                "GOVERNANCE_FAIL_CLOSED",
                ex.Message,
                StatusCodes.Status500InternalServerError,
                false,
                directive?.ReceiptId);

            return new TerminalWriteResult(StatusCodes.Status500InternalServerError, failClosed);
        }
    }
}

public sealed record TerminalWriteResult(int StatusCode, object Body);
