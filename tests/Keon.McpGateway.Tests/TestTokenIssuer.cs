using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Keon.McpGateway.Tests;

internal sealed class TestTokenIssuer : IDisposable
{
    private readonly RSA _rsa = RSA.Create(2048);

    public string PublicKeyPem => ExportPublicKeyPem();

    public string CreateToken(string tenantId, string actorId, params string[] scopes)
    {
        var credentials = new SigningCredentials(new RsaSecurityKey(_rsa), SecurityAlgorithms.RsaSha256);
        var now = DateTimeOffset.UtcNow;
        var token = new JwtSecurityToken(
            issuer: "keon-auth",
            audience: "keon-mcp-gateway",
            claims:
            [
                new Claim("sub", actorId),
                new Claim("tenant_id", tenantId),
                new Claim("actor_id", actorId),
                new Claim("scope", string.Join(' ', scopes)),
                new Claim("jti", Guid.NewGuid().ToString("N"))
            ],
            notBefore: now.UtcDateTime,
            expires: now.AddMinutes(10).UtcDateTime,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private string ExportPublicKeyPem()
    {
        var key = _rsa.ExportSubjectPublicKeyInfoPem();
        return key;
    }

    public void Dispose()
        => _rsa.Dispose();
}
