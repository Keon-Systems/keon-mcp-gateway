using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Keon.McpGateway.Contracts;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Keon.McpGateway.Auth;

public sealed class AuthOptions
{
    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string? JwksUrl { get; set; }
    public string? JwtPublicKeyPem { get; set; }
    public string[] RequiredScopes { get; set; } = [];
}

public sealed class JwtValidator
{
    private readonly AuthOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;

    public JwtValidator(IOptions<AuthOptions> options, IHttpClientFactory httpClientFactory)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<AuthResult> ValidateAsync(HttpContext context, string tenantId, string actorId, IReadOnlyCollection<string> toolScopes, CancellationToken ct)
    {
        var correlationId = CorrelationIdHelper.ResolveOrCreate(context.Request.Headers["X-Correlation-Id"].FirstOrDefault());
        var requestTool = context.Items["tool"]?.ToString() ?? "keon.governed.execute.v1";

        if (!context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return new AuthResult(null, McpResults.Error(correlationId, requestTool, "MCP_AUTH_INVALID", "Missing bearer token.", StatusCodes.Status401Unauthorized, false));
        }

        var token = context.Request.Headers.Authorization.ToString()["Bearer ".Length..].Trim();

        ClaimsPrincipal principal;
        try
        {
            principal = await ValidateTokenAsync(token, ct);
        }
        catch (Exception ex)
        {
            return new AuthResult(null, McpResults.Error(correlationId, requestTool, "MCP_AUTH_INVALID", ex.Message, StatusCodes.Status401Unauthorized, false));
        }

        var claimTenantId = principal.FindFirstValue("tenant_id");
        var claimActorId = principal.FindFirstValue("actor_id") ?? principal.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(claimTenantId) || string.IsNullOrWhiteSpace(claimActorId))
        {
            return new AuthResult(null, McpResults.Error(correlationId, requestTool, "MCP_TENANT_MISMATCH", "Missing tenant_id or actor_id claim binding.", StatusCodes.Status403Forbidden, false));
        }

        if (!string.Equals(claimTenantId, tenantId, StringComparison.Ordinal) || !string.Equals(claimActorId, actorId, StringComparison.Ordinal))
        {
            return new AuthResult(null, McpResults.Error(correlationId, requestTool, "MCP_TENANT_MISMATCH", "Token tenant_id or actor_id does not match request.", StatusCodes.Status403Forbidden, false));
        }

        var scopes = (principal.FindFirstValue("scope") ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var requiredScope in _options.RequiredScopes.Concat(toolScopes))
        {
            if (!scopes.Contains(requiredScope))
            {
                return new AuthResult(null, McpResults.Error(correlationId, requestTool, "MCP_SCOPE_DENIED", $"Missing required scope: {requiredScope}", StatusCodes.Status403Forbidden, false));
            }
        }

        return new AuthResult(new AuthSuccess(principal), null);
    }

    private async Task<ClaimsPrincipal> ValidateTokenAsync(string token, CancellationToken ct)
    {
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _options.Issuer,
            ValidateAudience = true,
            ValidAudience = _options.Audience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = await ResolveSigningKeysAsync(ct)
        };

        var handler = new JwtSecurityTokenHandler();
        return handler.ValidateToken(token, parameters, out _);
    }

    private async Task<IReadOnlyCollection<SecurityKey>> ResolveSigningKeysAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_options.JwtPublicKeyPem))
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(_options.JwtPublicKeyPem);
            return [new RsaSecurityKey(rsa.ExportParameters(false))];
        }

        if (string.IsNullOrWhiteSpace(_options.JwksUrl))
        {
            throw new InvalidOperationException("Auth.JwksUrl or Auth.JwtPublicKeyPem must be configured.");
        }

        var client = _httpClientFactory.CreateClient(nameof(JwtValidator));
        var jwksJson = await client.GetStringAsync(_options.JwksUrl, ct);
        return new JsonWebKeySet(jwksJson).Keys.Cast<SecurityKey>().ToArray();
    }
}
