using System.Security.Claims;
using Keon.McpGateway.Contracts;

namespace Keon.McpGateway.Spine;

public sealed class DirectiveFactory
{
    public DirectiveReceipt Create(ToolsInvokeRequest request, ClaimsPrincipal principal)
    {
        var actorId = principal.FindFirst("actor_id")?.Value ?? principal.FindFirst("sub")?.Value ?? request.ActorId;
        var tenantId = principal.FindFirst("tenant_id")?.Value ?? request.TenantId;
        return new DirectiveReceipt(IdFactory.NewReceiptId("rcpt_dir"), request.CorrelationId, tenantId, actorId, request.Tool, Hashing.Sha256(request.Arguments), DateTimeOffset.UtcNow);
    }
}
