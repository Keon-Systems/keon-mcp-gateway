using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Keon.McpGateway.ControlPlane;
using Keon.McpGateway.Contracts;

namespace Keon.McpGateway.Auth;

public sealed record ApiKeyAuthContext(ClaimsPrincipal Principal, GatewayApiKeySnapshot ApiKey, TenantEntitlementSnapshot Entitlements, GatewayEnvironmentState Environment);

public sealed class ApiKeyValidator
{
    private readonly ControlPlaneClient _controlPlaneClient;

    public ApiKeyValidator(ControlPlaneClient controlPlaneClient)
    {
        _controlPlaneClient = controlPlaneClient;
    }

    public async Task<(ApiKeyAuthContext? Context, McpErrorResponse? Error)> ValidateAsync(HttpContext httpContext, ToolsInvokeRequest request, CancellationToken ct)
    {
        var correlationId = CorrelationIdHelper.ResolveOrCreate(request.CorrelationId);
        var apiKeyHeader = httpContext.Request.Headers["X-Api-Key"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(apiKeyHeader))
        {
            return (null, McpResults.Error(correlationId, request.Tool, "MCP_AUTH_INVALID", "Missing X-Api-Key header.", StatusCodes.Status401Unauthorized, false));
        }

        var parts = apiKeyHeader.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4 || !string.Equals(parts[0], "keon", StringComparison.OrdinalIgnoreCase))
        {
            return (null, McpResults.Error(correlationId, request.Tool, "MCP_API_KEY_INVALID", "Invalid API key format.", StatusCodes.Status401Unauthorized, false));
        }

        var mode = parts[1];
        var prefix = parts[2];
        var secret = parts[3];

        GatewayApiKeySnapshot? apiKey;
        TenantEntitlementSnapshot? entitlements;
        GatewayEnvironmentState? environment;
        try
        {
            apiKey = await _controlPlaneClient.GetApiKeyAsync(prefix, ct);
            if (apiKey is null)
            {
                return (null, McpResults.Error(correlationId, request.Tool, "MCP_API_KEY_INVALID", "API key prefix was not found.", StatusCodes.Status401Unauthorized, false));
            }

            entitlements = await _controlPlaneClient.GetEntitlementsAsync(apiKey.TenantId, ct);
            environment = await _controlPlaneClient.GetEnvironmentAsync(apiKey.EnvironmentId, ct);
        }
        catch
        {
            return (null, McpResults.Error(correlationId, request.Tool, "GOVERNANCE_FAIL_CLOSED", "Unable to resolve gateway control-plane state.", StatusCodes.Status503ServiceUnavailable, true));
        }

        if (entitlements is null || environment is null || apiKey.Version != 1 || entitlements.Version <= 0 || environment.Version != 1)
        {
            return (null, McpResults.Error(correlationId, request.Tool, "GOVERNANCE_FAIL_CLOSED", "Gateway snapshot state is missing or versioned unexpectedly.", StatusCodes.Status503ServiceUnavailable, true));
        }

        if (!string.Equals(apiKey.Mode, mode, StringComparison.OrdinalIgnoreCase))
        {
            return (null, McpResults.Error(correlationId, request.Tool, "MCP_API_KEY_INVALID", "API key mode does not match prefix.", StatusCodes.Status401Unauthorized, false));
        }

        var computed = ComputeHash(apiKey.SecretSalt, secret);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(computed), Encoding.UTF8.GetBytes(apiKey.SecretHash)))
        {
            return (null, McpResults.Error(correlationId, request.Tool, "MCP_API_KEY_INVALID", "API key secret mismatch.", StatusCodes.Status401Unauthorized, false));
        }

        if (!string.Equals(apiKey.Status, "active", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(environment.Status, "active", StringComparison.OrdinalIgnoreCase))
        {
            return (null, McpResults.Error(correlationId, request.Tool, "MCP_KEY_REVOKED", "API key or environment is not active.", StatusCodes.Status403Forbidden, false));
        }

        if (!string.Equals(apiKey.TenantId, request.TenantId, StringComparison.Ordinal))
        {
            return (null, McpResults.Error(correlationId, request.Tool, "MCP_TENANT_MISMATCH", "API key tenant does not match request.", StatusCodes.Status403Forbidden, false));
        }

        if (!string.Equals(entitlements.BillingState, "active", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(entitlements.BillingState, "trialing", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(entitlements.BillingState, "past_due", StringComparison.OrdinalIgnoreCase))
        {
            return (null, McpResults.Error(correlationId, request.Tool, "MCP_TENANT_SUSPENDED", "Tenant billing state does not permit governed execution.", StatusCodes.Status403Forbidden, false));
        }

        if (entitlements.ConservativeRemaining <= 0)
        {
            return (null, McpResults.Error(correlationId, request.Tool, "MCP_QUOTA_EXHAUSTED", "Tenant governed execution quota is exhausted.", StatusCodes.Status402PaymentRequired, false));
        }

        var identity = new ClaimsIdentity(
        [
            new Claim("tenant_id", request.TenantId),
            new Claim("actor_id", request.ActorId),
            new Claim("auth_mode", "api_key"),
            new Claim("api_key_id", apiKey.ApiKeyId),
            new Claim("project_id", apiKey.ProjectId),
            new Claim("environment_id", apiKey.EnvironmentId)
        ], "ApiKey");

        return (new ApiKeyAuthContext(new ClaimsPrincipal(identity), apiKey, entitlements, environment), null);
    }

    private static string ComputeHash(string salt, string secret)
    {
        var bytes = Encoding.UTF8.GetBytes($"{salt}:{secret}");
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
