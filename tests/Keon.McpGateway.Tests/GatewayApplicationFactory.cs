using Keon.McpGateway.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Keon.McpGateway.Tests;

internal sealed class GatewayApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _runtimeBaseUrl;
    private readonly string _publicKeyPem;

    public GatewayApplicationFactory(string runtimeBaseUrl, string publicKeyPem)
    {
        _runtimeBaseUrl = runtimeBaseUrl;
        _publicKeyPem = publicKeyPem;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var contentRoot = Path.Combine(repoRoot, "src", "Keon.McpGateway");
        builder.UseEnvironment("Development");
        builder.UseContentRoot(contentRoot);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Runtime:BaseUrl"] = _runtimeBaseUrl,
                ["Runtime:TimeoutSeconds"] = "3",
                ["Runtime:MaxRetries"] = "2",
                ["Auth:Issuer"] = "keon-auth",
                ["Auth:Audience"] = "keon-mcp-gateway",
                ["Auth:JwtPublicKeyPem"] = _publicKeyPem,
                ["Auth:JwksUrl"] = ""
            });
        });
    }
}
