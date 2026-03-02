using Keon.McpGateway.Spine;
using Keon.McpGateway.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Keon.McpGateway.Tests;

internal sealed class GatewayApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _runtimeBaseUrl;
    private readonly string _publicKeyPem;
    private readonly IngressSpineMode _ingressSpineMode;
    private readonly string _ingressConnectionString;
    private readonly IIngressSpineSink? _sinkOverride;

    public GatewayApplicationFactory(string runtimeBaseUrl, string publicKeyPem, IngressSpineMode ingressSpineMode = IngressSpineMode.Off, string? ingressConnectionString = null, IIngressSpineSink? sinkOverride = null)
    {
        _runtimeBaseUrl = runtimeBaseUrl;
        _publicKeyPem = publicKeyPem;
        _ingressSpineMode = ingressSpineMode;
        _ingressConnectionString = ingressConnectionString ?? "Data Source=:memory:";
        _sinkOverride = sinkOverride;
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
                ["IngressSpine:Mode"] = _ingressSpineMode.ToString(),
                ["IngressSpine:ConnectionString"] = _ingressConnectionString,
                ["Auth:Issuer"] = "keon-auth",
                ["Auth:Audience"] = "keon-mcp-gateway",
                ["Auth:JwtPublicKeyPem"] = _publicKeyPem,
                ["Auth:JwksUrl"] = ""
            });
        });

        if (_sinkOverride is not null)
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton(_sinkOverride);
                services.AddSingleton<IIngressSpineSink>(_sinkOverride);
            });
        }
    }
}
