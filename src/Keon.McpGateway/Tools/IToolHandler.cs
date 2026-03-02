using System.Security.Claims;
using Keon.McpGateway.Contracts;

namespace Keon.McpGateway.Tools;

public sealed record ToolInvocationContext(ToolsInvokeRequest Request, ClaimsPrincipal Principal, DirectiveReceipt Directive, IntentReceipt Intent);

public sealed record ToolInvocationResult(int StatusCode, object Body);

public interface IToolHandler
{
    string Name { get; }
    IReadOnlyList<string> RequiredScopes { get; }
    ToolDefinition Definition(bool includeSchemas);
    Task<ToolInvocationResult> InvokeAsync(ToolInvocationContext context, CancellationToken ct);
}
